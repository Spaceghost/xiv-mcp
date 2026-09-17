namespace XivMcp.Plugin.Services;

/// <summary>Agent board state values (serialized lowercase).</summary>
public enum AgentState
{
    Running,
    Done,
    Failed,
    Info,
}

/// <summary>One agent's latest post. Immutable; an update replaces the entry.</summary>
public sealed record AgentPost(
    string Agent,
    string Status,
    AgentState State,
    double? Progress,
    string? Detail,
    string? ClientName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string StateName => State.ToString().ToLowerInvariant();
}

/// <summary>
/// In-memory progress board that AI agents write through post_status/clear_status and the plugin
/// window, Umbra widget (IPC) and ffxiv://agents resource read. Thread-safe; keyed by agent name
/// (case-insensitive); bounded in size; entries expire per configuration.
/// </summary>
public sealed class AgentBoard
{
    public const int MaxAgents = 64;
    public const int MaxAgentLength = 64;
    public const int MaxStatusLength = 200;
    public const int MaxDetailLength = 2000;

    private readonly Configuration config;
    private readonly object gate = new();
    private readonly Dictionary<string, AgentPost> posts = new(StringComparer.OrdinalIgnoreCase);

    public AgentBoard(Configuration config) => this.config = config;

    /// <summary>Raised (on the calling thread) after any change. Handlers must not throw.</summary>
    public event Action? Changed;

    /// <summary>Raised when a post transitions into done/failed (for notifications).</summary>
    public event Action<AgentPost>? Completed;

    public static AgentState ParseState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        null or "" or "running" => AgentState.Running,
        "done" => AgentState.Done,
        "failed" => AgentState.Failed,
        "info" => AgentState.Info,
        _ => throw new ArgumentException($"Unknown state '{state}'. Use running, done, failed or info."),
    };

    /// <summary>Creates or replaces the agent's post. Returns the stored post.</summary>
    public AgentPost Upsert(string agent, string status, AgentState state, double? progress, string? detail, string? clientName)
    {
        agent = Truncate(agent.Trim(), MaxAgentLength);
        if (agent.Length == 0)
            throw new ArgumentException("agent must not be empty.");
        status = Truncate(status.Trim(), MaxStatusLength);
        detail = string.IsNullOrWhiteSpace(detail) ? null : Truncate(detail.Trim(), MaxDetailLength);
        if (progress is { } p)
            progress = double.IsFinite(p) ? Math.Clamp(p, 0, 1) : null;

        var now = DateTimeOffset.UtcNow;
        AgentPost post;
        bool completed;
        lock (gate)
        {
            PruneLocked(now);
            posts.TryGetValue(agent, out var previous);
            post = new AgentPost(agent, status, state, progress, detail, clientName, previous?.CreatedAt ?? now, now);
            posts[agent] = post;
            completed = state is AgentState.Done or AgentState.Failed && previous?.State != state;

            if (posts.Count > MaxAgents)
            {
                var oldest = posts.Values.OrderBy(x => x.UpdatedAt).First();
                posts.Remove(oldest.Agent);
            }
        }

        Raise(Changed);
        if (completed)
        {
            try
            {
                Completed?.Invoke(post);
            }
            catch
            {
                // Notification plumbing must never fail a post.
            }
        }

        return post;
    }

    /// <summary>Removes one agent (by name) or everything (null). Returns the number removed.</summary>
    public int Clear(string? agent)
    {
        int removed;
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(agent))
            {
                removed = posts.Count;
                posts.Clear();
            }
            else
            {
                removed = posts.Remove(agent.Trim()) ? 1 : 0;
            }
        }

        if (removed > 0)
            Raise(Changed);
        return removed;
    }

    /// <summary>Newest update first.</summary>
    public IReadOnlyList<AgentPost> Snapshot()
    {
        bool pruned;
        AgentPost[] result;
        lock (gate)
        {
            pruned = PruneLocked(DateTimeOffset.UtcNow);
            result = posts.Values.OrderByDescending(p => p.UpdatedAt).ToArray();
        }

        if (pruned)
            Raise(Changed);
        return result;
    }

    public int Count
    {
        get
        {
            lock (gate)
                return posts.Count;
        }
    }

    private bool PruneLocked(DateTimeOffset now)
    {
        var minutes = config.AgentBoardExpiryMinutes;
        if (minutes <= 0 || posts.Count == 0)
            return false;
        var cutoff = now - TimeSpan.FromMinutes(minutes);
        var stale = posts.Values.Where(p => p.UpdatedAt < cutoff).Select(p => p.Agent).ToList();
        foreach (var name in stale)
            posts.Remove(name);
        return stale.Count > 0;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch
        {
            // See Changed: subscribers are UI/IPC/notification plumbing.
        }
    }
}

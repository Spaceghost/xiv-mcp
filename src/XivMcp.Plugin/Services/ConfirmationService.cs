using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>One call waiting for the player to approve or deny it in game.</summary>
public sealed class PendingConfirmation
{
    internal PendingConfirmation(string toolName, ToolPermission permission, string? clientName, string? summary, TimeSpan timeout)
    {
        ToolName = toolName;
        Permission = permission;
        ClientName = clientName;
        Summary = summary;
        CreatedAt = DateTimeOffset.UtcNow;
        Deadline = CreatedAt + timeout;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string ToolName { get; }

    public ToolPermission Permission { get; }

    public string? ClientName { get; }

    /// <summary>Human-readable arguments (already truncated).</summary>
    public string? Summary { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset Deadline { get; }

    internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Gate for Action/Chat calls. A server thread awaits <see cref="RequestAsync"/>; the ImGui
/// confirmation window (framework thread, Draw loop) resolves it via <see cref="Resolve"/>.
/// Timeouts, cancellation and plugin unload all resolve to "denied" so nothing waits forever.
/// </summary>
public sealed class ConfirmationService : IDisposable
{
    public const int MaxSummaryLength = 600;

    private readonly Configuration config;
    private readonly object gate = new();
    private readonly List<PendingConfirmation> pending = [];
    private bool disposed;

    public ConfirmationService(Configuration config) => this.config = config;

    /// <summary>Raised (any thread) when a request is added or resolved.</summary>
    public event Action? Changed;

    public bool HasPending
    {
        get
        {
            lock (gate)
                return pending.Count > 0;
        }
    }

    public IReadOnlyList<PendingConfirmation> Snapshot()
    {
        lock (gate)
            return pending.ToArray();
    }

    /// <summary>
    /// Resolves true immediately when confirmations are off; otherwise waits for the player.
    /// Never throws for timeout/cancel/unload: those return false.
    /// </summary>
    public async Task<bool> RequestAsync(string toolName, ToolPermission permission, string? clientName, string? summary, CancellationToken cancellationToken)
    {
        if (!config.ConfirmActions || permission < ToolPermission.Action)
            return true;

        if (summary is { Length: > MaxSummaryLength })
            summary = summary[..MaxSummaryLength] + "…";

        var request = new PendingConfirmation(toolName, permission, clientName, summary, TimeSpan.FromSeconds(config.ConfirmTimeoutSeconds));
        lock (gate)
        {
            if (disposed)
                return false;
            pending.Add(request);
        }

        RaiseChanged();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Deadline - request.CreatedAt);
        await using (timeout.Token.Register(static state => ((PendingConfirmation)state!).Completion.TrySetResult(false), request))
        {
            try
            {
                return await request.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                    pending.Remove(request);
                RaiseChanged();
            }
        }
    }

    /// <summary>Called from the Draw loop when the player clicks Approve or Deny.</summary>
    public void Resolve(Guid id, bool approved)
    {
        PendingConfirmation? match;
        lock (gate)
            match = pending.FirstOrDefault(p => p.Id == id);
        match?.Completion.TrySetResult(approved);
    }

    public void DenyAll()
    {
        foreach (var request in Snapshot())
            request.Completion.TrySetResult(false);
    }

    public void Dispose()
    {
        lock (gate)
            disposed = true;
        DenyAll();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Subscribers are UI/IPC plumbing; never let them break a pending call.
        }
    }
}

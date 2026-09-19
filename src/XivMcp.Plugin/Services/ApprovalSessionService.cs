using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>Why an approval session ended.</summary>
public enum ApprovalSessionEnd
{
    Expired,
    Revoked,
    Replaced,
    PermissionsChanged,
    Unloaded,
}

/// <summary>
/// A sudo-like "allow everything from this client" window. Identity is the client-reported name plus, when the client
/// had one, its MCP session id; a session started for a stateless (2026-07-28) client matches on the name only.
/// </summary>
public sealed record ApprovalSession(Guid Id, string? ClientName, string? SessionId, bool IncludeChat, DateTimeOffset StartedAt, DateTimeOffset Expires)
{
    public TimeSpan Remaining(DateTimeOffset now) => Expires > now ? Expires - now : TimeSpan.Zero;

    public string Describe() =>
        $"{ClientName ?? "(unnamed client)"}{(SessionId is { Length: > 8 } s ? $" [session {s[..8]}]" : "")}{(IncludeChat ? " incl. Chat" : "")}";
}

/// <summary>
/// Time-bound approval sessions: while one covers a client, that client's Action calls (and Chat calls when the session
/// includes Chat) run without an in-game prompt, both through tools/call and through the approval queue. Sessions live in
/// memory only, so they never survive a plugin reload or a game restart. Thread-safe; events are raised outside the lock.
/// </summary>
public sealed class ApprovalSessionService : IDisposable
{
    public const int MinMinutes = 1;
    public const int MaxMinutes = 60;
    public const int DefaultMinutes = 5;

    private readonly Configuration config;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly List<ApprovalSession> sessions = [];
    private bool disposed;

    public ApprovalSessionService(Configuration config, TimeProvider? time = null)
    {
        this.config = config;
        this.time = time ?? TimeProvider.System;
    }

    public event Action<ApprovalSession>? Started;

    public event Action<ApprovalSession, ApprovalSessionEnd>? Ended;

    /// <summary>The configured session length, clamped to 1-60 minutes.</summary>
    public TimeSpan Duration => TimeSpan.FromMinutes(Math.Clamp(config.ApprovalSessionMinutes, MinMinutes, MaxMinutes));

    /// <summary>Unexpired sessions, soonest expiry first. Ends expired ones first.</summary>
    public IReadOnlyList<ApprovalSession> Active()
    {
        Tick();
        lock (gate)
            return sessions.OrderBy(s => s.Expires).ToArray();
    }

    /// <summary>
    /// Starts (or replaces) the session for this client identity. Chat-tier calls are covered only with
    /// <paramref name="includeChat"/>.
    /// </summary>
    public ApprovalSession Start(string? clientName, string? sessionId, bool includeChat)
    {
        var now = time.GetUtcNow();
        var session = new ApprovalSession(Guid.NewGuid(), clientName, sessionId, includeChat, now, now + Duration);
        ApprovalSession[] replaced;
        lock (gate)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ApprovalSessionService));
            replaced = sessions.Where(s => s.ClientName == clientName && s.SessionId == sessionId).ToArray();
            sessions.RemoveAll(s => s.ClientName == clientName && s.SessionId == sessionId);
            sessions.Add(session);
        }

        foreach (var old in replaced)
            Raise(() => Ended?.Invoke(old, ApprovalSessionEnd.Replaced));
        Raise(() => Started?.Invoke(session));
        return session;
    }

    /// <summary>True when an unexpired session covers an Action/Chat call of <paramref name="tier"/> from this identity.</summary>
    public bool Covers(string? clientName, string? sessionId, ToolPermission tier)
    {
        if (tier < ToolPermission.Action)
            return false;
        var now = time.GetUtcNow();
        lock (gate)
        {
            foreach (var s in sessions)
            {
                if (s.Expires > now
                    && string.Equals(s.ClientName, clientName, StringComparison.Ordinal)
                    && (s.SessionId is null || string.Equals(s.SessionId, sessionId, StringComparison.Ordinal))
                    && (tier != ToolPermission.Chat || s.IncludeChat))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Ends sessions whose time is up. Call about once a second.</summary>
    public void Tick()
    {
        var now = time.GetUtcNow();
        ApprovalSession[] expired;
        lock (gate)
        {
            if (sessions.Count == 0)
                return;
            expired = sessions.Where(s => s.Expires <= now).ToArray();
            if (expired.Length == 0)
                return;
            sessions.RemoveAll(s => s.Expires <= now);
        }

        foreach (var s in expired)
            Raise(() => Ended?.Invoke(s, ApprovalSessionEnd.Expired));
    }

    public bool Revoke(Guid id)
    {
        ApprovalSession? match;
        lock (gate)
        {
            match = sessions.FirstOrDefault(s => s.Id == id);
            if (match != null)
                sessions.Remove(match);
        }

        if (match == null)
            return false;
        Raise(() => Ended?.Invoke(match, ApprovalSessionEnd.Revoked));
        return true;
    }

    public void RevokeAll(ApprovalSessionEnd reason = ApprovalSessionEnd.Revoked)
    {
        ApprovalSession[] all;
        lock (gate)
        {
            all = sessions.ToArray();
            sessions.Clear();
        }

        foreach (var s in all)
            Raise(() => Ended?.Invoke(s, reason));
    }

    public void Dispose()
    {
        RevokeAll(ApprovalSessionEnd.Unloaded);
        lock (gate)
            disposed = true;
    }

    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Subscribers are UI/log plumbing; they must not break approval decisions.
        }
    }
}

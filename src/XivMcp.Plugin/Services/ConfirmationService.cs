using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Chat;

namespace XivMcp.Plugin.Services;

/// <summary>What the player chose in the confirmation window.</summary>
public enum ConfirmationDecision
{
    Deny,
    Allow,

    /// <summary>Allow this call and further calls of the same tool, tier and client for <see cref="ConfirmationService.GrantDuration"/>.</summary>
    AllowForAWhile,
}

/// <summary>One call waiting for the player to approve or deny it in game.</summary>
public sealed class PendingConfirmation
{
    internal PendingConfirmation(string toolName, ToolPermission permission, ToolPermission tier, string? clientName, string? arguments, bool argumentsTruncated, DateTimeOffset createdAt, TimeSpan timeout)
    {
        ToolName = toolName;
        Permission = permission;
        Tier = tier;
        ClientName = clientName;
        Arguments = arguments;
        ArgumentsTruncated = argumentsTruncated;
        CreatedAt = createdAt;
        Deadline = createdAt + timeout;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string ToolName { get; }

    /// <summary>The tool's declared tier.</summary>
    public ToolPermission Permission { get; }

    /// <summary>What the call effectively does: Chat for execute_command with a chat command, otherwise <see cref="Permission"/>.</summary>
    public ToolPermission Tier { get; }

    /// <summary>Client-reported name and version (not authenticated).</summary>
    public string? ClientName { get; }

    /// <summary>Pretty-printed arguments with invisible characters made visible, or null when the call has none.</summary>
    public string? Arguments { get; }

    public bool ArgumentsTruncated { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset Deadline { get; }

    internal TaskCompletionSource<ConfirmationDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string GrantKey => ConfirmationService.GrantKeyOf(ToolName, Tier, ClientName);
}

/// <summary>A temporary "allow this tool" grant.</summary>
public sealed record ConfirmationGrant(string ToolName, ToolPermission Tier, string? ClientName, DateTimeOffset Expires);

/// <summary>
/// The server's <see cref="IToolCallApprover"/>: every Action/Chat call waits here until the player answers in the
/// confirmation window. Server threads await <see cref="ApproveToolCallAsync"/>; the ImGui window resolves requests
/// from the framework thread through <see cref="Resolve"/>. Timeouts, cancellation, "Deny all" and plugin unload all
/// deny, so nothing waits forever and nothing runs unconfirmed.
/// </summary>
public sealed class ConfirmationService : IToolCallApprover, IDisposable
{
    public const int MaxArgumentsLength = 4000;

    public const string ExecuteCommandTool = "execute_command";

    public static readonly TimeSpan GrantDuration = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Configuration config;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly List<PendingConfirmation> pending = [];
    private readonly Dictionary<string, ConfirmationGrant> grants = new(StringComparer.Ordinal);
    private bool disposed;

    public ConfirmationService(Configuration config, TimeProvider? time = null)
    {
        this.config = config;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised (any thread) when a request is added or resolved, or grants change.</summary>
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

    /// <summary>Unexpired grants, soonest expiry first.</summary>
    public IReadOnlyList<ConfirmationGrant> Grants()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            PruneGrantsLocked(now);
            return grants.Values.OrderBy(g => g.Expires).ToArray();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ApproveToolCallAsync(string toolName, ToolPermission permission, string? clientName, string? argumentsJson, CancellationToken cancellationToken)
    {
        if (permission < ToolPermission.Action || !config.ConfirmActions)
            return true;

        var tier = permission;
        if (toolName == ExecuteCommandTool && CommandArgument(argumentsJson) is { } commandLine)
        {
            switch (ChatCommands.Classify(commandLine))
            {
                case CommandKind.Blocked or CommandKind.Automation:
                    return true; // the tool refuses these itself; do not ask the player about a call that cannot run
                case CommandKind.Chat when !config.AllowChat:
                    return true; // likewise: execute_command rejects chat commands while the Chat tier is off
                case CommandKind.Chat:
                    tier = ToolPermission.Chat;
                    break;
            }
        }

        var now = time.GetUtcNow();
        var key = GrantKeyOf(toolName, tier, clientName);
        var (arguments, truncated) = FormatArguments(argumentsJson);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.ConfirmTimeoutSeconds, 5, 300));
        var request = new PendingConfirmation(toolName, permission, tier, clientName, arguments, truncated, now, timeout);
        lock (gate)
        {
            if (disposed)
                return false;
            PruneGrantsLocked(now);
            if (grants.ContainsKey(key))
                return true;
            pending.Add(request);
        }

        RaiseChanged();

        // The server cancels at its own approval timeout; this fallback only matters for other callers.
        using var fallback = new CancellationTokenSource(timeout + TimeSpan.FromSeconds(2), time);
        await using var cancelled = cancellationToken.Register(static state => ((PendingConfirmation)state!).Completion.TrySetCanceled(), request);
        await using var expired = fallback.Token.Register(static state => ((PendingConfirmation)state!).Completion.TrySetException(new TimeoutException()), request);
        ConfirmationDecision decision;
        try
        {
            decision = await request.Completion.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            lock (gate)
                pending.Remove(request);
            RaiseChanged();
        }

        if (decision == ConfirmationDecision.AllowForAWhile)
            Grant(request);
        return decision != ConfirmationDecision.Deny;
    }

    /// <summary>Called from the Draw loop when the player clicks a button.</summary>
    public void Resolve(Guid id, ConfirmationDecision decision)
    {
        PendingConfirmation? match;
        lock (gate)
            match = pending.FirstOrDefault(p => p.Id == id);
        match?.Completion.TrySetResult(decision);
    }

    public void DenyAll()
    {
        foreach (var request in Snapshot())
            request.Completion.TrySetResult(ConfirmationDecision.Deny);
    }

    /// <summary>Drops every "allow for a while" grant (after permission changes, or from the UI).</summary>
    public void RevokeGrants()
    {
        bool any;
        lock (gate)
        {
            any = grants.Count > 0;
            grants.Clear();
        }

        if (any)
            RaiseChanged();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            grants.Clear();
        }

        DenyAll();
    }

    internal static string GrantKeyOf(string toolName, ToolPermission tier, string? clientName) =>
        $"{toolName}\n{(int)tier}\n{clientName ?? ""}";

    private void Grant(PendingConfirmation request)
    {
        var grant = new ConfirmationGrant(request.ToolName, request.Tier, request.ClientName, time.GetUtcNow() + GrantDuration);
        PendingConfirmation[] alsoCovered;
        lock (gate)
        {
            if (disposed)
                return;
            grants[request.GrantKey] = grant;
            alsoCovered = pending.Where(p => p.GrantKey == request.GrantKey).ToArray();
        }

        foreach (var other in alsoCovered)
            other.Completion.TrySetResult(ConfirmationDecision.Allow);
        RaiseChanged();
    }

    private void PruneGrantsLocked(DateTimeOffset now)
    {
        if (grants.Count == 0)
            return;
        foreach (var key in grants.Where(g => g.Value.Expires <= now).Select(g => g.Key).ToArray())
            grants.Remove(key);
    }

    /// <summary>The "command" argument of an execute_command call, or null.</summary>
    internal static string? CommandArgument(string? argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson))
            return null;
        try
        {
            return JsonNode.Parse(argumentsJson) is JsonObject args && args.TryGetPropertyValue("command", out var node) &&
                   node is JsonValue value && value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Indented JSON for the confirmation window. Readable text stays readable, but characters that could hide or
    /// reorder what the player reads (bidi overrides, zero-width and other format characters, line separators) are
    /// shown as \uXXXX escapes.
    /// </summary>
    internal static (string? Text, bool Truncated) FormatArguments(string? argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson))
            return (null, false);

        string pretty;
        try
        {
            var node = JsonNode.Parse(argumentsJson);
            if (node is JsonObject { Count: 0 })
                return (null, false);
            pretty = node?.ToJsonString(PrettyJson) ?? "null";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            pretty = argumentsJson;
        }

        var sb = new StringBuilder(pretty.Length);
        foreach (var ch in pretty)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate
                || (char.IsControl(ch) && ch is not '\n'))
                sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            else
                sb.Append(ch);
        }

        var text = sb.ToString();
        return text.Length > MaxArgumentsLength ? (text[..MaxArgumentsLength], true) : (text, false);
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

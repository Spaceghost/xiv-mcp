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
    internal PendingConfirmation(string toolName, ToolPermission permission, ToolPermission tier, string? clientName, string? arguments, bool argumentsTruncated, DateTimeOffset createdAt, TimeSpan timeout, string? sessionId = null)
    {
        SessionId = sessionId;
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

    /// <summary>MCP session of the call; null for stateless requests.</summary>
    public string? SessionId { get; }

    /// <summary>Per-client token name the call authenticated with (not self-reported), or null.</summary>
    public string? AuthenticatedClient { get; init; }

    /// <summary>What the call will do, in one sentence, with the arguments filled in verbatim (from the tool's ApprovalSummary).</summary>
    public string? Summary { get; init; }

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
public sealed class ConfirmationService : ISessionAwareToolCallApprover, IDisposable
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

    /// <summary>
    /// "Allow everything from this client" sessions. Checked after the confirmation toggle and before a prompt; null means
    /// no sessions (tests, or before the plugin wires it).
    /// </summary>
    public ApprovalSessionService? Sessions { get; set; }

    /// <summary>Raised (any thread) when a request is added or resolved, or grants change.</summary>
    public event Action? Changed;

    /// <summary>Raised (caller's thread) for every call an owner auto-approve rule let through without a prompt.</summary>
    public event Action<ToolCallApprovalRequest, ToolPermission, AutoApproveMatch>? AutoApproved;

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
    public Task<bool> ApproveToolCallAsync(string toolName, ToolPermission permission, string? clientName, string? argumentsJson, CancellationToken cancellationToken) =>
        ApproveToolCallAsync(new ToolCallApprovalRequest(toolName, permission, clientName, null, argumentsJson), cancellationToken);

    /// <inheritdoc />
    public async Task<bool> ApproveToolCallAsync(ToolCallApprovalRequest call, CancellationToken cancellationToken)
    {
        var (toolName, permission, clientName, sessionId, argumentsJson, authenticatedClient) = call;

        // THE approval switch. The server puts every state-changing call (Action, Chat, and the Ui tools that ask for it)
        // to this method and nothing else decides whether the player is asked: unticked, everything runs at once (and is
        // still written to the action log); ticked, rules, sessions and grants below are refinements under it.
        if (!RequiresPrompting(config, permission))
            return true;

        // Null: the tool refuses the call by itself, so do not ask the player about a call that cannot run.
        if (EffectiveTier(toolName, permission, argumentsJson, config.AllowChat) is not { } tier)
            return true;

        if (PolicyApproves(call, tier))
            return true;

        if (Sessions?.Covers(clientName, sessionId, tier) == true)
            return true;

        var now = time.GetUtcNow();
        var key = GrantKeyOf(toolName, tier, clientName);
        var (arguments, truncated) = FormatArguments(argumentsJson);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.ConfirmTimeoutSeconds, 5, 300));
        var request = new PendingConfirmation(toolName, permission, tier, clientName, arguments, truncated, now, timeout, sessionId) { AuthenticatedClient = authenticatedClient, Summary = call.Summary };
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

    /// <summary>
    /// The single approval switch (<see cref="Configuration.ConfirmActions"/>, "Ask me before anything changes"): false
    /// when it is unticked, or for a Read call, which never reaches the gate anyway.
    /// </summary>
    public static bool RequiresPrompting(Configuration config, ToolPermission permission) =>
        config.ConfirmActions && permission > ToolPermission.Read;

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

    /// <summary>
    /// What an Action/Chat call effectively does: Chat for an execute_command line that posts chat, otherwise
    /// <paramref name="permission"/>. Null when the tool refuses the call by itself (blocked or automation commands, or chat
    /// while the Chat tier is off), so there is nothing to approve.
    /// </summary>
    public static ToolPermission? EffectiveTier(string toolName, ToolPermission permission, string? argumentsJson, bool allowChat)
    {
        // A Ui tool only gets here when it asked for approval; rules, sessions and grants treat it as an Action.
        if (permission == ToolPermission.Ui)
            return ToolPermission.Action;
        if (toolName != ExecuteCommandTool || CommandArgument(argumentsJson) is not { } commandLine)
            return permission;
        return ChatCommands.Classify(commandLine) switch
        {
            CommandKind.Blocked or CommandKind.Automation => null,
            CommandKind.Chat when !allowChat => null,
            CommandKind.Chat => ToolPermission.Chat,
            _ => permission,
        };
    }

    /// <summary>
    /// True when an Action/Chat call of <paramref name="tier"/> would run now without a prompt: confirmation is off, an
    /// owner rule pre-approves it for the token-identified client, an approval session covers the client, or an "allow this
    /// tool" grant matches. <paramref name="how"/> names which. A policy match raises <see cref="AutoApproved"/>.
    /// </summary>
    public bool PassesWithoutPrompt(ToolCallApprovalRequest call, ToolPermission tier, out string how)
    {
        var (toolName, _, clientName, sessionId, _, _) = call;
        how = "";
        if (!config.ConfirmActions)
        {
            how = "confirmation off";
            return true;
        }

        if (PolicyApproves(call, tier))
        {
            how = "policy";
            return true;
        }

        if (Sessions?.Covers(clientName, sessionId, tier) == true)
        {
            how = "session";
            return true;
        }

        var key = GrantKeyOf(toolName, tier, clientName);
        lock (gate)
        {
            if (disposed)
                return false;
            PruneGrantsLocked(time.GetUtcNow());
            if (!grants.ContainsKey(key))
                return false;
        }

        how = "grant";
        return true;
    }

    /// <summary>Owner auto-approve rules (<see cref="Configuration.AutoApproveRules"/>); raises <see cref="AutoApproved"/> on a match.</summary>
    private bool PolicyApproves(ToolCallApprovalRequest call, ToolPermission tier)
    {
        if (AutoApprovePolicy.Match(config.AutoApproveRules, call.AuthenticatedClient, call.ToolName, tier, call.ArgumentsJson) is not { } match)
            return false;
        try
        {
            AutoApproved?.Invoke(call, tier, match);
        }
        catch
        {
            // Logging plumbing must not change the decision.
        }

        return true;
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

        var text = ShowInvisible(pretty);
        return text.Length > MaxArgumentsLength ? (text[..MaxArgumentsLength], true) : (text, false);
    }

    /// <summary>
    /// Shows characters that could hide or reorder what the player reads (format/bidi, line separators, controls other
    /// than a newline, lone surrogates) as \uXXXX escapes; everything else stays as it is.
    /// </summary>
    public static string ShowInvisible(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate
                || (char.IsControl(ch) && ch is not '\n'))
                sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            else
                sb.Append(ch);
        }

        return sb.ToString();
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

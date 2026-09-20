// FROZEN CONTRACT: shared by every provider. Additive changes only.
using System.Text.Json.Nodes;

namespace XivMcp.Core;

/// <summary>
/// Runs work on the game's framework thread. The Dalamud plugin implements this with
/// IFramework.RunOnFrameworkThread; the dev host runs inline.
/// </summary>
public interface IGameThread
{
    /// <summary>True when the caller is already on the framework thread.</summary>
    bool IsOnGameThread { get; }

    Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default);

    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

/// <summary>Answers host-level questions the server needs before dispatching.</summary>
public interface IHostState
{
    /// <summary>A character is logged in and the local player object exists. Called on the game thread.</summary>
    bool IsLoggedIn { get; }

    /// <summary>Whether a tier is currently allowed by configuration.</summary>
    bool IsPermitted(ToolPermission permission);

    /// <summary>Whether a provider category is enabled by configuration.</summary>
    bool IsCategoryEnabled(string category);
}

/// <summary>Server → client notifications a provider may raise from any thread.</summary>
public interface IMcpNotifier
{
    /// <summary>notifications/resources/updated to every session subscribed to <paramref name="uri"/>.</summary>
    void ResourceUpdated(string uri);

    /// <summary>notifications/resources/list_changed, tools/list_changed, prompts/list_changed.</summary>
    void ResourceListChanged();

    void ToolListChanged();

    void PromptListChanged();

    /// <summary>notifications/message to every session whose logging level admits <paramref name="level"/>.</summary>
    void Log(McpLogLevel level, string logger, object? data);
}

/// <summary>
/// Optional per-call approval for Action and Chat tools (see <see cref="McpServer.Approver"/>). The server awaits it
/// on a thread-pool thread, never on the game thread, after the category, permission-tier, argument and login checks
/// and before invoking any tool whose <see cref="McpToolAttribute.Permission"/> is <see cref="ToolPermission.Action"/>
/// or <see cref="ToolPermission.Chat"/>. Return false to deny (the client gets an isError result "Denied in game by the
/// player"). Throw <see cref="TimeoutException"/> (or let the cancellation token fire) when nobody
/// answered in time. Any other exception denies the call.
/// </summary>
public interface IToolCallApprover
{
    /// <param name="toolName">Registered tool name.</param>
    /// <param name="permission">The tool's declared tier (Action or Chat).</param>
    /// <param name="clientName">Client-reported name and version, if any (not authenticated).</param>
    /// <param name="argumentsJson">The call's arguments object as compact JSON, or null when none were sent.</param>
    /// <param name="cancellationToken">Fires on client cancellation, server stop, or the approval timeout.</param>
    Task<bool> ApproveToolCallAsync(string toolName, ToolPermission permission, string? clientName, string? argumentsJson, CancellationToken cancellationToken);
}

/// <summary>One Action/Chat call as <see cref="ISessionAwareToolCallApprover"/> sees it.</summary>
/// <param name="ToolName">Registered tool name.</param>
/// <param name="Permission">The tool's declared tier (Action or Chat).</param>
/// <param name="ClientName">Client-reported name and version, if any (not authenticated).</param>
/// <param name="SessionId">MCP session id (Mcp-Session-Id) of the calling session; null for stateless (2026-07-28) requests.</param>
/// <param name="ArgumentsJson">The call's arguments object as compact JSON, or null when none were sent.</param>
/// <param name="AuthenticatedClient">Per-client token name the request authenticated with; null for the main token.</param>
public sealed record ToolCallApprovalRequest(string ToolName, ToolPermission Permission, string? ClientName, string? SessionId, string? ArgumentsJson, string? AuthenticatedClient = null)
{
    /// <summary>The tool's <see cref="McpToolAttribute.ApprovalSummary"/> with this call's arguments filled in: what will happen, for the player.</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// One call of a tool that goes through the approval gate, after it ran (or failed). Raised whether the player was
/// asked, a grant/session/rule covered it, or asking is switched off, so the host can keep a record of every action.
/// </summary>
/// <param name="ToolName">Registered tool name.</param>
/// <param name="Permission">The tool's declared tier.</param>
/// <param name="ClientName">Client-reported name and version, if any (not authenticated).</param>
/// <param name="AuthenticatedClient">Per-client token name the request authenticated with; null for the main token.</param>
/// <param name="SessionId">MCP session id, when there is one.</param>
/// <param name="Summary">What the call did, for a human (<see cref="McpToolAttribute.ApprovalSummary"/> filled in).</param>
/// <param name="ArgumentsJson">The call's arguments as compact JSON, or null.</param>
/// <param name="Success">False when the tool reported an error.</param>
/// <param name="Error">The error text when it failed.</param>
/// <param name="PreApproved">True for a call the host ran through <see cref="McpServer.ExecuteApprovedToolAsync"/> (an approved ticket).</param>
public sealed record GatedToolExecution(string ToolName, ToolPermission Permission, string? ClientName, string? AuthenticatedClient, string? SessionId, string Summary, string? ArgumentsJson, bool Success, string? Error, bool PreApproved);

/// <summary>A per-client bearer token: the client's name and the SHA-256 (hex) of the token.</summary>
public sealed record ClientToken(string Name, string Sha256Hex);

/// <summary>
/// An <see cref="IToolCallApprover"/> that also wants the caller's MCP session. When <see cref="McpServer.Approver"/>
/// implements it, the server calls this overload instead of <see cref="IToolCallApprover.ApproveToolCallAsync"/>; the
/// semantics (false denies, <see cref="TimeoutException"/> or the token means not confirmed) are the same.
/// </summary>
public interface ISessionAwareToolCallApprover : IToolCallApprover
{
    Task<bool> ApproveToolCallAsync(ToolCallApprovalRequest call, CancellationToken cancellationToken);
}

/// <summary>What <see cref="McpServer.DescribeApproval"/> says about one call.</summary>
/// <param name="NeedsApproval">The call is put to the approver before it runs.</param>
/// <param name="Permission">The tool's declared tier.</param>
/// <param name="Summary">What the call will do, for the player.</param>
public sealed record ToolApprovalInfo(bool NeedsApproval, ToolPermission Permission, string Summary);

/// <summary>Outcome of <see cref="McpServer.ExecuteApprovedToolAsync"/>.</summary>
/// <param name="Result">A tools/call result object (content, structuredContent, isError) as a 2026-07-28 client would get it.</param>
/// <param name="IsError">True when the tool did not run or reported an error.</param>
/// <param name="Error">The error text when <paramref name="IsError"/> is true.</param>
public sealed record ToolExecutionResult(JsonObject Result, bool IsError, string? Error);

public enum McpLogLevel { Debug, Info, Notice, Warning, Error, Critical, Alert, Emergency }

/// <summary>Per-call context injected when a tool/resource/prompt method declares it.</summary>
public sealed class ToolContext
{
    public required IGameThread Game { get; init; }

    public required IMcpNotifier Notifier { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    /// <summary>MCP session id (Mcp-Session-Id), or null for sessionless calls.</summary>
    public string? SessionId { get; init; }

    /// <summary>clientInfo.name from initialize, if known.</summary>
    public string? ClientName { get; init; }

    /// <summary>MCP protocol revision this call is served under (negotiated for sessions, per request for 2026-07-28).</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>
    /// Name of the per-client bearer token the request authenticated with (<see cref="McpServerOptions.ClientTokens"/>), or
    /// null for the main token. Unlike <see cref="ClientName"/>, a client cannot choose this.
    /// </summary>
    public string? AuthenticatedClient { get; init; }

    /// <summary>
    /// Sends notifications/progress when the request carried _meta.progressToken; otherwise no-op.
    /// </summary>
    public Func<double, double?, string?, Task> ReportProgress { get; init; } = static (_, _, _) => Task.CompletedTask;
}

/// <summary>Thrown by providers for expected, user-facing failures.</summary>
public class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }

    public McpToolException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// Stable machine-readable code from <see cref="McpErrorCodes"/>, published as <c>_meta["dev.xivmcp/error"].code</c> on the
    /// isError result. Defaults to <see cref="McpErrorCodes.ToolError"/>.
    /// </summary>
    public string Code { get; init; } = McpErrorCodes.ToolError;

    /// <summary>Whether the same call may succeed later without the caller changing anything.</summary>
    public bool Retryable { get; init; }

    /// <summary>Shorthand for a failure with a code.</summary>
    public static McpToolException WithCode(string code, string message, bool retryable = false) =>
        new(message) { Code = code, Retryable = retryable };
}

/// <summary>
/// The error model: every isError tool result carries <c>_meta["dev.xivmcp/error"] = {code, message, retryable}</c>
/// next to its text. Codes are stable; messages are for people and models and may be reworded.
/// </summary>
public static class McpErrorCodes
{
    /// <summary>A provider reported a failure without a more specific code.</summary>
    public const string ToolError = "tool_error";

    /// <summary>The thing asked for (item, quest, plugin, window) does not exist.</summary>
    public const string NotFound = "not_found";

    public const string InvalidArguments = "invalid_arguments";

    public const string UnknownTool = "unknown_tool";

    /// <summary>The tool's category is switched off in the settings.</summary>
    public const string CategoryDisabled = "category_disabled";

    /// <summary>The tool's permission tier is switched off in the settings.</summary>
    public const string TierDisabled = "tier_disabled";

    /// <summary>No character is logged in.</summary>
    public const string LoginRequired = "login_required";

    /// <summary>Served by the standalone host: the tool needs the running game. Retry once the game is up.</summary>
    public const string GameNotRunning = "game_not_running";

    /// <summary>The player clicked Deny.</summary>
    public const string Denied = "denied";

    /// <summary>Nobody answered the approval prompt in time.</summary>
    public const string NotConfirmed = "not_confirmed";

    public const string Timeout = "timeout";

    /// <summary>Too many calls; <c>retryAfterSeconds</c> says when to come back.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>Something the tool depends on is missing or not loaded (another plugin, an unopened window, a file).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Refused on purpose: the request is outside what this server does (see docs/HARD-LINES.md).</summary>
    public const string Refused = "refused";

    public const string InternalError = "internal_error";
}

/// <summary>Explicit tool result when the default JSON serialization is not wanted.</summary>
public sealed class ToolResult
{
    public List<ContentBlock> Content { get; init; } = [];

    public JsonNode? StructuredContent { get; init; }

    public bool IsError { get; init; }

    public static ToolResult Text(string text, bool isError = false) =>
        new() { Content = [ContentBlock.FromText(text)], IsError = isError };
}

public sealed class ContentBlock
{
    /// <summary>"text" | "image" | "resource_link" | "resource".</summary>
    public required string Type { get; init; }

    public string? Text { get; init; }

    /// <summary>Base64 for images.</summary>
    public string? Data { get; init; }

    public string? MimeType { get; init; }

    public string? Uri { get; init; }

    public string? Name { get; init; }

    public static ContentBlock FromText(string text) => new() { Type = "text", Text = text };

    public static ContentBlock FromImage(byte[] bytes, string mimeType) =>
        new() { Type = "image", Data = Convert.ToBase64String(bytes), MimeType = mimeType };
}

public sealed class PromptResult
{
    public string? Description { get; init; }

    public List<PromptMessage> Messages { get; init; } = [];
}

public sealed class PromptMessage
{
    /// <summary>"user" | "assistant".</summary>
    public required string Role { get; init; }

    public required ContentBlock Content { get; init; }

    public static PromptMessage User(string text) => new() { Role = "user", Content = ContentBlock.FromText(text) };

    public static PromptMessage Assistant(string text) => new() { Role = "assistant", Content = ContentBlock.FromText(text) };
}

/// <summary>One entry in the activity feed (every request the server handled).</summary>
public sealed record ActivityEntry(
    DateTimeOffset Timestamp,
    string? SessionId,
    string? ClientName,
    string Method,
    string? Target,
    bool Success,
    string? Error,
    double DurationMs);

/// <summary>Snapshot for UIs (plugin window, Umbra widget over IPC).</summary>
public sealed record ServerStatus(
    bool Running,
    string Endpoint,
    int ActiveSessions,
    long TotalRequests,
    long FailedRequests,
    string? LastError,
    IReadOnlyList<string> ConnectedClients);

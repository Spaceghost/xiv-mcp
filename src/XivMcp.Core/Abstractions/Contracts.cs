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

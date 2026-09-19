// FROZEN PUBLIC SURFACE (signatures). The core agent implements the bodies; additive changes only.
using System.Reflection;

namespace XivMcp.Core;

public sealed class McpServerOptions
{
    /// <summary>Loopback only by default. Anything else must be an explicit user choice.</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 41800;

    /// <summary>Endpoint path for Streamable HTTP.</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>Required as "Authorization: Bearer &lt;token&gt;" when non-empty.</summary>
    public string? BearerToken { get; set; }

    /// <summary>Origins accepted when a request carries an Origin header (DNS-rebinding defence). Loopback origins are always accepted.</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Additional bearer tokens that each identify one named client (stored as SHA-256 hex, never in clear). A request
    /// presenting one is authorized like <see cref="BearerToken"/> and carries the name as
    /// <see cref="ToolContext.AuthenticatedClient"/> / <see cref="ToolCallApprovalRequest.AuthenticatedClient"/>. Read on
    /// every request, so replacing the list revokes a token at once. Replace the list; do not mutate it.
    /// </summary>
    public IReadOnlyList<ClientToken> ClientTokens { get; set; } = [];

    public string ServerName { get; set; } = "xiv-mcp";

    public string ServerTitle { get; set; } = "FINAL FANTASY XIV (Dalamud)";

    public string ServerVersion { get; set; } = "0.1.0";

    /// <summary>Returned in InitializeResult.instructions.</summary>
    public string? Instructions { get; set; }

    public int MaxRequestBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Upper bound for a single tool/resource/prompt invocation.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Sessions idle longer than this are dropped.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public int ActivityCapacity { get; set; } = 500;

    /// <summary>Interval between SSE keep-alive comments on idle streams.</summary>
    public TimeSpan SseKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a call waits for <see cref="McpServer.Approver"/> before it fails with "Not confirmed in game". Not
    /// counted against <see cref="CallTimeout"/>, which starts once the call is approved.
    /// </summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum items per page for tools/list, resources/list, resources/templates/list and prompts/list.</summary>
    public int ListPageSize { get; set; } = 250;
}

/// <summary>
/// MCP server over Streamable HTTP (POST/GET/DELETE on <see cref="McpServerOptions.Path"/>, SSE for
/// server→client messages). Thread-safe; Start/Stop may be called repeatedly.
/// </summary>
public sealed partial class McpServer : IAsyncDisposable
{
    public McpServer(McpServerOptions options, IGameThread gameThread, IHostState hostState, Action<string, Exception?>? log = null)
    {
        Options = options;
        GameThread = gameThread;
        HostState = hostState;
        LogSink = log ?? ((_, _) => { });
        InitializeCore();
    }

    public McpServerOptions Options { get; }

    public IGameThread GameThread { get; }

    public IHostState HostState { get; }

    /// <summary>
    /// Optional in-game confirmation for Action and Chat tools. Null (default) runs them directly once their tier is
    /// permitted. May be set or cleared at any time; each call reads it once.
    /// </summary>
    public IToolCallApprover? Approver { get; set; }

    internal Action<string, Exception?> LogSink { get; }

    /// <summary>Registers every attributed member of <paramref name="provider"/>. Duplicate names throw.</summary>
    public partial void RegisterProvider(object provider);

    /// <summary>
    /// Instantiates and registers each <see cref="McpProviderAttribute"/> type in <paramref name="assembly"/>
    /// using <paramref name="factory"/> (the plugin resolves constructor dependencies there).
    /// </summary>
    public partial void RegisterProviders(Assembly assembly, Func<Type, object> factory);

    /// <summary>Binds the listener. Throws if the port cannot be bound.</summary>
    public partial Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes every session and SSE stream and releases the port.</summary>
    public partial Task StopAsync();

    public partial ValueTask DisposeAsync();

    public partial IMcpNotifier Notifier { get; }

    public partial ServerStatus GetStatus();

    /// <summary>Newest first, at most <paramref name="max"/> entries.</summary>
    public partial IReadOnlyList<ActivityEntry> GetActivity(int max = 100);

    /// <summary>Raised (on a thread-pool thread) after every handled request.</summary>
    public event Action<ActivityEntry>? ActivityRecorded;

    /// <summary>Registered tools as (name, title, description, permission, category) for UIs.</summary>
    public partial IReadOnlyList<(string Name, string? Title, string Description, ToolPermission Permission, string Category)> ListRegisteredTools();

    private partial void InitializeCore();

    private void RaiseActivity(ActivityEntry entry) => ActivityRecorded?.Invoke(entry);
}

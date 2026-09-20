// FROZEN CONTRACT between XivMcp.Plugin (provider) and XivMcp.Umbra (subscriber).
// Compiled into both assemblies via <Compile Include="..\Shared\IpcContract.cs" />.
// Payloads are JSON strings so neither side needs the other's types.
namespace XivMcp.Shared;

public static class IpcContract
{
    /// <summary>Bumped on any breaking change; subscribers compare before use.</summary>
    /// <remarks>
    /// Still 1: the gates from <see cref="ApiRevision"/> down were added additively in revision 2, so revision-1
    /// subscribers keep working. Subscribers that need them check <see cref="ApiRevision"/> (absent before revision 2).
    /// </remarks>
    public const int Version = 1;

    /// <summary>Additive revision within <see cref="Version"/>. 2: local model, ConnectClient, LocalModelChanged.</summary>
    public const int Revision = 2;

    /// <summary>Func&lt;int&gt;: returns <see cref="Version"/>. Absent when the plugin is not loaded.</summary>
    public const string ApiVersion = "XivMcp.ApiVersion";

    /// <summary>
    /// Func&lt;string&gt;: JSON object
    /// { running, endpoint, endpoints: string[], activeSessions, totalRequests, failedRequests, lastError, connectedClients: string[],
    ///   permissions: { read, ui, action, chat } }
    /// </summary>
    public const string GetStatus = "XivMcp.GetStatus";

    /// <summary>Func&lt;int, string&gt;: JSON array of the newest N activity entries
    /// [{ timestamp, sessionId, clientName, method, target, success, error, durationMs }].</summary>
    public const string GetActivity = "XivMcp.GetActivity";

    /// <summary>Func&lt;string&gt;: JSON array of agent board posts
    /// [{ agent, status, state: "running"|"done"|"failed"|"info", progress: number|null, detail, updatedAt }], newest first.</summary>
    public const string GetAgentBoard = "XivMcp.GetAgentBoard";

    /// <summary>Func&lt;bool, bool&gt;: start (true) or stop (false) the server; returns running state afterwards.</summary>
    public const string SetRunning = "XivMcp.SetRunning";

    /// <summary>Action: toggles the plugin's main window.</summary>
    public const string ToggleWindow = "XivMcp.ToggleWindow";

    /// <summary>Action (event): raised whenever status, activity or the agent board changes (throttled to ≤4 Hz).</summary>
    public const string Changed = "XivMcp.Changed";

    // ---- revision 2 (additive) ---------------------------------------------------------------

    /// <summary>Func&lt;int&gt;: returns <see cref="Revision"/>.</summary>
    public const string ApiRevision = "XivMcp.ApiRevision";

    /// <summary>
    /// Func&lt;string&gt;: JSON object { configured: bool, endpoint: string|null, model: string|null, hasApiKey: bool }.
    /// <c>endpoint</c> is an OpenAI-compatible base URL (e.g. http://127.0.0.1:11434/v1); configured = endpoint and model set.
    /// The API key itself is never returned.
    /// </summary>
    public const string GetLocalModel = "XivMcp.GetLocalModel";

    /// <summary>
    /// Func&lt;string, string&gt;: argument = client name (1-64 of letters, digits, '-', '_', '.'). Issues a fresh per-client
    /// bearer token for that name (replacing an existing one) and returns { endpoint, token, clientName }, or
    /// { error: "disabled" | "invalid_client_name" | "failed" }. Game actions from that client still need in-game approval.
    /// <c>endpoint</c> is the address the listener bound (the configured one while the server is stopped); an
    /// <c>endpoints</c> array follows when bind modes land.
    /// </summary>
    public const string ConnectClient = "XivMcp.ConnectClient";

    /// <summary>Action (event): raised when the local model settings are saved.</summary>
    public const string LocalModelChanged = "XivMcp.LocalModelChanged";
}

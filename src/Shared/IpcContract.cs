// FROZEN CONTRACT between XivMcp.Plugin (provider) and XivMcp.Umbra (subscriber).
// Compiled into both assemblies via <Compile Include="..\Shared\IpcContract.cs" />.
// Payloads are JSON strings so neither side needs the other's types.
namespace XivMcp.Shared;

public static class IpcContract
{
    /// <summary>Bumped on any breaking change; subscribers compare before use.</summary>
    public const int Version = 1;

    /// <summary>Func&lt;int&gt;: returns <see cref="Version"/>. Absent when the plugin is not loaded.</summary>
    public const string ApiVersion = "XivMcp.ApiVersion";

    /// <summary>
    /// Func&lt;string&gt;: JSON object
    /// { running, endpoint, activeSessions, totalRequests, failedRequests, lastError, connectedClients: string[],
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
}

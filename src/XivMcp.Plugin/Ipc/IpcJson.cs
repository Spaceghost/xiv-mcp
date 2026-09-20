using System.Text.Json;
using System.Text.Json.Serialization;
using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Ipc;

/// <summary>
/// JSON payloads of the <see cref="Shared.IpcContract"/> gates (camelCase, nulls written). Pure, so the Umbra
/// widget's parser is tested against exactly what the plugin sends.
/// </summary>
public static class IpcJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// GetStatus. <c>permissions</c> are the effective tier toggles. <c>endpoint</c> is the address to hand a
    /// client — with the tailnet bound that is the tailnet address, which works both on and off this machine —
    /// and <c>endpoints</c> lists every address the server answers on, in bind order.
    /// </summary>
    public static string Status(
        bool running,
        string endpoint,
        ServerStatus status,
        string? hostError,
        IHostState permissions,
        int agents,
        bool confirmActions,
        IReadOnlyList<string>? endpoints = null) =>
        JsonSerializer.Serialize(
            new
            {
                running,
                endpoint,
                endpoints = endpoints ?? [endpoint],
                activeSessions = status.ActiveSessions,
                totalRequests = status.TotalRequests,
                failedRequests = status.FailedRequests,
                lastError = hostError ?? status.LastError,
                connectedClients = status.ConnectedClients,
                permissions = new
                {
                    read = permissions.IsPermitted(ToolPermission.Read),
                    ui = permissions.IsPermitted(ToolPermission.Ui),
                    action = permissions.IsPermitted(ToolPermission.Action),
                    chat = permissions.IsPermitted(ToolPermission.Chat),
                },
                agents,
                confirmActions,
            },
            Json);

    /// <summary>GetActivity: newest first.</summary>
    public static string Activity(IEnumerable<ActivityEntry> entries) => JsonSerializer.Serialize(
        entries.Select(e => new
        {
            timestamp = e.Timestamp,
            sessionId = e.SessionId,
            clientName = e.ClientName,
            method = e.Method,
            target = e.Target,
            success = e.Success,
            error = e.Error,
            durationMs = e.DurationMs,
        }),
        Json);

    /// <summary>GetAgentBoard: newest update first.</summary>
    public static string Board(IEnumerable<AgentPost> posts) => JsonSerializer.Serialize(
        posts.Select(p => new
        {
            agent = p.Agent,
            status = p.Status,
            state = p.StateName,
            progress = p.Progress,
            detail = p.Detail,
            clientName = p.ClientName,
            updatedAt = p.UpdatedAt,
        }),
        Json);
}

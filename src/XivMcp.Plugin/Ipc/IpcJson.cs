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

    /// <summary>GetStatus. <c>permissions</c> are the effective tier toggles.</summary>
    public static string Status(bool running, string endpoint, ServerStatus status, string? hostError, IHostState permissions, int agents, bool confirmActions) =>
        JsonSerializer.Serialize(
            new
            {
                running,
                endpoint,
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

    /// <summary>GetLocalModel. The API key is reported only as <c>hasApiKey</c>.</summary>
    public static string LocalModel(string? endpoint, string? model, string? apiKey)
    {
        var e = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
        var m = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        return JsonSerializer.Serialize(
            new { configured = e != null && m != null, endpoint = e, model = m, hasApiKey = !string.IsNullOrWhiteSpace(apiKey) },
            Json);
    }

    /// <summary>ConnectClient success: the MCP endpoint and the new per-client token.</summary>
    public static string Connect(string endpoint, string token, string clientName) =>
        JsonSerializer.Serialize(new { endpoint, token, clientName }, Json);

    /// <summary>ConnectClient failure.</summary>
    public static string Error(string error) => JsonSerializer.Serialize(new { error }, Json);
}

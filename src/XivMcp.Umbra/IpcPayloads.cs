// Plain data + JSON parsing for the XivMcp IPC payloads. No Dalamud/Umbra types here so the
// parser can be compiled and tested on the host without the game.
using System.Globalization;
using System.Text.Json;

namespace Umbra.XivMcp;

/// <summary>How the widget currently sees the XivMcp plugin.</summary>
public enum McpLinkState
{
    /// <summary>XivMcp is not loaded (no IPC provider registered).</summary>
    Missing,

    /// <summary>XivMcp is loaded but speaks a different IPC version.</summary>
    VersionMismatch,

    /// <summary>An IPC call failed; will be retried.</summary>
    Error,

    /// <summary>XivMcp is loaded, the HTTP server is stopped.</summary>
    Stopped,

    /// <summary>XivMcp is loaded and listening.</summary>
    Running,
}

public sealed record McpPermissions(bool Read, bool Ui, bool Action, bool Chat);

public sealed record McpStatus(
    bool Running,
    string Endpoint,
    int ActiveSessions,
    long TotalRequests,
    long FailedRequests,
    string? LastError,
    IReadOnlyList<string> ConnectedClients,
    McpPermissions? Permissions);

public sealed record McpActivity(
    DateTimeOffset? Timestamp,
    string? SessionId,
    string? ClientName,
    string Method,
    string? Target,
    bool Success,
    string? Error,
    double DurationMs);

public sealed record McpAgentPost(
    string Agent,
    string Status,
    string State,
    double? Progress,
    string? Detail,
    DateTimeOffset? UpdatedAt,
    string? ClientName = null)
{
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Progress normalised to 0..1. Accepts 0..1 fractions and 0..100 percentages.</summary>
    public double? Fraction => McpFormat.NormalizeProgress(Progress);
}

/// <summary>Immutable view the widgets render. Replaced wholesale on every refresh.</summary>
public sealed record McpSnapshot(
    McpLinkState State,
    int? RemoteVersion,
    McpStatus? Status,
    IReadOnlyList<McpActivity> Activity,
    IReadOnlyList<McpAgentPost> Agents,
    string? Problem,
    DateTimeOffset RefreshedAt)
{
    public static McpSnapshot Initial { get; } =
        new(McpLinkState.Missing, null, null, [], [], null, DateTimeOffset.MinValue);

    public int RunningAgents => Agents.Count(a => a.IsRunning);

    public int Sessions => Status?.ActiveSessions ?? 0;
}

public static class IpcPayloadParser
{
    /// <summary>Parses GetStatus JSON. Returns null when the payload is not a JSON object.</summary>
    public static McpStatus? ParseStatus(string? json)
    {
        if (!TryParse(json, JsonValueKind.Object, out var doc)) return null;
        using (doc)
        {
            var o = doc!.RootElement;
            McpPermissions? perms = null;
            if (TryProp(o, "permissions", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                perms = new McpPermissions(
                    Bool(p, "read"), Bool(p, "ui"), Bool(p, "action"), Bool(p, "chat"));
            }

            var clients = new List<string>();
            if (TryProp(o, "connectedClients", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in c.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) clients.Add(s);
                }
            }

            return new McpStatus(
                Bool(o, "running"),
                Str(o, "endpoint") ?? string.Empty,
                (int)Math.Clamp(Long(o, "activeSessions"), 0, int.MaxValue),
                Long(o, "totalRequests"),
                Long(o, "failedRequests"),
                Str(o, "lastError"),
                clients,
                perms);
        }
    }

    /// <summary>Parses GetActivity JSON (newest first). Malformed entries are skipped.</summary>
    public static IReadOnlyList<McpActivity> ParseActivity(string? json)
    {
        if (!TryParse(json, JsonValueKind.Array, out var doc)) return [];
        using (doc)
        {
            var list = new List<McpActivity>();
            foreach (var e in doc!.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                list.Add(new McpActivity(
                    Time(e, "timestamp"),
                    Str(e, "sessionId"),
                    Str(e, "clientName"),
                    Str(e, "method") ?? "?",
                    Str(e, "target"),
                    Bool(e, "success"),
                    Str(e, "error"),
                    Double(e, "durationMs") ?? 0));
            }

            return list;
        }
    }

    /// <summary>Parses GetAgentBoard JSON (newest first). Entries without an agent name are skipped.</summary>
    public static IReadOnlyList<McpAgentPost> ParseAgentBoard(string? json)
    {
        if (!TryParse(json, JsonValueKind.Array, out var doc)) return [];
        using (doc)
        {
            var list = new List<McpAgentPost>();
            foreach (var e in doc!.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var agent = Str(e, "agent");
                if (string.IsNullOrWhiteSpace(agent)) continue;
                list.Add(new McpAgentPost(
                    agent,
                    Str(e, "status") ?? string.Empty,
                    (Str(e, "state") ?? "info").ToLowerInvariant(),
                    Double(e, "progress"),
                    Str(e, "detail"),
                    Time(e, "updatedAt"),
                    Str(e, "clientName")));
            }

            return list;
        }
    }

    private static bool TryParse(string? json, JsonValueKind kind, out JsonDocument? doc)
    {
        doc = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return false;
        }

        if (doc.RootElement.ValueKind == kind) return true;
        doc.Dispose();
        doc = null;
        return false;
    }

    private static bool TryProp(JsonElement o, string name, out JsonElement value)
    {
        if (o.TryGetProperty(name, out value)) return true;
        foreach (var p in o.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        return false;
    }

    private static string? Str(JsonElement o, string name) =>
        TryProp(o, name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    private static bool Bool(JsonElement o, string name) =>
        TryProp(o, name, out var v) && (v.ValueKind == JsonValueKind.True
                                        || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static long Long(JsonElement o, string name)
    {
        if (!TryProp(o, name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var l)) return l;
            if (v.TryGetDouble(out var d) && double.IsFinite(d)) return (long)Math.Clamp(d, long.MinValue, long.MaxValue);
        }

        return 0;
    }

    private static double? Double(JsonElement o, string name)
    {
        if (!TryProp(o, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            && double.IsFinite(s)) return s;
        return null;
    }

    /// <summary>ISO-8601 string, or a number of Unix milliseconds (seconds if it is small).</summary>
    private static DateTimeOffset? Time(JsonElement o, string name)
    {
        if (!TryProp(o, name, out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                return DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            case JsonValueKind.Number when v.TryGetInt64(out var n):
                try
                {
                    return n < 100_000_000_000L
                        ? DateTimeOffset.FromUnixTimeSeconds(n)
                        : DateTimeOffset.FromUnixTimeMilliseconds(n);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            default:
                return null;
        }
    }
}

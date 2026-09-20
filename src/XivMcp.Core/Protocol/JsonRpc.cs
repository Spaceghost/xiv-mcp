using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Core.Protocol;

internal static class ProtocolVersions
{
    /// <summary>Stateless revision: per-request _meta, server/discover, subscriptions/listen.</summary>
    public const string V2026_07_28 = "2026-07-28";

    public const string V2025_11_25 = "2025-11-25";
    public const string V2025_06_18 = "2025-06-18";
    public const string V2025_03_26 = "2025-03-26";

    public const string LatestModern = V2026_07_28;
    public const string LatestLegacy = V2025_11_25;

    /// <summary>Every version this server speaks, newest first.</summary>
    public static readonly string[] All = [V2026_07_28, V2025_11_25, V2025_06_18, V2025_03_26];

    public static readonly string[] Modern = [V2026_07_28];

    public static readonly string[] Legacy = [V2025_11_25, V2025_06_18, V2025_03_26];

    public static bool IsModern(string? version) => version == V2026_07_28;

    public static bool IsLegacy(string? version) => version is V2025_11_25 or V2025_06_18 or V2025_03_26;

    /// <summary>outputSchema, structuredContent, title, resource links: 2025-06-18 and later.</summary>
    public static bool HasStructuredOutput(string version) => version != V2025_03_26;

    /// <summary>JSON-RPC batching existed only in 2025-03-26.</summary>
    public static bool AllowsBatch(string version) => version == V2025_03_26;

    /// <summary>SSE priming events (id + empty data) were introduced in 2025-11-25.</summary>
    public static bool UsesPrimingEvent(string version) => version == V2025_11_25;
}

internal static class JsonRpcCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>Session not found (legacy transport; same code the reference TypeScript SDK uses).</summary>
    public const int SessionNotFound = -32001;

    /// <summary>Resource not found in 2025-11-25 and earlier. Modern uses -32602.</summary>
    public const int LegacyResourceNotFound = -32002;

    public const int HeaderMismatch = -32020;

    /// <summary>Per-caller rate limit reached (resources/read, prompts/get; tools/call reports it as an isError result).</summary>
    public const int RateLimited = -32029;
    public const int MissingRequiredClientCapability = -32021;
    public const int UnsupportedProtocolVersion = -32022;
}

internal static class MetaKeys
{
    public const string ProtocolVersion = "io.modelcontextprotocol/protocolVersion";
    public const string ClientInfo = "io.modelcontextprotocol/clientInfo";
    public const string ClientCapabilities = "io.modelcontextprotocol/clientCapabilities";
    public const string LogLevel = "io.modelcontextprotocol/logLevel";
    public const string SubscriptionId = "io.modelcontextprotocol/subscriptionId";
    public const string ServerInfo = "io.modelcontextprotocol/serverInfo";
    public const string ProgressToken = "progressToken";
}

/// <summary>A protocol-level failure that becomes a JSON-RPC error response.</summary>
internal sealed class McpProtocolException : Exception
{
    public McpProtocolException(int code, string message, JsonNode? data = null, int httpStatus = 200)
        : base(message)
    {
        Code = code;
        Data2 = data;
        HttpStatus = httpStatus;
    }

    public int Code { get; }

    public JsonNode? Data2 { get; }

    /// <summary>HTTP status for the modern transport (legacy always uses 200 for method errors).</summary>
    public int HttpStatus { get; }
}

internal enum JsonRpcKind
{
    Invalid,
    Request,
    Notification,
    Response,
}

internal readonly struct JsonRpcMessage
{
    public JsonRpcKind Kind { get; init; }

    public JsonNode? Id { get; init; }

    public string? Method { get; init; }

    public JsonObject? Params { get; init; }

    public string? InvalidReason { get; init; }

    public static JsonRpcMessage Classify(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return new JsonRpcMessage { Kind = JsonRpcKind.Invalid, InvalidReason = "A JSON-RPC message must be a JSON object" };

        if (!obj.TryGetPropertyValue("jsonrpc", out var v) || v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String || jv.GetValue<string>() != "2.0")
            return new JsonRpcMessage { Kind = JsonRpcKind.Invalid, InvalidReason = "\"jsonrpc\" must be \"2.0\"", Id = SafeId(obj) };

        obj.TryGetPropertyValue("id", out var id);
        var hasId = obj.ContainsKey("id");
        if (hasId && !IsValidId(id))
            return new JsonRpcMessage { Kind = JsonRpcKind.Invalid, InvalidReason = "\"id\" must be a string or integer" };

        if (obj.TryGetPropertyValue("method", out var m))
        {
            if (m is not JsonValue mv || mv.GetValueKind() != JsonValueKind.String)
                return new JsonRpcMessage { Kind = JsonRpcKind.Invalid, InvalidReason = "\"method\" must be a string", Id = hasId ? id : null };

            JsonObject? prms = null;
            if (obj.TryGetPropertyValue("params", out var p) && p is not null)
            {
                if (p is not JsonObject po)
                    return new JsonRpcMessage { Kind = JsonRpcKind.Invalid, InvalidReason = "\"params\" must be an object", Id = hasId ? id : null };
                prms = po;
            }

            return new JsonRpcMessage
            {
                Kind = hasId ? JsonRpcKind.Request : JsonRpcKind.Notification,
                Id = hasId ? id : null,
                Method = mv.GetValue<string>(),
                Params = prms,
            };
        }

        if (hasId && (obj.ContainsKey("result") || obj.ContainsKey("error")))
            return new JsonRpcMessage { Kind = JsonRpcKind.Response, Id = id };

        return new JsonRpcMessage { Kind = JsonRpcKind.Invalid, InvalidReason = "Not a JSON-RPC request, notification or response", Id = hasId ? id : null };
    }

    private static JsonNode? SafeId(JsonObject obj) =>
        obj.TryGetPropertyValue("id", out var id) && IsValidId(id) ? id : null;

    private static bool IsValidId(JsonNode? id)
    {
        if (id is not JsonValue v)
            return false;
        return v.GetValueKind() switch
        {
            JsonValueKind.String => true,
            JsonValueKind.Number => v.TryGetValue<long>(out _) || (v.TryGetValue<double>(out var d) && Math.Floor(d) == d),
            _ => false,
        };
    }
}

internal static class JsonRpc
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = true,
    };

    public static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
            error["data"] = data;
        var response = new JsonObject { ["jsonrpc"] = "2.0" };
        if (id is not null)
            response["id"] = id.DeepClone();
        response["error"] = error;
        return response;
    }

    public static JsonObject Notification(string method, JsonObject? @params = null)
    {
        var n = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null)
            n["params"] = @params;
        return n;
    }

    public static byte[] Serialize(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            node.WriteTo(writer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Canonical text of a request id, used as a dictionary key ("5" vs "\"5\"" stay distinct).</summary>
    public static string IdKey(JsonNode? id) => id is null ? "null" : id.ToJsonString();

    public static string? GetString(JsonObject? obj, string name) =>
        obj is not null && obj.TryGetPropertyValue(name, out var v) && v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String
            ? jv.GetValue<string>()
            : null;

    public static string RequireString(JsonObject? obj, string name)
    {
        var s = GetString(obj, name);
        if (s is null)
            throw new McpProtocolException(JsonRpcCodes.InvalidParams, $"Missing required string parameter \"{name}\"");
        return s;
    }

    public static JsonObject? GetObject(JsonObject? obj, string name) =>
        obj is not null && obj.TryGetPropertyValue(name, out var v) ? v as JsonObject : null;
}

internal static class LogLevels
{
    private static readonly string[] Names = ["debug", "info", "notice", "warning", "error", "critical", "alert", "emergency"];

    public static string Name(McpLogLevel level) => Names[(int)level];

    public static bool TryParse(string? text, out McpLogLevel level)
    {
        for (var i = 0; i < Names.Length; i++)
        {
            if (Names[i] == text)
            {
                level = (McpLogLevel)i;
                return true;
            }
        }

        level = default;
        return false;
    }
}

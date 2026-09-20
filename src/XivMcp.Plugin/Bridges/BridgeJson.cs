using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XivMcp.Core;

namespace XivMcp.Plugin.Bridges;

/// <summary>
/// Defensive handling of what another plugin answers. Nothing a sibling sends is trusted to be small, well formed
/// or of the documented shape: answers are size-checked before parsing, their errors become
/// <see cref="McpToolException"/> carrying the sibling's own text, and what goes back to the MCP client is capped.
/// </summary>
public static class BridgeJson
{
    /// <summary>The most sibling data one tool result carries (64 KiB of JSON text, measured as UTF-8).</summary>
    public const int MaxResultBytes = 64 * 1024;

    /// <summary>An answer longer than this is not even parsed (a paged tool may parse more than it returns).</summary>
    public const int MaxParseChars = 4 * 1024 * 1024;

    /// <summary>How much of a sibling's error text is repeated.</summary>
    public const int MaxErrorChars = 500;

    /// <summary>Most bytes a string argument sent to a sibling may have (GhosttyDalamud refuses more).</summary>
    public const int MaxArgumentBytes = 1024;

    /// <summary>A capped copy of sibling data: the node, or a raw prefix when it was too large to return whole.</summary>
    public sealed record Capped(JsonNode? Node, bool Truncated, string? RawPrefix);

    /// <summary>
    /// Reads a <c>{"ok":true,"result":…}</c> / <c>{"ok":false,"error":"why"}</c> envelope (GhosttyDalamud's Call gate, and
    /// the style proposed for Almanac). Returns the result. An "unknown method" error becomes
    /// <see cref="BridgeCapabilityMissingException"/>; any other error a tool error with the sibling's words.
    /// </summary>
    public static JsonNode? ParseEnvelope(string sibling, string gate, string method, string? raw)
    {
        if (ParseJson(sibling, gate, raw) is not JsonObject envelope || envelope["ok"] is not JsonValue ok ||
            ok.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new McpToolException($"{sibling} answered {gate} {method} without an {{\"ok\": …}} envelope; its IPC may have changed.");
        }

        if (ok.GetValueKind() == JsonValueKind.True)
        {
            return envelope["result"]?.DeepClone();
        }

        var error = Clip(Text(envelope["error"]) ?? "no reason given", MaxErrorChars);
        if (IsUnknownMethod(error))
        {
            throw new BridgeCapabilityMissingException($"{gate}#{method}");
        }

        throw new McpToolException($"{sibling} refused {method}: {error}");
    }

    /// <summary>Parses a JSON payload, refusing empty, oversized and malformed answers.</summary>
    public static JsonNode? ParseJson(string sibling, string gate, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{sibling} answered {gate} with nothing.", retryable: true);
        }

        if (raw.Length > MaxParseChars)
        {
            throw new McpToolException($"{sibling} answered {gate} with {raw.Length} characters; more than {MaxParseChars} is not read.");
        }

        try
        {
            return JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            throw new McpToolException($"{sibling} answered {gate} with something that is not JSON: {Clip(raw, 200)}");
        }
    }

    /// <summary>Reads an <c>"ok: …"</c> / <c>"error: …"</c> text reply (XivDesktop's change gates). Returns the text after "ok:".</summary>
    public static string ParseTextReply(string sibling, string gate, string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
        {
            return Clip(text.Length > 2 ? text[2..].TrimStart(':', ' ') : "", MaxErrorChars);
        }

        if (text.Length == 0)
        {
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{sibling} answered {gate} with nothing.", retryable: true);
        }

        var reason = text.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? text[5..].TrimStart(':', ' ') : text;
        throw new McpToolException($"{sibling} refused ({gate}): {Clip(reason, MaxErrorChars)}");
    }

    /// <summary>The wording GhosttyDalamud uses, and the wording every proposed JSON gate is asked to use.</summary>
    public static bool IsUnknownMethod(string? error) =>
        error is not null && error.TrimStart().StartsWith("unknown method", StringComparison.OrdinalIgnoreCase);

    /// <summary>Caps sibling data for the tool result: whole when it fits in <see cref="MaxResultBytes"/>, else a raw prefix and a flag.</summary>
    public static Capped Cap(JsonNode? node)
    {
        if (node is null)
        {
            return new Capped(null, false, null);
        }

        var text = node.ToJsonString();
        if (Encoding.UTF8.GetByteCount(text) <= MaxResultBytes)
        {
            return new Capped(node, false, null);
        }

        return new Capped(null, true, ClipBytes(text, MaxResultBytes));
    }

    /// <summary>
    /// The check GhosttyDalamud applies to strings, applied before sending so the refusal names the argument:
    /// at most 1024 bytes of UTF-8 and no control characters (so no line breaks either).
    /// </summary>
    public static string CheckArgument(string name, string? value, bool allowEmpty = false, int maxBytes = MaxArgumentBytes)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 && !allowEmpty)
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"{name} is required.");
        }

        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"{name} is longer than {maxBytes} bytes.");
        }

        if (text.Any(c => char.IsControl(c) || c is (char)0x2028 or (char)0x2029))
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"{name} must not contain control characters or line breaks.");
        }

        return text;
    }

    /// <summary>First <paramref name="max"/> characters, with an ellipsis when cut.</summary>
    public static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    /// <summary>String value of a node; null for anything that is not a string.</summary>
    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>String member of an object; null when absent or not a string.</summary>
    public static string? Text(JsonNode? parent, string key) => parent is JsonObject o ? Text(o[key]) : null;

    /// <summary>Integer member of an object; null when absent, not a number or not integral.</summary>
    public static long? Integer(JsonNode? parent, string key)
    {
        if (parent is not JsonObject o || o[key] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        return value.TryGetValue<double>(out var d) && Math.Abs(d) < 9e15 && Math.Floor(d) == d ? (long)d : null;
    }

    /// <summary>Boolean member of an object; null when absent or not a boolean.</summary>
    public static bool? Flag(JsonNode? parent, string key) =>
        parent is JsonObject o && o[key] is JsonValue value
            ? value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>Array member of an object as objects only; empty when absent or of another shape.</summary>
    public static List<JsonObject> Objects(JsonNode? parent, string key) =>
        parent is JsonObject o && o[key] is JsonArray array ? array.OfType<JsonObject>().ToList() : [];

    private static string ClipBytes(string text, int maxBytes)
    {
        var length = Math.Min(text.Length, maxBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > maxBytes)
        {
            length -= Math.Max(1, length / 16);
        }

        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
}

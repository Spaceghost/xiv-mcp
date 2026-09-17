using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivMcp.Core;

/// <summary>
/// The one JSON configuration used for everything the server emits: tool results
/// (<c>structuredContent</c> and the text mirror), resource bodies and generated schemas.
/// camelCase properties, enums as camelCase strings, nulls omitted.
/// </summary>
public static class McpJson
{
    /// <summary>Shared, read-only options. Do not mutate.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: false);

    /// <summary>Same as <see cref="Options"/> but indented (for human-facing text).</summary>
    public static JsonSerializerOptions IndentedOptions { get; } = CreateOptions(indented: true);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 64,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

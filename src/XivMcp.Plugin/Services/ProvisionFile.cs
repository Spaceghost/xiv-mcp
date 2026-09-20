using System.Text.Json;

namespace XivMcp.Plugin.Services;

/// <summary>
/// Settings handed to the plugin by an outside provisioning file. Every field is optional: only the keys
/// actually present in the file override the saved configuration, and the plugin never writes any of this
/// back. Field names double as the keys <see cref="Configuration.IsProvisioned"/> takes.
/// </summary>
public sealed class ProvisionDocument
{
    public const string FileName = "provision.json";

    /// <summary>The keys the file actually carried, so the UI can grey out exactly those settings.</summary>
    public required IReadOnlySet<string> Present { get; init; }

    /// <summary>Absolute path the document was read from (shown in the UI).</summary>
    public required string Source { get; init; }

    public bool? Enabled { get; init; }

    public BindMode? BindMode { get; init; }

    public string? CustomHost { get; init; }

    public int? Port { get; init; }

    public string? Path { get; init; }

    public bool? RequireToken { get; init; }

    /// <summary>Bearer token for config-managed machines. Never logged, never echoed into the plugin's own config file.</summary>
    public string? BearerToken { get; init; }

    public List<string>? AllowedOrigins { get; init; }

    public int? CallTimeoutSeconds { get; init; }

    public int? ConfirmTimeoutSeconds { get; init; }

    public List<string>? DisabledCategories { get; init; }

    public bool Has(string field) => Present.Contains(field);

    /// <summary>True when the file provisions nothing the plugin understands.</summary>
    public bool IsEmpty => Present.Count == 0;
}

/// <summary>
/// Reads the optional provisioning file. Read-only by contract: nothing here opens the file for writing.
/// Parsing is strict about the values it does understand (a bad port is an error, not a silent clamp) and
/// tolerant of keys it does not, so a newer file does not break an older plugin.
/// </summary>
public static class ProvisionFile
{
    /// <summary>Environment variable that names the file outright.</summary>
    public const string PathVariable = "XIVMCP_PROVISION";

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    /// <summary>Largest file accepted; a provisioning file is a handful of settings.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// Where the provisioning file lives: <c>XIVMCP_PROVISION</c> if set, else
    /// <c>$XDG_CONFIG_HOME/xiv-mcp/provision.json</c>, else <c>$HOME/.config/xiv-mcp/provision.json</c>.
    /// Under Wine the Unix paths are rewritten onto the <c>Z:</c> drive, the same way objective packs are.
    /// Returns null when none of the variables are set.
    /// </summary>
    public static string? ResolvePath(Func<string, string?> environment, bool windows)
    {
        var explicitPath = environment(PathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return ToPlatformPath(explicitPath.Trim().Trim('"'), windows);

        var xdg = environment("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return ToPlatformPath(Join(xdg.Trim().TrimEnd('/', '\\'), "xiv-mcp", ProvisionDocument.FileName), windows);

        var home = environment("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = environment("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home))
            return null;

        return ToPlatformPath(Join(home.Trim().TrimEnd('/', '\\'), ".config", "xiv-mcp", ProvisionDocument.FileName), windows);
    }

    /// <summary>Resolves the path for the machine this code is running on.</summary>
    public static string? ResolvePath() =>
        ResolvePath(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());

    private static string Join(string root, params string[] parts) => root + "/" + string.Join('/', parts);

    /// <summary>Maps a Unix absolute path onto Wine's <c>Z:</c> drive when running as a Windows process.</summary>
    public static string ToPlatformPath(string path, bool windows)
    {
        if (!windows || !path.StartsWith('/'))
            return path;
        return "Z:" + path.Replace('/', '\\');
    }

    /// <summary>
    /// Parses the file's text. Throws <see cref="ArgumentException"/> with a message fit for the UI when the
    /// file is not valid JSON or carries a value the plugin cannot use.
    /// </summary>
    public static ProvisionDocument Parse(string json, string source)
    {
        if (json.Length > MaxBytes)
            throw new ArgumentException($"Provision file is larger than {MaxBytes / 1024} KiB.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Provision file is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Provision file must be a JSON object.");

            var present = new HashSet<string>(StringComparer.Ordinal);
            return new ProvisionDocument
            {
                Source = source,
                Present = present,
                Enabled = Bool(root, nameof(ProvisionDocument.Enabled), present),
                BindMode = Mode(root, present),
                CustomHost = Text(root, nameof(ProvisionDocument.CustomHost), present, 128),
                Port = Number(root, nameof(ProvisionDocument.Port), present, 1, 65535),
                Path = EndpointPath(root, present),
                RequireToken = Bool(root, nameof(ProvisionDocument.RequireToken), present),
                BearerToken = Text(root, nameof(ProvisionDocument.BearerToken), present, 512),
                AllowedOrigins = Strings(root, nameof(ProvisionDocument.AllowedOrigins), present),
                CallTimeoutSeconds = Number(root, nameof(ProvisionDocument.CallTimeoutSeconds), present, 5, 600),
                ConfirmTimeoutSeconds = Number(root, nameof(ProvisionDocument.ConfirmTimeoutSeconds), present, 5, 300),
                DisabledCategories = Strings(root, nameof(ProvisionDocument.DisabledCategories), present),
            };
        }
    }

    /// <summary>
    /// Reads and parses the file. Returns null when it does not exist (the normal case). Throws
    /// <see cref="ArgumentException"/> for a malformed file and <see cref="IOException"/> when it cannot be read.
    /// </summary>
    public static ProvisionDocument? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path);
        return Parse(text, path);
    }

    private static bool? Bool(JsonElement root, string name, HashSet<string> present)
    {
        if (!TryGet(root, name, out var value))
            return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"\"{name}\" must be true or false.");
        present.Add(name);
        return value.GetBoolean();
    }

    private static int? Number(JsonElement root, string name, HashSet<string> present, int min, int max)
    {
        if (!TryGet(root, name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new ArgumentException($"\"{name}\" must be a whole number.");
        if (number < min || number > max)
            throw new ArgumentException($"\"{name}\" must be between {min} and {max}.");
        present.Add(name);
        return number;
    }

    private static string? Text(JsonElement root, string name, HashSet<string> present, int maxLength)
    {
        if (!TryGet(root, name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"\"{name}\" must be a string.");
        var text = value.GetString()?.Trim() ?? "";
        if (text.Length == 0)
            throw new ArgumentException($"\"{name}\" must not be empty.");
        if (text.Length > maxLength)
            throw new ArgumentException($"\"{name}\" is longer than {maxLength} characters.");
        present.Add(name);
        return text;
    }

    private static string? EndpointPath(JsonElement root, HashSet<string> present)
    {
        var text = Text(root, nameof(ProvisionDocument.Path), present, 128);
        if (text is null)
            return null;
        if (!text.StartsWith('/'))
            text = "/" + text;
        if (text.Length > 1)
            text = text.TrimEnd('/');
        if (text.Any(char.IsWhiteSpace))
            throw new ArgumentException("\"Path\" must not contain spaces.");
        return text;
    }

    private static BindMode? Mode(JsonElement root, HashSet<string> present)
    {
        var name = nameof(ProvisionDocument.BindMode);
        if (!TryGet(root, name, out var value))
            return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.String when Enum.TryParse<BindMode>(value.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed):
                present.Add(name);
                return parsed;
            case JsonValueKind.Number when value.TryGetInt32(out var number) && Enum.IsDefined((BindMode)number):
                present.Add(name);
                return (BindMode)number;
            default:
                throw new ArgumentException($"\"{name}\" must be one of {string.Join(", ", Enum.GetNames<BindMode>())}.");
        }
    }

    private static List<string>? Strings(JsonElement root, string name, HashSet<string> present)
    {
        if (!TryGet(root, name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"\"{name}\" must be an array of strings.");
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"\"{name}\" must be an array of strings.");
            var text = item.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text) && !list.Contains(text, StringComparer.OrdinalIgnoreCase))
                list.Add(text);
        }

        present.Add(name);
        return list;
    }

    /// <summary>Case-insensitive lookup, so <c>bindMode</c> and <c>BindMode</c> both work. Null members count as absent.</summary>
    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.ValueKind == JsonValueKind.Null)
                break;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}

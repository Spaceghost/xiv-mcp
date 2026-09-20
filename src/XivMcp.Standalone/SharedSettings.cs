using System.Text.Json;
using XivMcp.Core;

namespace XivMcp.Standalone;

/// <summary>
/// The endpoint and token settings the standalone host shares with the plugin, so a client configured for one works
/// against the other. Layered, later wins: built-in defaults, the plugin's own saved configuration
/// (<c>pluginConfigs/XivMcp.json</c>, read-only), the provisioning file (<c>provision.json</c>, the same one the plugin
/// reads), then environment and command line. Secrets are read, never written and never logged.
/// </summary>
internal sealed record SharedSettings
{
    public int Port { get; init; } = 41800;

    public string Path { get; init; } = "/mcp";

    public bool RequireToken { get; init; } = true;

    public string? BearerToken { get; init; }

    public IReadOnlyList<ClientToken> ClientTokens { get; init; } = [];

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>0 loopback, 1 loopback + tailnet, 2 tailnet only, 3 custom: the plugin's BindMode numbers.</summary>
    public int BindMode { get; init; }

    public string? CustomHost { get; init; }

    /// <summary>Which files contributed, for the startup banner (paths only, never contents).</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 };

    /// <summary>Overlays one JSON settings document (plugin configuration or provisioning file). Unknown or malformed values are ignored.</summary>
    public SharedSettings Overlay(string? json, string source)
    {
        if (string.IsNullOrWhiteSpace(json))
            return this;
        try
        {
            using var document = JsonDocument.Parse(json, Lenient);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return this;

            var next = this with { Sources = [.. Sources, source] };
            foreach (var property in root.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name.ToLowerInvariant())
                {
                    case "port" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) && port is >= 1 and <= 65535:
                        next = next with { Port = port };
                        break;
                    case "path" when value.ValueKind == JsonValueKind.String && NormalizePath(value.GetString()) is { } path:
                        next = next with { Path = path };
                        break;
                    case "requiretoken" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        next = next with { RequireToken = value.GetBoolean() };
                        break;
                    case "bearertoken" when value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } token:
                        next = next with { BearerToken = token };
                        break;
                    case "bindmode" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var mode) && mode is >= 0 and <= 3:
                        next = next with { BindMode = mode };
                        break;
                    case "bindmode" when value.ValueKind == JsonValueKind.String:
                        next = next with { BindMode = ParseBindMode(value.GetString()) ?? next.BindMode };
                        break;
                    case "customhost" when value.ValueKind == JsonValueKind.String:
                        next = next with { CustomHost = value.GetString() };
                        break;
                    case "allowedorigins" when value.ValueKind == JsonValueKind.Array:
                        next = next with { AllowedOrigins = [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(v => v.Length > 0)] };
                        break;
                    case "clienttokens" when value.ValueKind == JsonValueKind.Array:
                        next = next with { ClientTokens = ReadClientTokens(value) };
                        break;
                }
            }

            return next;
        }
        catch (JsonException)
        {
            return this;
        }
    }

    internal static int? ParseBindMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "loopback" => 0,
        "loopbackandtailnet" or "loopback+tailnet" => 1,
        "tailnetonly" or "tailnet" => 2,
        "custom" => 3,
        _ => null,
    };

    internal static string? NormalizePath(string? text)
    {
        var p = (text ?? "").Trim();
        if (p.Length == 0)
            return null;
        if (!p.StartsWith('/'))
            p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    private static List<ClientToken> ReadClientTokens(JsonElement array)
    {
        var tokens = new List<ClientToken>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            string? name = null, hash = null;
            foreach (var p in entry.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    continue;
                if (p.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    name = p.Value.GetString();
                else if (p.Name.Equals("TokenSha256", StringComparison.OrdinalIgnoreCase))
                    hash = p.Value.GetString();
            }

            if (name is { Length: > 0 and <= 64 } && hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit))
                tokens.Add(new ClientToken(name, hash));
        }

        return tokens;
    }

    /// <summary>Where the plugin keeps its configuration, per launcher. The first that exists is used.</summary>
    public static IEnumerable<string> PluginConfigCandidates(string home, string? appData)
    {
        yield return System.IO.Path.Combine(home, ".xlcore", "pluginConfigs", "XivMcp.json");
        yield return System.IO.Path.Combine(home, ".var", "app", "dev.goats.xivlauncher", "data", "xlcore", "pluginConfigs", "XivMcp.json");
        if (!string.IsNullOrEmpty(appData))
            yield return System.IO.Path.Combine(appData, "XIVLauncher", "pluginConfigs", "XivMcp.json");
    }

    /// <summary>The provisioning file the plugin also reads: $XIVMCP_PROVISION, else $XDG_CONFIG_HOME/xiv-mcp/provision.json, else ~/.config/xiv-mcp/provision.json.</summary>
    public static string ProvisionPath(Func<string, string?> environment, string home)
    {
        if (environment("XIVMCP_PROVISION") is { Length: > 0 } explicitPath)
            return explicitPath.Trim().Trim('"');
        var config = environment("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : System.IO.Path.Combine(home, ".config");
        return System.IO.Path.Combine(config, "xiv-mcp", "provision.json");
    }
}

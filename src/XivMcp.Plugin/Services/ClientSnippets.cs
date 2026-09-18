using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Plugin.Services;

/// <summary>Copy-able client configuration for the Status tab. The token is substituted only when asked.</summary>
public static class ClientSnippets
{
    public const string ServerName = "ffxiv";
    public const string TokenPlaceholder = "<token>";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>`claude mcp add` at user scope with the token as a static header.</summary>
    public static string ClaudeCode(Configuration config, TokenDisplay display)
    {
        var header = config.RequireToken ? $" --header \"Authorization: Bearer {Token(config, display)}\"" : "";
        return $"claude mcp add --scope user --transport http {ServerName} {config.EndpointUrl}{header}";
    }

    /// <summary>Generic mcpServers JSON (Claude Desktop-style / .mcp.json / most HTTP-capable clients).</summary>
    public static string GenericJson(Configuration config, TokenDisplay display)
    {
        var server = new JsonObject
        {
            ["type"] = "http",
            ["url"] = config.EndpointUrl,
        };
        if (config.RequireToken)
            server["headers"] = new JsonObject { ["Authorization"] = $"Bearer {Token(config, display)}" };

        var root = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = server } };
        return root.ToJsonString(Indented);
    }

    private static string Token(Configuration config, TokenDisplay display) => display switch
    {
        TokenDisplay.Real => config.BearerToken,
        _ => TokenPlaceholder,
    };
}

public enum TokenDisplay
{
    Placeholder,
    Real,
}

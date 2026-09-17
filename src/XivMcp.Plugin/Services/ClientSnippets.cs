using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Plugin.Services;

/// <summary>Copy-able client configuration for the Status tab. The token is substituted only when asked.</summary>
public static class ClientSnippets
{
    public const string ServerName = "xiv-mcp";
    public const string TokenPlaceholder = "<token>";
    private const string Mask = "••••••••••••••••";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Recommended: the repo helper registers a headersHelper so the token is read at connect time.</summary>
    public static string ClaudeCodeHelper(Configuration config) =>
        $"tools/claude-mcp-add.sh --url {config.EndpointUrl}";

    /// <summary>Plain `claude mcp add` with a static header (stores the token in Claude's config).</summary>
    public static string ClaudeCodeStatic(Configuration config, TokenDisplay display)
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
        TokenDisplay.Masked => Mask,
        _ => TokenPlaceholder,
    };
}

public enum TokenDisplay
{
    Placeholder,
    Masked,
    Real,
}

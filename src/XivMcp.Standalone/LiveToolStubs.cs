using System.Reflection;
using System.Text.Json.Nodes;
using XivMcp.Core;

namespace XivMcp.Standalone;

/// <summary>
/// Publishes every tool of the plugin's catalogue that this host does not serve itself, under the same name, schema
/// and annotations, answering with the structured <see cref="McpErrorCodes.GameNotRunning"/> error. A client's tool list
/// is therefore the same whichever host holds the port; only what a live tool answers differs.
/// </summary>
internal static class LiveToolStubs
{
    public const string CatalogueResource = "XivMcp.Standalone.tools.json";

    public static JsonObject LoadCatalogue()
    {
        using var stream = typeof(LiveToolStubs).Assembly.GetManifestResourceStream(CatalogueResource)
                           ?? throw new InvalidOperationException($"embedded resource {CatalogueResource} is missing");
        return JsonNode.Parse(stream) as JsonObject ?? throw new InvalidOperationException("docs/tools.json is not a JSON object");
    }

    /// <summary>One stub per catalogue tool whose name is not in <paramref name="served"/>.</summary>
    /// <param name="gameDataMissing">No game installation was found: static tools are stubbed too, with a different message.</param>
    public static List<ExternalTool> Build(JsonObject catalogue, IReadOnlySet<string> served, bool gameDataMissing)
    {
        var stubs = new List<ExternalTool>();
        foreach (var node in catalogue["tools"]?.AsArray() ?? [])
        {
            if (node is not JsonObject tool || tool["name"]?.GetValue<string>() is not { Length: > 0 } name || served.Contains(name))
                continue;

            var isStatic = tool["availability"]?.GetValue<string>() == "static";
            var message = isStatic && gameDataMissing
                ? $"Tool '{name}' needs the installed game's data files, and no FINAL FANTASY XIV installation was found. Start xiv-mcp-standalone with --game <path> (or set XIVMCP_GAME)."
                : $"Tool '{name}' needs the running game: FINAL FANTASY XIV is not running (this is the standalone xiv-mcp host, which serves static game data only). " +
                  "Ask the player to start the game; the plugin then takes over this same endpoint and the tool works. Tools marked availability=static work now.";
            var code = isStatic && gameDataMissing ? McpErrorCodes.Unavailable : McpErrorCodes.GameNotRunning;

            stubs.Add(new ExternalTool
            {
                Name = name,
                Title = tool["title"]?.GetValue<string>(),
                Description = tool["description"]?.GetValue<string>() ?? "",
                Category = tool["category"]?.GetValue<string>() ?? "general",
                Permission = ParsePermission(tool["permission"]?.GetValue<string>()),
                RequiresApproval = tool["needsApproval"]?.GetValue<bool>() == true,
                Destructive = tool["destructiveHint"]?.GetValue<bool>() == true,
                Idempotent = tool["idempotentHint"]?.GetValue<bool>() != false,
                OpenWorld = tool["openWorldHint"]?.GetValue<bool>() == true,
                Availability = isStatic ? ToolAvailability.Static : ToolAvailability.Live,
                Sources = [.. (tool["dataSources"]?.AsArray() ?? []).Select(s => s!.GetValue<string>())],
                ApprovalSummary = tool["approvalSummary"]?.GetValue<string>(),
                InputSchema = tool["inputSchema"]?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "object" },
                OutputSchema = tool["outputSchema"]?.DeepClone() as JsonObject,
                Handler = (_, _) => throw McpToolException.WithCode(code, message, retryable: true),
            });
        }

        return stubs;
    }

    private static ToolPermission ParsePermission(string? text) => text switch
    {
        "ui" => ToolPermission.Ui,
        "action" => ToolPermission.Action,
        "chat" => ToolPermission.Chat,
        _ => ToolPermission.Read,
    };
}

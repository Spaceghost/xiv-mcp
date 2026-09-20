using XivMcp.Core;
using XivMcp.Plugin.Providers.Meta;

namespace XivMcp.Standalone;

/// <summary>What the info tool needs to know about this host.</summary>
internal sealed record StandaloneFacts(string Version, Func<string> Endpoint, DateTimeOffset StartedAt, string? GameVersion, string Language, McpServer Server);

/// <summary>
/// get_server_info for the pre-game host: the same name, arguments and result shape as the plugin's, with
/// host = "standalone" and gameRunning = false so an agent can plan around it.
/// </summary>
[McpProvider("meta")]
internal sealed class StandaloneInfoProvider(StandaloneFacts facts)
{
    [McpTool("get_server_info",
        Title = "Server info, enabled tiers and categories",
        Description =
            "Describes this XivMcp server. This is the STANDALONE host (host = \"standalone\", gameRunning = false): FINAL FANTASY XIV is not running, " +
            "so only tools with availability \"static\" work (game data lookups: items, recipes, quests, duties, actions, sheets, zones, " +
            "weather forecast, Eorzea time); every other tool is listed with its normal schema and answers with the game_not_running error. " +
            "Once the game starts, the in-game plugin takes over this same endpoint and token. Returns the same fields as the plugin's get_server_info.",
        GameThread = false,
        RequiresLogin = false,
        Availability = ToolAvailability.Static,
        Sources = ["xivmcp:server", "lumina:GameData.Repositories"])]
    public ServerInfoDto GetServerInfo(ToolContext ctx)
    {
        var status = facts.Server.GetStatus();
        var catalogue = facts.Server.ExportCatalogue()["tools"]!.AsArray();
        var statics = catalogue.Count(t => t!["availability"]!.GetValue<string>() == "static");
        var categories = catalogue
            .GroupBy(t => t!["category"]!.GetValue<string>(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CategoryInfoDto(g.Key, true, g.Count(), 0))
            .ToArray();

        return new ServerInfoDto(
            "xiv-mcp",
            facts.Version,
            facts.Endpoint(),
            "streamable-http",
            status.Running,
            facts.StartedAt,
            status.ActiveSessions,
            status.ConnectedClients,
            ctx.ClientName,
            ctx.SessionId,
            new PermissionTiersDto(true, false, false, false, true, "Standalone host: nothing can be changed because the game is not running; the player's tiers and approval switch apply once the plugin takes over."),
            catalogue.Count,
            statics,
            categories,
            null,
            facts.GameVersion,
            facts.Language,
            false,
            ctx.ProtocolVersion)
        {
            Host = "standalone",
            GameRunning = false,
            CatalogueVersion = McpServer.CatalogueVersion,
            StaticTools = statics,
            RateLimitPerMinute = facts.Server.Options.RateLimitPerMinute,
        };
    }
}

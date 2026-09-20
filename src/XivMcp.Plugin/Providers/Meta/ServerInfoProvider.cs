using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Providers.Meta;

/// <summary>Self-description of the server so an agent can plan around what the player enabled.</summary>
[McpProvider("meta")]
public sealed class ServerInfoProvider
{
    private readonly ServerHost host;
    private readonly Configuration config;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager data;
    private readonly IClientState clientState;

    public ServerInfoProvider(ServerHost host, Configuration config, IDalamudPluginInterface pluginInterface, IDataManager data, IClientState clientState)
    {
        this.host = host;
        this.config = config;
        this.pluginInterface = pluginInterface;
        this.data = data;
        this.clientState = clientState;
    }

    [McpTool("get_server_info",
        Title = "Server info, enabled tiers and categories",
        Description =
            "Describes this XivMcp server: plugin version, endpoint, running state, connected clients, which permission " +
            "tiers are currently allowed (read, ui, action, chat — and whether state-changing calls need in-game approval: permissions.confirmActions is the player's single approval switch), " +
            "host (\"plugin\" inside the running game, \"standalone\" before the game starts: there only availability=static tools work and the rest answer game_not_running), " +
            "each tool category with its enabled flag and tool count, Dalamud and game versions, client language, " +
            "whether a character is logged in and the MCP protocolVersion negotiated for this call. Call this first to learn what you can do before planning; a tier or " +
            "category that is off means its tools are unavailable until the player enables them in /xivmcp settings.",
        GameThread = false,
        RequiresLogin = false,
        Availability = ToolAvailability.Static,
        Sources = ["xivmcp:server", "dalamud:IDalamudPluginInterface", "lumina:GameData.Repositories"])]
    public async Task<ServerInfoDto> GetServerInfo(ToolContext ctx)
    {
        var loggedIn = await ctx.Game.InvokeAsync(() => clientState.IsLoggedIn, ctx.CancellationToken).ConfigureAwait(false);
        var status = host.Status;
        var hostState = host.HostState;
        var tools = host.ListTools();
        var providers = host.Providers;

        string? note = null;
        if (config.ConfirmActions && (config.AllowAction || config.AllowChat))
            note = $"Each Action/Chat call waits up to {config.ConfirmTimeoutSeconds} s for the player to click Allow in game; a denied or unanswered call returns an error and did not run.";

        var permissions = new PermissionTiersDto(
            hostState.IsPermitted(ToolPermission.Read),
            hostState.IsPermitted(ToolPermission.Ui),
            hostState.IsPermitted(ToolPermission.Action),
            hostState.IsPermitted(ToolPermission.Chat),
            config.ConfirmActions,
            note);

        var categories = host.Categories
            .Select(c => new CategoryInfoDto(
                c,
                config.IsCategoryEnabled(c),
                tools.Count(t => string.Equals(t.Category, c, StringComparison.OrdinalIgnoreCase)),
                providers.Count(p => !p.Loaded && string.Equals(p.Category, c, StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        var available = tools.Count(t => hostState.IsPermitted(t.Permission) && config.IsCategoryEnabled(t.Category));

        string? gameVersion = null;
        try
        {
            if (data.GameData.Repositories.TryGetValue("ffxiv", out var repo))
                gameVersion = repo.Version;
        }
        catch
        {
            // Version file unreadable: report null rather than fail the call.
        }

        return new ServerInfoDto(
            "xiv-mcp",
            host.PluginVersion,
            host.Endpoint,
            "streamable-http",
            host.IsRunning,
            host.StartedAt,
            status.ActiveSessions,
            status.ConnectedClients,
            ctx.ClientName,
            ctx.SessionId,
            permissions,
            tools.Count,
            available,
            categories,
            pluginInterface.GetDalamudVersion().Version.ToString(),
            gameVersion,
            clientState.ClientLanguage.ToString(),
            loggedIn,
            ctx.ProtocolVersion)
        {
            Host = "plugin",
            GameRunning = true,
            CatalogueVersion = McpServer.CatalogueVersion,
            StaticTools = host.Server.ExportCatalogue()["tools"]!.AsArray().Count(t => t!["availability"]!.GetValue<string>() == "static"),
            RateLimitPerMinute = host.Server.Options.RateLimitPerMinute,
        };
    }
}

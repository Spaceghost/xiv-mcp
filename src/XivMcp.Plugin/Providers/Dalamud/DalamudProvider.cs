using System.Globalization;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using XivMcp.Core;

// Not "Providers.Dalamud": a namespace segment named Dalamud would shadow the real Dalamud.* namespaces
// for fully qualified names in every sibling XivMcp.Plugin.Providers.* namespace.
namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>Information about Dalamud itself and the installed plugins.</summary>
[McpProvider("dalamud")]
public sealed unsafe class DalamudProvider
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IGameConfig gameConfig;

    public DalamudProvider(IDalamudPluginInterface pluginInterface, IClientState clientState, ICondition condition, IDataManager dataManager, IGameGui gameGui, IGameConfig gameConfig)
    {
        this.pluginInterface = pluginInterface;
        this.clientState = clientState;
        this.condition = condition;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.gameConfig = gameConfig;
    }

    [McpTool("list_plugins",
        Sources = ["dalamud:InstalledPlugins", "dalamud:internal.PluginManager"],
        Title = "List Dalamud plugins",
        Description =
            "Lists the Dalamud plugins installed in this game client. Each entry: internalName, name, version, author, loaded (currently running), isDev (local dev plugin), isThirdParty (from a custom repository), isTesting, isOutdated, isBanned/isOrphaned/isDecommissioned (only when true), hasMainUi/hasConfigUi, apiLevel, installedFromUrl (the repository the plugin was installed from: a custom repository URL, scrubbed of credentials, or Dalamud's markers OFFICIAL / DEVPLUGIN), projectUrl (the manifest's source link), " +
            "state (Dalamud's load state: Loaded, Unloaded, LoadError, UnloadError, Loading, Unloading, DependencyResolutionFailed) and updateAvailable. state and updateAvailable come from Dalamud's plugin manager, which is not public API: they are omitted when this Dalamud build does not expose them, and updateAvailable only reflects the repository data Dalamud last fetched (what its installer shows), not a fresh check. " +
            "Use to check whether a plugin the user mentions is installed and enabled before suggesting its commands. Sorted by name; truncated=true when more match than limit.",
        Permission = ToolPermission.Read, GameThread = false, RequiresLogin = false)]
    public PluginListResult ListPlugins(
        [McpParam("Case-insensitive substring filter on name or internal name.")] string? nameContains = null,
        [McpParam("Only plugins that are currently loaded.")] bool loadedOnly = false,
        [McpParam("Maximum entries to return.", Minimum = 1, Maximum = 500)] int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 500);
        var all = new List<PluginInfo>();
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in DalamudInternals.GetPlugins() ?? [])
            states[p.InternalName] = states.TryGetValue(p.InternalName, out var other) && other == "Loaded" ? other : p.State;
        var updatable = DalamudInternals.GetUpdatableInternalNames();
        foreach (var plugin in pluginInterface.InstalledPlugins)
        {
            if (loadedOnly && !plugin.IsLoaded)
                continue;
            if (!string.IsNullOrEmpty(nameContains) &&
                !plugin.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase) &&
                !plugin.InternalName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                continue;

            string? author = null;
            int? apiLevel = null;
            string? installedFrom = null, projectUrl = null;
            try
            {
                author = plugin.Manifest?.Author;
                apiLevel = plugin.Manifest?.DalamudApiLevel;
                installedFrom = plugin.Manifest?.InstalledFromUrl;
                projectUrl = plugin.Manifest?.RepoUrl;
            }
            catch
            {
                // Manifest data is informational only.
            }

            all.Add(new PluginInfo(
                plugin.InternalName,
                plugin.Name,
                plugin.Version?.ToString(),
                string.IsNullOrEmpty(author) ? null : author,
                plugin.IsLoaded,
                plugin.IsDev,
                plugin.IsThirdParty,
                plugin.IsTesting,
                plugin.IsOutdated,
                plugin.IsBanned ? true : null,
                plugin.IsOrphaned ? true : null,
                plugin.IsDecommissioned ? true : null,
                plugin.HasMainUi,
                plugin.HasConfigUi,
                apiLevel,
                CleanUrl(installedFrom),
                CleanUrl(projectUrl),
                states.GetValueOrDefault(plugin.InternalName),
                updatable is null ? null : updatable.Contains(plugin.InternalName)));
        }

        all.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var page = all.Take(limit).ToList();
        return new PluginListResult(page, all.Count, all.Count > page.Count);
    }

    [McpTool("get_dalamud_info",
        Sources = ["dalamud:IDalamudPluginInterface", "dalamud:internal.PluginManager", "lumina:GameData.Repositories"],
        Title = "Dalamud and client info",
        Description =
            "Returns environment facts about this game client: dalamudVersion, dalamudApiLevel, dalamudScmVersion/gitHash/betaTrack when known, dalamudTrack (the beta track name, or \"release\" when Dalamud reports none), clientStructsGitHash, pluginSafeMode (plugins were not loaded this session; read from Dalamud's plugin manager, which is not public API, so it is omitted when unreachable), gameVersion (ffxiv) and expansionVersions, clientLanguage (game data language), dalamudUiLanguage, " +
            "loggedIn, isPvP, inGpose, inCutscene, gameUiHidden, globalUiScale (the game's UI scale factor), colorTheme (System Configuration theme: dark, light, classicFF, clearBlue) and hasModifiedGameDataFiles, plus plugin counts. " +
            "Use for troubleshooting or to adapt behaviour to the client's language and state.",
        Permission = ToolPermission.Read, RequiresLogin = false)]
    public DalamudInfoResult GetDalamudInfo()
    {
        string? dalamudVersion = null, scm = null, gitHash = null, beta = null, clientStructsHash = null;
        try
        {
            var info = pluginInterface.GetDalamudVersion();
            dalamudVersion = info.Version?.ToString();
            scm = info.ScmVersion;
            gitHash = info.GitHash;
            beta = info.BetaTrack;
            clientStructsHash = info.GitHashClientStructs;
        }
        catch
        {
            // Older/newer Dalamud builds may not expose every field.
        }

        var dalamudAssemblyVersion = typeof(IDalamudPluginInterface).Assembly.GetName().Version;
        dalamudVersion ??= dalamudAssemblyVersion?.ToString();

        string? gameVersion = null;
        Dictionary<string, string>? expansions = null;
        try
        {
            foreach (var (key, repo) in dataManager.GameData.Repositories)
            {
                if (key == "ffxiv")
                {
                    gameVersion = repo.Version;
                }
                else
                {
                    expansions ??= new Dictionary<string, string>();
                    expansions[key] = repo.Version;
                }
            }
        }
        catch
        {
            // Repository metadata is informational only.
        }

        float? uiScale = null;
        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            if (manager != null)
                uiScale = MathF.Round(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase.GetGlobalUIScale(), 3);
        }
        catch
        {
            uiScale = null;
        }

        string? theme = null;
        if (gameConfig.TryGet(SystemConfigOption.ColorThemeType, out uint themeValue))
        {
            theme = themeValue switch
            {
                0 => "dark",
                1 => "light",
                2 => "classicFF",
                3 => "clearBlue",
                _ => themeValue.ToString(CultureInfo.InvariantCulture),
            };
        }

        var inCutscene = condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] || condition[ConditionFlag.OccupiedInCutSceneEvent];
        var plugins = pluginInterface.InstalledPlugins.ToList();

        return new DalamudInfoResult(
            dalamudVersion,
            dalamudAssemblyVersion?.Major,
            string.IsNullOrEmpty(scm) ? null : scm,
            string.IsNullOrEmpty(gitHash) ? null : gitHash,
            string.IsNullOrEmpty(beta) ? null : beta,
            gameVersion,
            expansions,
            clientState.ClientLanguage.ToString(),
            pluginInterface.UiLanguage,
            clientState.IsLoggedIn,
            clientState.IsPvP,
            clientState.IsGPosing,
            inCutscene,
            gameGui.GameUiHidden,
            uiScale,
            theme,
            dataManager.HasModifiedGameDataFiles,
            plugins.Count,
            plugins.Count(p => p.IsLoaded),
            string.IsNullOrEmpty(beta) ? "release" : beta,
            string.IsNullOrEmpty(clientStructsHash) ? null : clientStructsHash,
            DalamudInternals.IsSafeMode());
    }

    /// <summary>A manifest URL with credentials and tokens removed, capped.</summary>
    internal static string? CleanUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var clean = LogScrubber.Scrub(url.Trim());
        return clean.Length > 300 ? clean[..300] : clean;
    }

    public sealed record PluginInfo(
        string InternalName,
        string Name,
        string? Version,
        string? Author,
        bool Loaded,
        bool IsDev,
        bool IsThirdParty,
        bool IsTesting,
        bool IsOutdated,
        bool? IsBanned,
        bool? IsOrphaned,
        bool? IsDecommissioned,
        bool HasMainUi,
        bool HasConfigUi,
        int? ApiLevel,
        string? InstalledFromUrl = null,
        string? ProjectUrl = null,
        string? State = null,
        bool? UpdateAvailable = null);

    public sealed record PluginListResult(IReadOnlyList<PluginInfo> Plugins, int Total, bool Truncated);

    public sealed record DalamudInfoResult(
        string? DalamudVersion,
        int? DalamudApiLevel,
        string? DalamudScmVersion,
        string? DalamudGitHash,
        string? DalamudBetaTrack,
        string? GameVersion,
        IReadOnlyDictionary<string, string>? ExpansionVersions,
        string ClientLanguage,
        string DalamudUiLanguage,
        bool LoggedIn,
        bool IsPvP,
        bool InGpose,
        bool InCutscene,
        bool GameUiHidden,
        float? GlobalUiScale,
        string? ColorTheme,
        bool HasModifiedGameDataFiles,
        int InstalledPlugins,
        int LoadedPlugins,
        string? DalamudTrack = null,
        string? ClientStructsGitHash = null,
        bool? PluginSafeMode = null);
}

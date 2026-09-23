using System.Globalization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>Scrubbed reads of dalamud.log and the troubleshooting summary built on them.</summary>
[McpProvider("dalamud")]
public sealed class DalamudLogProvider
{
    private const int MaxMessageChars = 2000;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;

    public DalamudLogProvider(IDalamudPluginInterface pluginInterface, IClientState clientState, IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.clientState = clientState;
        this.dataManager = dataManager;
    }

    [McpTool("read_plugin_log",
        Sources = ["file:dalamud.log"],
        Title = "Read the Dalamud log",
        Description =
            "Returns the newest matching entries of dalamud.log (Dalamud's and every plugin's log), oldest first so the newest is last. Each entry: time (ISO 8601 with offset), level (verbose|debug|information|warning|error|fatal), plugin (the [Tag] the line starts with: a plugin's internal name, or a Dalamud module such as PLUGINM; omitted for untagged Dalamud lines), message, and details (exception/stack-trace lines that followed, capped). " +
            "Only the last tailMiB of the file is scanned (never the whole file), so matched counts that window; windowStartsMidFile=true means older entries exist beyond it. truncated=true when more entries matched than limit (the oldest matches are dropped). " +
            "Every returned string is scrubbed: bearer/Authorization values, token=/key=/password= values, long hex/base64 secrets, e-mail addresses, IPv4/IPv6 addresses (a bare four-part number counts as one unless written as a version) become <redacted:kind>, and home directories become ~ with the OS user name removed. Character names are NOT scrubbed: this is the player's own log and the client already shows them. " +
            "Use it to see why a plugin failed to load or what it logged around an error; use get_troubleshooting_summary first for the overview. Fails with unavailable when the log file cannot be found or read.",
        Permission = ToolPermission.Read, GameThread = false, RequiresLogin = false)]
    public LogReadResult ReadPluginLog(
        [McpParam("Only lines tagged [plugin]: a plugin's internal or display name (see list_plugins), or a Dalamud module tag. Case-insensitive, exact.")] string? plugin = null,
        [McpParam("Minimum level.", Enum = ["verbose", "debug", "information", "warning", "error", "fatal"])] string level = "information",
        [McpParam("Only entries whose message or details contain this text (case-insensitive).")] string? contains = null,
        [McpParam("Only entries from the last N minutes (1-10080).", Minimum = 1, Maximum = 10080)] int? sinceMinutes = null,
        [McpParam("Maximum entries to return (1-500).", Minimum = 1, Maximum = 500)] int limit = 100,
        [McpParam("How much of the end of the file to scan, in MiB (1-4).", Minimum = 1, Maximum = 4)] int tailMiB = 2)
    {
        if (!DalamudLogParser.TryParseLevel(level, out var minimum))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "level must be one of verbose, debug, information, warning, error, fatal.");
        if (contains is { Length: > 200 })
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "contains is limited to 200 characters.");
        if (sinceMinutes is < 1 or > 10080)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "sinceMinutes must be between 1 and 10080.");

        var tag = ResolveTag(plugin);
        var tail = ReadTail(Math.Clamp(tailMiB, 1, 4) * 1024 * 1024);
        var entries = DalamudLogParser.ParseEntries(tail.Lines);
        var since = sinceMinutes is { } minutes ? DateTimeOffset.Now.AddMinutes(-minutes) : (DateTimeOffset?)null;
        var selection = DalamudLogFilter.Select(entries, new DalamudLogQuery(tag, minimum, string.IsNullOrEmpty(contains) ? null : contains, since, Math.Clamp(limit, 1, 500)));

        var user = Environment.UserName;
        var result = selection.Entries.Select(e => new LogEntryDto(
            e.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            e.Level.ToString().ToLowerInvariant(),
            e.Plugin is null ? null : Cap(LogScrubber.Scrub(e.Plugin, user), 80),
            Cap(LogScrubber.Scrub(e.Message, user), MaxMessageChars),
            e.Continuation is null ? null : Cap(LogScrubber.Scrub(e.Continuation, user), MaxMessageChars))).ToList();

        return new LogReadResult(result, result.Count, selection.Matched, selection.Truncated, tail.FileBytes, tail.ScannedBytes, tail.StartedMidFile);
    }

    [McpTool("get_troubleshooting_summary",
        Sources = ["dalamud:IDalamudPluginInterface", "dalamud:InstalledPlugins", "dalamud:internal.PluginManager", "dalamud:internal.DalamudConfiguration", "file:dalamud.log", "lumina:GameData.Repositories"],
        Title = "Troubleshooting summary",
        Description =
            "A compact overview like the header of Dalamud's troubleshooting pack: dalamudVersion, dalamudTrack, dalamudApiLevel, gameVersion, clientLanguage, dalamudUiLanguage, hostPlatform and isWine, pluginSafeMode, installed/loaded/thirdParty/dev/testing plugin counts, and lists of internal names that need attention: notLoaded, failedToLoad (state LoadError or DependencyResolutionFailed), outdated, orphaned, banned, decommissioned, updateAvailable. " +
            "logErrorsLastHour counts error and fatal log entries of the last 60 minutes per plugin tag (untagged lines count as Dalamud), from the last 4 MiB of dalamud.log; logAvailable=false when the log could not be read. customRepositoryCount/enabledCustomRepositoryCount give the number of custom plugin repositories, never their URLs (list_plugin_repositories has those). " +
            "failedToLoad, updateAvailable, pluginSafeMode and the repository counts come from Dalamud internals and are omitted when this Dalamud build does not expose them. Nothing here contains paths, addresses or tokens. Start here when the player says a plugin is broken, then read_plugin_log for that plugin.",
        Permission = ToolPermission.Read, GameThread = false, RequiresLogin = false)]
    public TroubleshootingSummary GetTroubleshootingSummary()
    {
        string? version = null, track = null;
        try
        {
            var info = pluginInterface.GetDalamudVersion();
            version = info.ScmVersion ?? info.Version?.ToString();
            track = string.IsNullOrEmpty(info.BetaTrack) ? "release" : info.BetaTrack;
        }
        catch
        {
            // Reported as unknown.
        }

        string? gameVersion = null;
        try
        {
            gameVersion = dataManager.GameData.Repositories.TryGetValue("ffxiv", out var repo) ? repo.Version : null;
        }
        catch
        {
            // Reported as unknown.
        }

        var plugins = pluginInterface.InstalledPlugins.ToList();
        var internals = DalamudInternals.GetPlugins();
        var updatable = DalamudInternals.GetUpdatableInternalNames();
        var repositories = DalamudInternals.GetRepositories();

        IReadOnlyList<PluginErrorCount>? errors = null;
        var logAvailable = false;
        try
        {
            var tail = ReadTail(DalamudLogTailReader.MaxTailBytes);
            var user = Environment.UserName;
            errors = DalamudLogFilter.ErrorCounts(DalamudLogParser.ParseEntries(tail.Lines), DateTimeOffset.Now.AddHours(-1), 25)
                .Select(p => new PluginErrorCount(Cap(LogScrubber.Scrub(p.Key, user), 80), p.Value)).ToList();
            logAvailable = true;
        }
        catch (McpToolException)
        {
            // logAvailable stays false.
        }

        return new TroubleshootingSummary(
            version,
            track,
            typeof(IDalamudPluginInterface).Assembly.GetName().Version?.Major,
            gameVersion,
            clientState.ClientLanguage.ToString(),
            pluginInterface.UiLanguage,
            Dalamud.Utility.Util.GetHostPlatform().ToString(),
            Dalamud.Utility.Util.IsWine(),
            DalamudInternals.IsSafeMode(),
            plugins.Count,
            plugins.Count(static p => p.IsLoaded),
            plugins.Count(static p => p.IsThirdParty),
            plugins.Count(static p => p.IsDev),
            plugins.Count(static p => p.IsTesting),
            Names(PluginInstances.NotLoaded(plugins, static p => p.InternalName, static p => p.IsLoaded)),
            internals is null ? null : Distinct(internals.Where(static p => p.State is "LoadError" or "DependencyResolutionFailed").Select(static p => p.InternalName)),
            Names(plugins.Where(static p => p.IsOutdated)),
            Names(plugins.Where(static p => p.IsOrphaned)),
            Names(plugins.Where(static p => p.IsBanned)),
            Names(plugins.Where(static p => p.IsDecommissioned)),
            updatable is null ? null : Distinct(updatable),
            logAvailable,
            errors,
            repositories?.Count,
            repositories?.Count(static r => r.IsEnabled));
    }

    private static List<string> Names(IEnumerable<IExposedPlugin> plugins) => Distinct(plugins.Select(static p => p.InternalName));

    private static List<string> Distinct(IEnumerable<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).Take(100).ToList();

    private static string Cap(string text, int max) => text.Length > max ? text[..max] + "..." : text;

    /// <summary>A display name is translated to the internal name plugins log under; anything else is used as the tag itself.</summary>
    private string? ResolveTag(string? plugin)
    {
        var text = plugin?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > 80)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "plugin is limited to 80 characters.");
        text = text.Trim('[', ']');
        foreach (var installed in pluginInterface.InstalledPlugins)
        {
            if (string.Equals(installed.InternalName, text, StringComparison.OrdinalIgnoreCase))
                return installed.InternalName;
        }

        foreach (var installed in pluginInterface.InstalledPlugins)
        {
            if (string.Equals(installed.Name, text, StringComparison.OrdinalIgnoreCase))
                return installed.InternalName;
        }

        return text;
    }

    private DalamudLogTail ReadTail(int maxBytes)
    {
        var candidates = DalamudLogLocator.Candidates(
            pluginInterface.ConfigDirectory?.FullName,
            pluginInterface.ConfigFile?.FullName,
            pluginInterface.DalamudAssetDirectory?.FullName);
        var path = DalamudLogLocator.FindExisting(candidates)
            ?? throw McpToolException.WithCode(McpErrorCodes.Unavailable, "dalamud.log was not found in the launcher folder that holds Dalamud's pluginConfigs, nor in its logs folder. This launcher keeps its log somewhere else; the player can open it from the launcher or with /xllog in game.");
        try
        {
            return DalamudLogTailReader.ReadTail(path, maxBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, "dalamud.log could not be read right now (it is locked or being rotated). Try again in a moment.", retryable: true);
        }
    }

    public sealed record LogEntryDto(string Time, string Level, string? Plugin, string Message, string? Details);

    public sealed record LogReadResult(IReadOnlyList<LogEntryDto> Entries, int Returned, int Matched, bool Truncated, long FileBytes, long ScannedBytes, bool WindowStartsMidFile);

    public sealed record PluginErrorCount(string Plugin, int Errors);

    public sealed record TroubleshootingSummary(
        string? DalamudVersion,
        string? DalamudTrack,
        int? DalamudApiLevel,
        string? GameVersion,
        string ClientLanguage,
        string DalamudUiLanguage,
        string HostPlatform,
        bool IsWine,
        bool? PluginSafeMode,
        int InstalledPlugins,
        int LoadedPlugins,
        int ThirdPartyPlugins,
        int DevPlugins,
        int TestingPlugins,
        IReadOnlyList<string> NotLoaded,
        IReadOnlyList<string>? FailedToLoad,
        IReadOnlyList<string> Outdated,
        IReadOnlyList<string> Orphaned,
        IReadOnlyList<string> Banned,
        IReadOnlyList<string> Decommissioned,
        IReadOnlyList<string>? UpdateAvailable,
        bool LogAvailable,
        IReadOnlyList<PluginErrorCount>? LogErrorsLastHour,
        int? CustomRepositoryCount,
        int? EnabledCustomRepositoryCount);
}

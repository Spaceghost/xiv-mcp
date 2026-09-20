using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>Read-only views of Dalamud's own state: custom repositories, plugin statistics, UI settings.</summary>
[McpProvider("dalamud")]
public sealed class DalamudInspectProvider
{
    private readonly IDalamudPluginInterface pluginInterface;

    public DalamudInspectProvider(IDalamudPluginInterface pluginInterface) => this.pluginInterface = pluginInterface;

    [McpTool("list_plugin_repositories",
        Sources = ["dalamud:internal.DalamudConfiguration"],
        Title = "List custom plugin repositories",
        Description =
            "Lists the custom (third-party) plugin repositories configured in Dalamud's settings (Experimental tab): url and enabled for each, in the configured order; Dalamud's own main repository is not part of this list. URLs are scrubbed of embedded credentials and token/key query values (<redacted:kind>). " +
            "The list is read from Dalamud's in-memory configuration (DalamudConfiguration.ThirdRepoList), which is not public plugin API: on a Dalamud build where it moved the tool fails with unavailable. Nothing is read from or written to Dalamud's config files. " +
            "Use it to tell where a third-party plugin could have come from (compare list_plugins installedFromUrl); add_plugin_repository is the assisted way to add one.",
        Permission = ToolPermission.Read, GameThread = false, RequiresLogin = false)]
    public RepositoryListResult ListPluginRepositories(
        [McpParam("Maximum entries to return (1-500).", Minimum = 1, Maximum = 500)] int limit = 100,
        [McpParam("Entries to skip.", Minimum = 0)] int offset = 0)
    {
        var all = DalamudInternals.GetRepositories()
            ?? throw Unavailable(DalamudInternals.RepositoryKeys, "its custom repository list");
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var page = all.Skip(offset).Take(limit)
            .Select(static r => new RepositoryDto(DalamudProvider.CleanUrl(r.Url) ?? "", r.IsEnabled)).ToList();
        return new RepositoryListResult(page, all.Count, all.Count(static r => r.IsEnabled), offset + page.Count < all.Count);
    }

    [McpTool("get_plugin_stats",
        Sources = ["dalamud:internal.PluginDrawStatistics", "dalamud:internal.Framework.StatsHistory"],
        Title = "Plugin performance statistics",
        Description =
            "Best-effort copy of Dalamud's Plugin Statistics window (/xlstats). draw: per loaded plugin the UI draw time in milliseconds (lastMs, averageMs over Dalamud's rolling window, maxMs), sorted by averageMs descending. framework: per Framework.Update handler the run time in milliseconds (lastMs, averageMs, maxMs, samples; up to 1000 samples each), keyed handler = \"Type::Method\" as Dalamud records it, sorted by averageMs descending. " +
            "Dalamud only measures while the player has switched on draw-time tracking and framework-update tracking in that window: drawTrackingEnabled / frameworkTrackingEnabled say so, and with tracking off the numbers are stale or empty (this tool never switches tracking on). " +
            "All of this is Dalamud-internal, not public API: a part that cannot be reached on this build is omitted with drawAvailable/frameworkAvailable=false, and when neither can be reached the tool fails with unavailable. Hook statistics are not included.",
        Permission = ToolPermission.Read, RequiresLogin = false)]
    public PluginStatsResult GetPluginStats(
        [McpParam("Maximum rows per list (1-200).", Minimum = 1, Maximum = 200)] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        var draw = DalamudInternals.GetDrawStats();
        var framework = DalamudInternals.GetFrameworkStats();
        if (draw is null && framework is null)
            throw Unavailable([.. DalamudInternals.DrawStatsKeys, .. DalamudInternals.FrameworkStatsKeys], "its plugin statistics");

        List<DrawStatDto>? drawRows = null;
        var drawTotal = 0;
        if (draw is { } d)
        {
            // Dalamud keeps draw times in 100 ns ticks and shows them divided by 10000 as milliseconds; -1 means never measured.
            var measured = d.Plugins.Where(static p => p.LastTicks >= 0).ToList();
            drawTotal = measured.Count;
            drawRows = measured
                .OrderByDescending(static p => p.AverageTicks)
                .Take(limit)
                .Select(static p => new DrawStatDto(p.InternalName, p.Name, Ms(p.LastTicks), Ms(p.AverageTicks), Ms(p.MaxTicks)))
                .ToList();
        }

        List<FrameworkStatDto>? frameworkRows = null;
        var frameworkTotal = 0;
        if (framework is { } f)
        {
            frameworkTotal = f.History.Count;
            frameworkRows = f.History
                .Where(static p => p.Value.Length > 0)
                .Select(static p => new FrameworkStatDto(
                    Cap(LogScrubber.Scrub(p.Key), 200),
                    Round(p.Value[^1]),
                    Round(p.Value.Average()),
                    Round(p.Value.Max()),
                    p.Value.Length))
                .OrderByDescending(static r => r.AverageMs)
                .Take(limit)
                .ToList();
        }

        return new PluginStatsResult(
            draw is not null,
            draw?.Enabled,
            drawRows,
            framework is not null,
            framework?.Enabled,
            frameworkRows,
            drawTotal > (drawRows?.Count ?? 0) || frameworkTotal > (frameworkRows?.Count ?? 0));
    }

    [McpTool("get_ui_info",
        Sources = ["dalamud:ImGuiHelpers.GlobalScale", "dalamud:IUiBuilder.DefaultFontSpec", "dalamud:StyleModel"],
        Title = "Dalamud UI settings",
        Description =
            "Returns how Dalamud draws plugin windows: globalScale (Dalamud's global UI scale, 1.0 = 100 %; not the game's HUD scale, which get_dalamud_info reports as globalUiScale), defaultFont (English description of the default font, e.g. \"Game: Axis (12pt)\"), defaultFontSizePx/defaultFontSizePt/defaultFontLineHeightPx, " +
            "usesGameFont (true when the default font is one of the game's own fonts rather than a Dalamud or system font), styleName (the chosen Dalamud style/theme, e.g. \"Dalamud Standard\"; omitted when Dalamud reports none) and the UI-hide switches this plugin runs under. Public Dalamud API only. " +
            "Use it when advising on window sizes or unreadable text in plugin windows.",
        Permission = ToolPermission.Read, RequiresLogin = false)]
    public UiInfoResult GetUiInfo()
    {
        string? font = null, style = null;
        float? sizePx = null, sizePt = null, lineHeight = null;
        bool? usesGameFont = null;
        try
        {
            var spec = pluginInterface.UiBuilder.DefaultFontSpec;
            font = spec.ToLocalizedString("en");
            sizePx = Round2(spec.SizePx);
            sizePt = Round2(spec.SizePt);
            lineHeight = Round2(spec.LineHeightPx);
            usesGameFont = spec is SingleFontSpec { FontId: GameFontAndFamilyId };
        }
        catch
        {
            // The font atlas may not be built yet; the fields stay empty.
        }

        try
        {
            style = StyleModel.GetConfiguredStyle()?.Name;
        }
        catch
        {
            style = null;
        }

        float? scale = null;
        try
        {
            scale = Round2(ImGuiHelpers.GlobalScale);
        }
        catch
        {
            scale = null;
        }

        return new UiInfoResult(
            scale,
            string.IsNullOrEmpty(font) ? null : Cap(font, 200),
            sizePx,
            sizePt,
            lineHeight,
            usesGameFont,
            string.IsNullOrEmpty(style) ? null : Cap(style, 100),
            pluginInterface.UiBuilder.DisableAutomaticUiHide,
            pluginInterface.UiBuilder.DisableUserUiHide,
            pluginInterface.UiBuilder.DisableCutsceneUiHide,
            pluginInterface.UiBuilder.DisableGposeUiHide);
    }

    internal static McpToolException Unavailable(IEnumerable<string> keys, string what) =>
        McpToolException.WithCode(McpErrorCodes.Unavailable,
            DalamudInternals.FirstMissing(keys) is { } missing
                ? $"This Dalamud build does not expose {what} the way this plugin expects ({missing} is missing). It is internal to Dalamud, not public API; the plugin needs an update for this Dalamud version."
                : $"This Dalamud build does not expose {what} right now (the service is not loaded yet). Try again once the game has finished loading.",
            retryable: DalamudInternals.FirstMissing(keys) is null);

    private static double Ms(long ticks) => ticks < 0 ? 0 : Math.Round(ticks / 10000.0, 4);

    private static double Round(double ms) => Math.Round(ms, 4);

    private static float Round2(float value) => MathF.Round(value, 2);

    private static string Cap(string text, int max) => text.Length > max ? text[..max] : text;

    public sealed record RepositoryDto(string Url, bool Enabled);

    public sealed record RepositoryListResult(IReadOnlyList<RepositoryDto> Repositories, int Total, int EnabledTotal, bool Truncated);

    public sealed record DrawStatDto(string InternalName, string Name, double LastMs, double AverageMs, double MaxMs);

    public sealed record FrameworkStatDto(string Handler, double LastMs, double AverageMs, double MaxMs, int Samples);

    public sealed record PluginStatsResult(
        bool DrawAvailable,
        bool? DrawTrackingEnabled,
        IReadOnlyList<DrawStatDto>? Draw,
        bool FrameworkAvailable,
        bool? FrameworkTrackingEnabled,
        IReadOnlyList<FrameworkStatDto>? Framework,
        bool Truncated);

    public sealed record UiInfoResult(
        float? GlobalScale,
        string? DefaultFont,
        float? DefaultFontSizePx,
        float? DefaultFontSizePt,
        float? DefaultFontLineHeightPx,
        bool? UsesGameFont,
        string? StyleName,
        bool DisableAutomaticUiHide,
        bool DisableUserUiHide,
        bool DisableCutsceneUiHide,
        bool DisableGposeUiHide);
}

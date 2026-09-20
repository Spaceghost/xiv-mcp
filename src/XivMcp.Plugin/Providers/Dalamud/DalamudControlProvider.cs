using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>
/// The Dalamud tools that change something. All of them go through the server's approval gate. None of them installs,
/// updates or deletes a plugin, and none of them writes a repository list: see docs/HARD-LINES.md.
/// </summary>
[McpProvider("dalamud")]
public sealed class DalamudControlProvider
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(90);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly INotificationManager notifications;

    public DalamudControlProvider(IDalamudPluginInterface pluginInterface, INotificationManager notifications)
    {
        this.pluginInterface = pluginInterface;
        this.notifications = notifications;
    }

    [McpTool("open_plugin_ui",
        Sources = ["dalamud:IExposedPlugin"],
        Title = "Open a plugin's window",
        Description =
            "Opens the main window or the settings window of an installed, loaded Dalamud plugin, exactly like the buttons in the plugin installer (IExposedPlugin.OpenMainUi / OpenConfigUi). plugin is the internal name from list_plugins (a display name works when it is unambiguous); which is 'main' or 'config'. " +
            "Returns the plugin's internalName and which window was asked for; whether a window actually appeared is up to that plugin. Fails with not_found for an unknown plugin, unavailable when the plugin is not loaded or has no such window (list_plugins hasMainUi/hasConfigUi). It only opens a window; it clicks nothing inside it.",
        Permission = ToolPermission.Ui, RequiresApproval = true, RequiresLogin = false,
        ApprovalSummary = "Open {plugin}'s {which} window.")]
    public OpenPluginUiResult OpenPluginUi(
        [McpParam("Plugin internal name (see list_plugins), or its display name.")] string plugin,
        [McpParam("Which window to open.", Enum = ["main", "config"])] string which = "main")
    {
        var config = which?.Trim().ToUpperInvariant() switch
        {
            "MAIN" => false,
            "CONFIG" or "SETTINGS" => true,
            _ => throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "which must be 'main' or 'config'."),
        };

        var target = ResolvePublic(plugin);
        if (!target.IsLoaded)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{target.InternalName} is installed but not loaded, so it has no windows. set_plugin_enabled can load it.");
        if (config ? !target.HasConfigUi : !target.HasMainUi)
        {
            var other = config ? target.HasMainUi : target.HasConfigUi;
            throw McpToolException.WithCode(McpErrorCodes.Unavailable,
                $"{target.InternalName} has no {(config ? "config" : "main")} window{(other ? $"; it does have a {(config ? "main" : "config")} window" : "")}.");
        }

        if (config)
            target.OpenConfigUi();
        else
            target.OpenMainUi();
        return new OpenPluginUiResult(target.InternalName, target.Name, config ? "config" : "main");
    }

    [McpTool("open_dalamud_window",
        Sources = ["dalamud:IDalamudPluginInterface.OpenPluginInstallerTo", "dalamud:IDalamudPluginInterface.OpenDalamudSettingsTo"],
        Title = "Open the plugin installer or Dalamud settings",
        Description =
            "Opens one of Dalamud's own windows in front of the player: window='installer' (the plugin installer; tab is allPlugins, installedPlugins, updateablePlugins, changelogs or dalamudChangelogs) or window='settings' (Dalamud settings; tab is general, lookAndFeel, autoUpdates, serverInfoBar, badge, experimental or about), optionally with searchText typed into that window's search box. " +
            "This is the whole answer to \"install plugin X\" or \"update my plugins\": this server never installs, updates or removes a plugin, it puts the installer on that search and the player clicks. Returns opened (what Dalamud reported), window, tab and searchText. Fails with invalid_arguments for an unknown window or tab.",
        Permission = ToolPermission.Ui, RequiresApproval = true, RequiresLogin = false,
        ApprovalSummary = "Open Dalamud's {window} window on tab {tab}, searching for: {searchText}")]
    public OpenDalamudWindowResult OpenDalamudWindow(
        [McpParam("Which Dalamud window.", Enum = ["installer", "settings"])] string window,
        [McpParam("Tab to show; default is the window's first tab (allPlugins / general). Installer: allPlugins, installedPlugins, updateablePlugins, changelogs, dalamudChangelogs. Settings: general, lookAndFeel, autoUpdates, serverInfoBar, badge, experimental, about.")] string? tab = null,
        [McpParam("Text for the window's search box (single line, up to 100 characters).")] string? searchText = null)
    {
        var target = DalamudWindowTarget.Parse(window, tab, searchText);
        var opened = target.IsInstaller
            ? pluginInterface.OpenPluginInstallerTo(target.InstallerTab, target.SearchText)
            : pluginInterface.OpenDalamudSettingsTo(target.SettingsTab, target.SearchText);
        return new OpenDalamudWindowResult(
            opened,
            target.IsInstaller ? "installer" : "settings",
            target.IsInstaller ? CamelCase(target.InstallerTab.ToString()) : CamelCase(target.SettingsTab.ToString()),
            target.SearchText);
    }

    [McpTool("set_plugin_enabled",
        Sources = ["dalamud:internal.PluginManager", "dalamud:internal.ProfileManager"],
        Title = "Enable or disable a plugin",
        Description =
            "Enables (loads) or disables (unloads) an INSTALLED Dalamud plugin and records the choice the way the plugin installer's toggle does, so it persists across restarts: disabling unloads the plugin and then marks it not wanted in Dalamud's default plugin collection; enabling marks it wanted and then loads it. plugin is the internal name from list_plugins. " +
            "Returns internalName, enabled (what was asked), changed (false when it already was in that state), state and loaded afterwards. There is no public Dalamud API for this: it calls the same internal methods the installer calls, and fails with unavailable when this Dalamud build does not expose them, when Dalamud itself would grey the toggle out (safe mode, a banned/outdated/orphaned plugin, a load in progress, a plugin managed by a non-default collection), or when the plugin throws while loading (the message says why; read_plugin_log has the rest). " +
            "Refused: disabling this MCP server's own plugin (the connection would drop mid-call; the player can do it in /xlplugins). It never installs or updates anything: an outdated plugin stays outdated, and a plugin that is not installed is not_found (open_dalamud_window puts the installer on a search instead). Disabling is the destructive direction: whatever the plugin was doing stops.",
        Permission = ToolPermission.Action, Destructive = true, Idempotent = true, GameThread = false, RequiresLogin = false,
        ApprovalSummary = "Set Dalamud plugin {plugin} to enabled = {enabled} (loads or unloads it now and remembers the choice).")]
    public async Task<PluginStateResult> SetPluginEnabled(
        [McpParam("Plugin internal name (see list_plugins), or its display name.")] string plugin,
        [McpParam("true loads the plugin and keeps it enabled; false unloads it and keeps it disabled.")] bool enabled,
        ToolContext? ctx = null)
    {
        var target = ResolvePublic(plugin);
        if (!enabled && IsSelf(target.InternalName))
            PluginToggleRules.EnsureCanSetEnabled(SelfFacts(target), enable: false, pluginInterface.InternalName);

        var internalPlugin = ResolveInternal(target, DalamudInternals.ToggleKeys);
        var context = DalamudInternals.GetToggleContext(internalPlugin)
            ?? throw DalamudInspectProvider.Unavailable(DalamudInternals.ToggleKeys, "its plugin manager");
        var facts = Facts(internalPlugin, context.SafeMode, context.ProfilesBusy, context.WantingProfiles, context.SingleProfileIsDefault, context.SingleProfileEnabled);

        if (PluginToggleRules.AlreadyInState(facts, enabled))
        {
            return new PluginStateResult(facts.InternalName, internalPlugin.Name, enabled, false, facts.State, facts.IsLoaded);
        }

        PluginToggleRules.EnsureCanSetEnabled(facts, enabled, pluginInterface.InternalName);
        await RunDalamud(() => DalamudInternals.SetEnabledAsync(internalPlugin, context.SingleProfile!, enabled), enabled ? "enable" : "disable", facts.InternalName, ctx).ConfigureAwait(false);
        return After(target, enabled, changed: true);
    }

    [McpTool("reload_plugin",
        Sources = ["dalamud:internal.PluginManager"],
        Title = "Reload a plugin",
        Description =
            "Unloads and loads again one installed, currently loaded Dalamud plugin (Dalamud's LocalPlugin.ReloadAsync, what a dev plugin's automatic reload uses): the same files are loaded again, nothing is downloaded or updated, and the plugin's enabled state is unchanged. Use it when a plugin is stuck; the plugin loses whatever it kept only in memory. " +
            "Returns internalName, state and loaded afterwards. Internal to Dalamud, not public API: fails with unavailable when this build does not expose it, in safe mode, while Dalamud is busy, when the plugin is not loaded (set_plugin_enabled loads it), or when the plugin throws while loading. Refused for this MCP server's own plugin (the connection would drop mid-call; the player can toggle it in /xlplugins).",
        Permission = ToolPermission.Action, Destructive = false, Idempotent = false, GameThread = false, RequiresLogin = false,
        ApprovalSummary = "Reload Dalamud plugin {plugin} (unload it and load the same files again).")]
    public async Task<PluginStateResult> ReloadPlugin(
        [McpParam("Plugin internal name (see list_plugins), or its display name.")] string plugin,
        ToolContext? ctx = null)
    {
        var target = ResolvePublic(plugin);
        if (IsSelf(target.InternalName))
            PluginToggleRules.EnsureCanReload(SelfFacts(target), pluginInterface.InternalName);

        var internalPlugin = ResolveInternal(target, DalamudInternals.ReloadKeys);
        var context = DalamudInternals.GetReloadContext()
            ?? throw DalamudInspectProvider.Unavailable(DalamudInternals.ReloadKeys, "its plugin manager");
        var facts = Facts(internalPlugin, context.SafeMode, context.ProfilesBusy, 1, true, true);
        PluginToggleRules.EnsureCanReload(facts, pluginInterface.InternalName);

        await RunDalamud(() => DalamudInternals.ReloadAsync(internalPlugin), "reload", facts.InternalName, ctx).ConfigureAwait(false);
        return After(target, enabled: true, changed: true);
    }

    [McpTool("add_plugin_repository",
        Sources = ["dalamud:IDalamudPluginInterface.OpenDalamudSettingsTo", "dalamud:internal.DalamudConfiguration"],
        Title = "Help the player add a custom plugin repository",
        Description =
            "ASSISTED, not automatic: a custom repository can ship code that runs inside the game, so adding one is the player's supply-chain decision and this tool never writes Dalamud's configuration. After approval it (1) opens Dalamud settings on the Experimental tab, where custom repositories live, (2) copies the URL to the clipboard and (3) shows a notification telling the player to paste it into the list, press +, and save. " +
            "The result always says added=false: nothing has been added when the call returns, and only the player can finish it; check list_plugin_repositories afterwards. alreadyConfigured=true (and nothing is opened or copied) when the URL is already in the list. " +
            "The url must be https, at most 300 printable ASCII characters, parse as an absolute URL with a full host name, and carry no credentials (user:password@); otherwise invalid_arguments. After the player has added it, open_dalamud_window (installer, with searchText) puts the plugin in front of them.",
        Permission = ToolPermission.Action, Destructive = false, Idempotent = true, OpenWorld = false, RequiresLogin = false,
        ApprovalSummary = "Open Dalamud settings (Experimental tab) and copy this custom plugin repository URL to your clipboard so YOU can paste and save it; nothing is added automatically: {url}")]
    public AddRepositoryResult AddPluginRepository(
        [McpParam("The repository's pluginmaster URL (https only).")] string url)
    {
        var clean = RepositoryUrlValidator.Validate(url);
        var existing = DalamudInternals.GetRepositories()?.FirstOrDefault(r => string.Equals(r.Url.Trim(), clean, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return new AddRepositoryResult(false, true, existing.IsEnabled, false, false, clean,
                existing.IsEnabled ? "That repository is already in Dalamud's custom repository list and enabled. Nothing was opened." : "That repository is already in Dalamud's custom repository list but disabled; the player can tick it in Dalamud settings, Experimental tab. Nothing was opened.");
        }

        var opened = pluginInterface.OpenDalamudSettingsTo(SettingsOpenKind.Experimental, null);
        var copied = false;
        try
        {
            ImGui.SetClipboardText(clean);
            copied = true;
        }
        catch
        {
            // The URL is still in the notification and the result.
        }

        try
        {
            notifications.AddNotification(new Notification
            {
                Title = "Custom plugin repository",
                Content = $"{(copied ? "Copied to your clipboard" : "Requested")}: {clean}\nIf you trust it: Dalamud settings > Experimental > Custom Plugin Repositories, paste it into the empty row, press +, then save. Nothing has been added for you.",
                Type = NotificationType.Info,
                Minimized = false,
                InitialDuration = TimeSpan.FromSeconds(30),
            });
        }
        catch
        {
            // Informational only.
        }

        return new AddRepositoryResult(false, false, null, opened, copied, clean,
            "Nothing was added. Dalamud settings is open on the Experimental tab" + (copied ? " and the URL is on the clipboard" : "") + ": the player has to paste it under Custom Plugin Repositories, press +, and save. Check list_plugin_repositories afterwards.");
    }

    private static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static PluginToggleFacts Facts(InternalPlugin p, bool safeMode, bool busy, int wanting, bool singleIsDefault, bool singleEnabled) =>
        new(p.InternalName, p.State, p.IsLoaded, p.IsDev, p.IsOutdated, p.IsBanned, p.IsOrphaned, safeMode, busy, wanting, singleIsDefault, singleEnabled);

    /// <summary>Enough for the self-refusal, which must not depend on Dalamud internals being reachable.</summary>
    private static PluginToggleFacts SelfFacts(IExposedPlugin p) =>
        new(p.InternalName, p.IsLoaded ? "Loaded" : "Unloaded", p.IsLoaded, p.IsDev, p.IsOutdated, p.IsBanned, p.IsOrphaned, false, false, 1, true, true);

    private bool IsSelf(string internalName) => string.Equals(internalName, pluginInterface.InternalName, StringComparison.OrdinalIgnoreCase);

    private IExposedPlugin ResolvePublic(string plugin)
    {
        var installed = pluginInterface.InstalledPlugins.ToList();
        var index = PluginNameResolver.Resolve(installed.Select(static p => (p.InternalName, p.Name)).ToList(), plugin);
        return installed[index];
    }

    private static InternalPlugin ResolveInternal(IExposedPlugin target, string[] keys)
    {
        var plugins = DalamudInternals.GetPlugins() ?? throw DalamudInspectProvider.Unavailable(keys, "its plugin manager");
        if (DalamudInternals.FirstMissing(keys) is not null)
            throw DalamudInspectProvider.Unavailable(keys, "its plugin manager");
        var matches = plugins.Where(p => string.Equals(p.InternalName, target.InternalName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"Dalamud's plugin manager lists {matches.Count} plugins named {target.InternalName}; act on it in the plugin installer instead.");
    }

    private static async Task RunDalamud(Func<Task> action, string verb, string internalName, ToolContext? ctx)
    {
        try
        {
            // Off the framework thread, like the installer (Task.Run): plugin constructors may wait on it.
            await Task.Run(action).WaitAsync(LoadTimeout, ctx?.CancellationToken ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw McpToolException.WithCode(McpErrorCodes.Timeout, $"Dalamud did not finish the {verb} of {internalName} within {LoadTimeout.TotalSeconds:0} seconds; it may still complete. Check list_plugins.", retryable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            var reason = LogScrubber.Scrub(e.InnerException?.Message ?? e.Message, Environment.UserName);
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"Dalamud could not {verb} {internalName}: {(reason.Length > 400 ? reason[..400] : reason)} (read_plugin_log has the details).");
        }
    }

    private PluginStateResult After(IExposedPlugin target, bool enabled, bool changed)
    {
        var now = DalamudInternals.GetPlugins()?.FirstOrDefault(p => string.Equals(p.InternalName, target.InternalName, StringComparison.OrdinalIgnoreCase));
        var loaded = now?.IsLoaded ?? pluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == target.InternalName)?.IsLoaded ?? false;
        return new PluginStateResult(target.InternalName, target.Name, enabled, changed, now?.State ?? (loaded ? "Loaded" : "Unloaded"), loaded);
    }

    public sealed record OpenPluginUiResult(string InternalName, string Name, string Which);

    public sealed record OpenDalamudWindowResult(bool Opened, string Window, string Tab, string? SearchText);

    public sealed record PluginStateResult(string InternalName, string Name, bool Enabled, bool Changed, string State, bool Loaded);

    public sealed record AddRepositoryResult(bool Added, bool AlreadyConfigured, bool? ExistingEnabled, bool SettingsOpened, bool CopiedToClipboard, string Url, string NextStep);
}

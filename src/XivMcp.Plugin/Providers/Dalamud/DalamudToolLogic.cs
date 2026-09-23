using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>
/// Picks one plugin out of several installs that share an internal name. Dalamud lists every install: a dev copy of
/// a plugin sits next to the repository copy under the same internal name, and usually only one of them is loaded.
/// Anything that asks "is X there, is it loaded, which version" has to look at the loaded one, not whichever
/// Dalamud happened to list first. Pure.
/// </summary>
public static class PluginInstances
{
    /// <summary>
    /// Index of the instance to use: the first loaded one, else the first one; -1 when <paramref name="plugins"/> is empty.
    /// </summary>
    public static int PreferLoaded<T>(IReadOnlyList<T> plugins, Func<T, bool> isLoaded)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(isLoaded);
        for (var i = 0; i < plugins.Count; i++)
        {
            if (isLoaded(plugins[i]))
                return i;
        }

        return plugins.Count > 0 ? 0 : -1;
    }

    /// <summary>The loaded instance among <paramref name="plugins"/> whose internal name is <paramref name="internalName"/>, else the first such; null when there is none.</summary>
    public static T? Find<T>(IEnumerable<T> plugins, string internalName, Func<T, string> nameOf, Func<T, bool> isLoaded)
        where T : class
    {
        var matches = plugins.Where(p => string.Equals(nameOf(p), internalName, StringComparison.OrdinalIgnoreCase)).ToList();
        var index = PreferLoaded(matches, isLoaded);
        return index < 0 ? null : matches[index];
    }

    /// <summary>The installs that are not loaded and have no loaded copy under the same internal name.</summary>
    public static IEnumerable<T> NotLoaded<T>(IReadOnlyList<T> plugins, Func<T, string> nameOf, Func<T, bool> isLoaded)
    {
        var loaded = new HashSet<string>(plugins.Where(isLoaded).Select(nameOf), StringComparer.OrdinalIgnoreCase);
        return plugins.Where(p => !isLoaded(p) && !loaded.Contains(nameOf(p)));
    }

    /// <summary>
    /// Like <see cref="Find{T}(IEnumerable{T}, string, Func{T, string}, Func{T, bool})"/>, for pairing one install with
    /// its entry in another list: an install of the same kind (dev or not) is taken over the other kind.
    /// </summary>
    public static T? Find<T>(IEnumerable<T> plugins, string internalName, bool isDev, Func<T, string> nameOf, Func<T, bool> isDevOf, Func<T, bool> isLoaded)
        where T : class
    {
        var matches = plugins.Where(p => string.Equals(nameOf(p), internalName, StringComparison.OrdinalIgnoreCase)).ToList();
        var sameKind = matches.Where(p => isDevOf(p) == isDev).ToList();
        var pool = sameKind.Count > 0 ? sameKind : matches;
        var index = PreferLoaded(pool, isLoaded);
        return index < 0 ? null : pool[index];
    }
}

/// <summary>Resolves what a caller typed to exactly one installed plugin: internal name first, then display name. Pure.</summary>
public static class PluginNameResolver
{
    /// <summary>Resolves by name alone; two installs sharing a name are always ambiguous.</summary>
    public static int Resolve(IReadOnlyList<(string InternalName, string Name)> plugins, string? wanted) =>
        Resolve(plugins.Select(static p => (p.InternalName, p.Name, false)).ToList(), wanted);

    /// <returns>Index into <paramref name="plugins"/>.</returns>
    /// <remarks>
    /// When every match is an install of one plugin (same internal name: a dev copy next to the repository copy)
    /// and exactly one of them is loaded, that one is the answer. Different plugins that share a display name, or
    /// copies of which none or several are loaded, stay ambiguous.
    /// </remarks>
    /// <exception cref="McpToolException"><c>invalid_arguments</c> for an empty or ambiguous name, <c>not_found</c> otherwise.</exception>
    public static int Resolve(IReadOnlyList<(string InternalName, string Name, bool IsLoaded)> plugins, string? wanted)
    {
        var text = wanted?.Trim();
        if (string.IsNullOrEmpty(text))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "plugin is required: the plugin's internal name (see list_plugins).");
        if (text.Length > 120)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "plugin is too long to be a plugin name.");

        var byInternal = Matches(plugins, p => string.Equals(p.InternalName, text, StringComparison.OrdinalIgnoreCase));
        if (byInternal.Count == 0)
            byInternal = Matches(plugins, p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase));
        if (byInternal.Count == 1)
            return byInternal[0];
        if (byInternal.Count > 1)
        {
            var sameInternalName = byInternal.All(i => string.Equals(plugins[i].InternalName, plugins[byInternal[0]].InternalName, StringComparison.OrdinalIgnoreCase));
            var loaded = byInternal.Where(i => plugins[i].IsLoaded).ToList();
            if (sameInternalName && loaded.Count == 1)
                return loaded[0];

            // Two installs of one plugin (a dev copy next to the repository copy) share both names.
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments,
                $"'{text}' matches {byInternal.Count} installed plugins ({string.Join(", ", byInternal.Select(i => plugins[i].InternalName))}). Pass the internal name; when one plugin is installed twice (a dev copy next to the repository copy) act on it in the plugin installer instead.");
        }

        var near = plugins
            .Where(p => p.InternalName.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.InternalName).Take(5).ToList();
        var hint = near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : " Use list_plugins for the installed names.";
        throw McpToolException.WithCode(McpErrorCodes.NotFound,
            $"No installed plugin is named '{text}'.{hint} This server never installs plugins: open_dalamud_window can put the installer in front of the player on a search.");
    }

    private static List<int> Matches(IReadOnlyList<(string InternalName, string Name, bool IsLoaded)> plugins, Func<(string InternalName, string Name, bool IsLoaded), bool> predicate)
    {
        var result = new List<int>();
        for (var i = 0; i < plugins.Count; i++)
        {
            if (predicate(plugins[i]))
                result.Add(i);
        }

        return result;
    }
}

/// <summary>Rules for a custom plugin repository URL before it is shown to the player. Pure.</summary>
public static class RepositoryUrlValidator
{
    public const int MaxLength = 300;

    /// <returns>The URL exactly as it will be shown and copied (trimmed input, not re-serialized).</returns>
    /// <exception cref="McpToolException"><c>invalid_arguments</c> with the rule that failed.</exception>
    public static string Validate(string? url)
    {
        var text = url?.Trim();
        if (string.IsNullOrEmpty(text))
            throw Invalid("url is required.");
        if (text.Length > MaxLength)
            throw Invalid($"url is longer than {MaxLength} characters.");
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c > 0x7E)
                throw Invalid("url may contain only printable ASCII without spaces (use punycode and percent-encoding).");
        }

        if (text.Contains('\\', StringComparison.Ordinal) || (text.Length > 8 && text[8] == '/'))
            throw Invalid("url does not parse as an absolute URL.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw Invalid("url does not parse as an absolute URL.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw Invalid("url must be https.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || text.AsSpan(8).ToString().Split('/')[0].Contains('@', StringComparison.Ordinal))
            throw Invalid("url must not carry credentials (user:password@host).");
        if (string.IsNullOrEmpty(uri.Host) || (uri.HostNameType == UriHostNameType.Dns && !uri.Host.Contains('.', StringComparison.Ordinal)))
            throw Invalid("url needs a full host name.");
        return text;
    }

    private static McpToolException Invalid(string message) => McpToolException.WithCode(McpErrorCodes.InvalidArguments, message);
}

/// <summary>What the rules below need to know about a plugin and Dalamud's state; mirrors what the installer reads before enabling its toggle.</summary>
public sealed record PluginToggleFacts(
    string InternalName,
    string State,
    bool IsLoaded,
    bool IsDev,
    bool IsOutdated,
    bool IsBanned,
    bool IsOrphaned,
    bool SafeMode,
    bool ProfilesBusy,
    int WantingProfiles,
    bool SingleProfileIsDefault,
    bool SingleProfileEnabled);

/// <summary>
/// The conditions under which Dalamud's plugin installer greys out its enable/disable toggle, plus this server's own
/// refusals. Pure. The installer also allows a plugin that sits in exactly one non-default collection; that case is
/// left to the player here because collections can depend on the logged-in character.
/// </summary>
public static class PluginToggleRules
{
    /// <exception cref="McpToolException"><c>refused</c> for this server's own plugin, <c>unavailable</c> when Dalamud itself would not allow it now.</exception>
    public static void EnsureCanSetEnabled(PluginToggleFacts facts, bool enable, string ownInternalName)
    {
        if (!enable)
            RefuseSelf(facts, ownInternalName, "disable");
        EnsureCommon(facts);

        if (facts.IsBanned)
            throw Blocked($"{facts.InternalName} is banned by Dalamud and cannot be loaded.");
        if (facts.IsOutdated && !facts.IsDev)
            throw Blocked($"{facts.InternalName} is built for an older Dalamud API level and cannot be loaded until its author updates it. This server never updates plugins: open_dalamud_window (installer, updateablePlugins) shows the player what is available.");
        if (facts.IsOrphaned && !facts.IsLoaded)
            throw Blocked($"{facts.InternalName} is orphaned (its repository is gone) and Dalamud will not load it.");
        if (facts.WantingProfiles != 1 || !facts.SingleProfileIsDefault)
            throw Blocked($"{facts.InternalName} is managed by a plugin collection (it is listed in {facts.WantingProfiles} collection(s)); change it in the plugin installer's collections instead.");
        if (!facts.SingleProfileEnabled)
            throw Blocked("Dalamud's default plugin collection is disabled; nothing can be toggled until it is enabled.");
    }

    /// <exception cref="McpToolException"><c>refused</c> for this server's own plugin, <c>unavailable</c> when it cannot be reloaded now.</exception>
    public static void EnsureCanReload(PluginToggleFacts facts, string ownInternalName)
    {
        RefuseSelf(facts, ownInternalName, "reload");
        EnsureCommon(facts);
        if (!facts.IsLoaded)
            throw Blocked($"{facts.InternalName} is not loaded (state {facts.State}); there is nothing to reload. set_plugin_enabled loads it.");
    }

    /// <summary>True when the plugin is already where the caller wants it, so nothing needs doing.</summary>
    public static bool AlreadyInState(PluginToggleFacts facts, bool enable) =>
        enable ? facts.IsLoaded : string.Equals(facts.State, "Unloaded", StringComparison.Ordinal);

    private static void RefuseSelf(PluginToggleFacts facts, string ownInternalName, string verb)
    {
        if (string.Equals(facts.InternalName, ownInternalName, StringComparison.OrdinalIgnoreCase))
        {
            throw McpToolException.WithCode(McpErrorCodes.Refused,
                $"Refusing to {verb} {ownInternalName}: that is this MCP server, and the connection would drop in the middle of the call. The player can do it in the plugin installer (/xlplugins, Installed Plugins).");
        }
    }

    private static void EnsureCommon(PluginToggleFacts facts)
    {
        if (facts.SafeMode)
            throw Blocked("Dalamud is in plugin safe mode: no plugin can be loaded or unloaded until the game is restarted without it.");
        if (facts.ProfilesBusy)
            throw Blocked("Dalamud is busy applying plugin collections; try again in a moment.", retryable: true);
        if (facts.State is "Loading" or "Unloading")
            throw Blocked($"{facts.InternalName} is {facts.State.ToLowerInvariant()} right now; try again in a moment.", retryable: true);
        if (facts.State is "UnloadError")
            throw Blocked($"{facts.InternalName} failed to unload earlier; Dalamud needs a game restart before it can be loaded again.");
    }

    private static McpToolException Blocked(string message, bool retryable = false) =>
        McpToolException.WithCode(McpErrorCodes.Unavailable, message, retryable);
}

/// <summary>Which Dalamud window and tab open_dalamud_window was asked for. Pure parsing of the two string arguments.</summary>
public sealed record DalamudWindowTarget(bool IsInstaller, Dalamud.Interface.PluginInstallerOpenKind InstallerTab, Dalamud.Interface.SettingsOpenKind SettingsTab, string? SearchText)
{
    public const int MaxSearchLength = 100;

    public static readonly string[] InstallerTabs = ["allPlugins", "installedPlugins", "updateablePlugins", "changelogs", "dalamudChangelogs"];

    public static readonly string[] SettingsTabs = ["general", "lookAndFeel", "autoUpdates", "serverInfoBar", "badge", "experimental", "about"];

    /// <exception cref="McpToolException"><c>invalid_arguments</c> naming the allowed values.</exception>
    public static DalamudWindowTarget Parse(string? window, string? tab, string? searchText)
    {
        var search = searchText?.Trim();
        if (string.IsNullOrEmpty(search))
            search = null;
        if (search is not null && (search.Length > MaxSearchLength || search.Any(char.IsControl)))
            throw Invalid($"searchText must be a single line of at most {MaxSearchLength} characters.");

        var tabText = string.IsNullOrWhiteSpace(tab) ? null : tab.Trim();
        switch (window?.Trim().ToUpperInvariant())
        {
            case "INSTALLER" or "PLUGIN_INSTALLER" or "PLUGININSTALLER":
                return new DalamudWindowTarget(true, ParseTab<Dalamud.Interface.PluginInstallerOpenKind>(tabText, InstallerTabs), default, search);
            case "SETTINGS" or "DALAMUD_SETTINGS":
                return new DalamudWindowTarget(false, default, ParseTab<Dalamud.Interface.SettingsOpenKind>(tabText, SettingsTabs), search);
            default:
                throw Invalid("window must be 'installer' (the plugin installer) or 'settings' (Dalamud settings).");
        }
    }

    private static T ParseTab<T>(string? tab, string[] allowed)
        where T : struct, Enum
    {
        if (tab is null)
            return default;
        var known = allowed.FirstOrDefault(a => string.Equals(a, tab, StringComparison.OrdinalIgnoreCase));
        if (known is not null && Enum.TryParse<T>(known, ignoreCase: true, out var value))
            return value;
        throw Invalid($"tab '{(tab.Length > 40 ? tab[..40] : tab)}' is not a tab of that window. Allowed: {string.Join(", ", allowed)}.");
    }

    private static McpToolException Invalid(string message) => McpToolException.WithCode(McpErrorCodes.InvalidArguments, message);
}

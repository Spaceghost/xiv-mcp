using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Dalamud.Plugin;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>One non-public Dalamud type or member this plugin reaches by reflection.</summary>
/// <param name="Key">How the code refers to it.</param>
/// <param name="TypeName">Full type name in Dalamud.dll.</param>
/// <param name="Member">Property or method name; null for the type itself.</param>
/// <param name="IsStatic">Static member.</param>
/// <param name="Parameters">For methods: the parameter types' simple names, in order. Null for properties and types.</param>
public sealed record DalamudInternalName(string Key, string TypeName, string? Member = null, bool IsStatic = false, string[]? Parameters = null);

/// <summary>What the plugin manager knows about one installed plugin. <see cref="Instance"/> is Dalamud's LocalPlugin and never leaves the provider.</summary>
internal sealed record InternalPlugin(object Instance, string InternalName, string Name, string State, bool IsLoaded, bool IsDev, bool IsOutdated, bool IsBanned, bool IsOrphaned, Guid WorkingPluginId);

internal sealed record InternalRepository(string Url, bool IsEnabled);

internal sealed record InternalDrawStats(string InternalName, string Name, long LastTicks, long MaxTicks, long AverageTicks);

/// <summary>The profile ("collection") state the installer looks at before it lets its toggle be clicked.</summary>
internal sealed record InternalToggleContext(bool SafeMode, bool ProfilesBusy, int WantingProfiles, object? SingleProfile, bool SingleProfileIsDefault, bool SingleProfileEnabled);

/// <summary>
/// The ONLY place that touches Dalamud's non-public surface. Every reflected name is in <see cref="Names"/>;
/// <c>DalamudInternalsTests</c> checks that table against the Dalamud.dll the build references, so a Dalamud update that
/// renames something fails CI. At run time a missing name makes the feature return null and the tool answer
/// <c>unavailable</c>; nothing here throws for a missing member.
/// </summary>
internal static class DalamudInternals
{
    private const string ServiceT = "Dalamud.Service`1";
    private const string Configuration = "Dalamud.Configuration.Internal.DalamudConfiguration";
    private const string RepoSettings = "Dalamud.Configuration.ThirdPartyRepoSettings";
    private const string PluginManager = "Dalamud.Plugin.Internal.PluginManager";
    private const string ProfileManager = "Dalamud.Plugin.Internal.Profiles.ProfileManager";
    private const string Profile = "Dalamud.Plugin.Internal.Profiles.Profile";
    private const string LocalPlugin = "Dalamud.Plugin.Internal.Types.LocalPlugin";
    private const string LocalDevPlugin = "Dalamud.Plugin.Internal.Types.LocalDevPlugin";
    private const string AvailableUpdate = "Dalamud.Plugin.Internal.Types.AvailablePluginUpdate";
    private const string PluginInterface = "Dalamud.Plugin.DalamudPluginInterface";
    private const string UiBuilder = "Dalamud.Interface.UiBuilder";
    private const string DrawStatistics = "Dalamud.Interface.PluginDrawStatistics";
    private const string Framework = "Dalamud.Game.Framework";

    /// <summary>Every internal name relied on. Keep it complete: the test reads this table, the code resolves only through it.</summary>
    public static readonly IReadOnlyList<DalamudInternalName> Names =
    [
        new("Service", ServiceT),
        new("Service.GetNullable", ServiceT, "GetNullable", IsStatic: true, Parameters: ["ExceptionPropagationMode"]),

        new("Configuration", Configuration),
        new("Configuration.ThirdRepoList", Configuration, "ThirdRepoList"),
        new("RepoSettings.Url", RepoSettings, "Url"),
        new("RepoSettings.IsEnabled", RepoSettings, "IsEnabled"),

        new("PluginManager", PluginManager),
        new("PluginManager.InstalledPlugins", PluginManager, "InstalledPlugins"),
        new("PluginManager.UpdatablePlugins", PluginManager, "UpdatablePlugins"),
        new("PluginManager.SafeMode", PluginManager, "SafeMode"),
        new("AvailableUpdate.InstalledPlugin", AvailableUpdate, "InstalledPlugin"),

        new("ProfileManager", ProfileManager),
        new("ProfileManager.Profiles", ProfileManager, "Profiles"),
        new("ProfileManager.IsBusy", ProfileManager, "IsBusy"),
        new("Profile.IsEnabled", Profile, "IsEnabled"),
        new("Profile.IsDefaultProfile", Profile, "IsDefaultProfile"),
        new("Profile.WantsPlugin", Profile, "WantsPlugin", Parameters: ["Guid"]),
        new("Profile.AddOrUpdateAsync", Profile, "AddOrUpdateAsync", Parameters: ["Guid", "String", "Boolean", "Boolean"]),

        new("LocalPlugin.InternalName", LocalPlugin, "InternalName"),
        new("LocalPlugin.Name", LocalPlugin, "Name"),
        new("LocalPlugin.State", LocalPlugin, "State"),
        new("LocalPlugin.IsLoaded", LocalPlugin, "IsLoaded"),
        new("LocalPlugin.IsDev", LocalPlugin, "IsDev"),
        new("LocalPlugin.IsOutdated", LocalPlugin, "IsOutdated"),
        new("LocalPlugin.IsBanned", LocalPlugin, "IsBanned"),
        new("LocalPlugin.IsOrphaned", LocalPlugin, "IsOrphaned"),
        new("LocalPlugin.EffectiveWorkingPluginId", LocalPlugin, "EffectiveWorkingPluginId"),
        new("LocalPlugin.DalamudInterface", LocalPlugin, "DalamudInterface"),
        new("LocalPlugin.LoadAsync", LocalPlugin, "LoadAsync", Parameters: ["PluginLoadReason", "Boolean", "CancellationToken"]),
        new("LocalPlugin.UnloadAsync", LocalPlugin, "UnloadAsync", Parameters: ["PluginLoaderDisposalMode"]),
        new("LocalPlugin.ReloadAsync", LocalPlugin, "ReloadAsync", Parameters: []),
        new("LocalDevPlugin.ReloadManifest", LocalDevPlugin, "ReloadManifest", Parameters: []),

        new("PluginInterface.LocalUiBuilder", PluginInterface, "LocalUiBuilder"),
        new("UiBuilder.DoStats", UiBuilder, "DoStats", IsStatic: true),
        new("UiBuilder.PluginDrawStatistics", UiBuilder, "PluginDrawStatistics"),
        new("DrawStatistics.LastDrawTime", DrawStatistics, "LastDrawTime"),
        new("DrawStatistics.MaxDrawTime", DrawStatistics, "MaxDrawTime"),
        new("DrawStatistics.AverageDrawTime", DrawStatistics, "AverageDrawTime"),

        new("Framework.StatsEnabled", Framework, "StatsEnabled", IsStatic: true),
        new("Framework.StatsHistory", Framework, "StatsHistory", IsStatic: true),
    ];

    public static readonly string[] RepositoryKeys = ["Service.GetNullable", "Configuration", "Configuration.ThirdRepoList", "RepoSettings.Url", "RepoSettings.IsEnabled"];

    public static readonly string[] SafeModeKeys = ["Service.GetNullable", "PluginManager", "PluginManager.SafeMode"];

    public static readonly string[] PluginKeys =
    [
        "Service.GetNullable", "PluginManager", "PluginManager.InstalledPlugins", "LocalPlugin.InternalName", "LocalPlugin.Name", "LocalPlugin.State",
        "LocalPlugin.IsLoaded", "LocalPlugin.IsDev", "LocalPlugin.IsOutdated", "LocalPlugin.IsBanned", "LocalPlugin.IsOrphaned", "LocalPlugin.EffectiveWorkingPluginId",
    ];

    public static readonly string[] UpdatableKeys = ["Service.GetNullable", "PluginManager", "PluginManager.UpdatablePlugins", "AvailableUpdate.InstalledPlugin", "LocalPlugin.InternalName"];

    public static readonly string[] ToggleKeys =
    [
        .. PluginKeys, "PluginManager.SafeMode", "ProfileManager", "ProfileManager.Profiles", "ProfileManager.IsBusy", "Profile.IsEnabled", "Profile.IsDefaultProfile",
        "Profile.WantsPlugin", "Profile.AddOrUpdateAsync", "LocalPlugin.LoadAsync", "LocalPlugin.UnloadAsync",
    ];

    public static readonly string[] ReloadKeys = [.. PluginKeys, "PluginManager.SafeMode", "ProfileManager", "ProfileManager.IsBusy", "LocalPlugin.ReloadAsync"];

    public static readonly string[] DrawStatsKeys =
    [
        .. PluginKeys, "LocalPlugin.DalamudInterface", "PluginInterface.LocalUiBuilder", "UiBuilder.DoStats", "UiBuilder.PluginDrawStatistics",
        "DrawStatistics.LastDrawTime", "DrawStatistics.MaxDrawTime", "DrawStatistics.AverageDrawTime",
    ];

    public static readonly string[] FrameworkStatsKeys = ["Framework.StatsEnabled", "Framework.StatsHistory"];

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly ConcurrentDictionary<string, object?> Cache = new(StringComparer.Ordinal);

    private static Assembly DalamudAssembly => typeof(IDalamudPluginInterface).Assembly;

    /// <summary>Resolves one table entry against an assembly: a <see cref="Type"/>, <see cref="PropertyInfo"/> or <see cref="MethodInfo"/>, or null.</summary>
    public static object? Resolve(Assembly dalamud, DalamudInternalName name)
    {
        try
        {
            var type = dalamud.GetType(name.TypeName, throwOnError: false);
            if (type is null || name.Member is null)
                return type;

            if (name.Parameters is null)
            {
                var property = type.GetProperty(name.Member, Any);
                var getter = property?.GetGetMethod(nonPublic: true);
                return getter is not null && getter.IsStatic == name.IsStatic ? property : null;
            }

            return type.GetMethods(Any).SingleOrDefault(m =>
                m.Name == name.Member && m.IsStatic == name.IsStatic &&
                m.GetParameters().Select(static p => p.ParameterType.Name).SequenceEqual(name.Parameters, StringComparer.Ordinal));
        }
        catch (Exception e) when (e is AmbiguousMatchException or InvalidOperationException or TypeLoadException or FileNotFoundException or FileLoadException)
        {
            return null;
        }
    }

    /// <summary>Keys of <see cref="Names"/> that do not resolve in <paramref name="dalamud"/>.</summary>
    public static IReadOnlyList<string> FindMissing(Assembly dalamud) =>
        Names.Where(n => Resolve(dalamud, n) is null).Select(static n => n.Key).ToList();

    /// <summary>The first of <paramref name="keys"/> that this Dalamud build lacks, as "Type.Member", or null when all are there.</summary>
    public static string? FirstMissing(IEnumerable<string> keys) => keys.FirstOrDefault(k => Lookup(k) is null);

    public static IReadOnlyList<InternalRepository>? GetRepositories()
    {
        if (FirstMissing(RepositoryKeys) is not null || Service("Configuration") is not { } config)
            return null;
        if (Get("Configuration.ThirdRepoList", config) is not IEnumerable list)
            return null;

        var result = new List<InternalRepository>();
        foreach (var repo in list)
        {
            if (repo is null)
                continue;
            result.Add(new InternalRepository(Get("RepoSettings.Url", repo) as string ?? "", Get("RepoSettings.IsEnabled", repo) is true));
        }

        return result;
    }

    public static bool? IsSafeMode()
    {
        if (FirstMissing(SafeModeKeys) is not null || Service("PluginManager") is not { } manager)
            return null;
        return Get("PluginManager.SafeMode", manager) as bool?;
    }

    public static IReadOnlyList<InternalPlugin>? GetPlugins()
    {
        if (FirstMissing(PluginKeys) is not null || Service("PluginManager") is not { } manager)
            return null;
        if (Get("PluginManager.InstalledPlugins", manager) is not IEnumerable list)
            return null;

        var result = new List<InternalPlugin>();
        foreach (var plugin in list)
        {
            if (plugin is null)
                continue;
            result.Add(new InternalPlugin(
                plugin,
                Get("LocalPlugin.InternalName", plugin) as string ?? "",
                Get("LocalPlugin.Name", plugin) as string ?? "",
                Get("LocalPlugin.State", plugin)?.ToString() ?? "Unknown",
                Get("LocalPlugin.IsLoaded", plugin) is true,
                Get("LocalPlugin.IsDev", plugin) is true,
                Get("LocalPlugin.IsOutdated", plugin) is true,
                Get("LocalPlugin.IsBanned", plugin) is true,
                Get("LocalPlugin.IsOrphaned", plugin) is true,
                Get("LocalPlugin.EffectiveWorkingPluginId", plugin) as Guid? ?? Guid.Empty));
        }

        return result;
    }

    /// <summary>Internal names the plugin installer currently lists as updatable (from the repository data Dalamud last fetched).</summary>
    public static IReadOnlySet<string>? GetUpdatableInternalNames()
    {
        if (FirstMissing(UpdatableKeys) is not null || Service("PluginManager") is not { } manager)
            return null;
        if (Get("PluginManager.UpdatablePlugins", manager) is not IEnumerable list)
            return null;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var update in list)
        {
            if (update is not null && Get("AvailableUpdate.InstalledPlugin", update) is { } plugin && Get("LocalPlugin.InternalName", plugin) is string name)
                result.Add(name);
        }

        return result;
    }

    public static InternalToggleContext? GetToggleContext(InternalPlugin plugin)
    {
        if (FirstMissing(ToggleKeys) is not null || Service("PluginManager") is not { } manager || Service("ProfileManager") is not { } profiles)
            return null;
        if (Get("ProfileManager.Profiles", profiles) is not IEnumerable list)
            return null;

        var wanting = new List<object>();
        foreach (var profile in list)
        {
            // Profile.WantsPlugin returns bool?: null means the profile does not list the plugin at all.
            if (profile is not null && Call("Profile.WantsPlugin", profile, plugin.WorkingPluginId) is bool)
                wanting.Add(profile);
        }

        var single = wanting.Count == 1 ? wanting[0] : null;
        return new InternalToggleContext(
            Get("PluginManager.SafeMode", manager) is true,
            Get("ProfileManager.IsBusy", profiles) is true,
            wanting.Count,
            single,
            single is not null && Get("Profile.IsDefaultProfile", single) is true,
            single is not null && Get("Profile.IsEnabled", single) is true);
    }

    /// <summary>(safeMode, profilesBusy) for reload_plugin, or null.</summary>
    public static (bool SafeMode, bool ProfilesBusy)? GetReloadContext()
    {
        if (FirstMissing(ReloadKeys) is not null || Service("PluginManager") is not { } manager || Service("ProfileManager") is not { } profiles)
            return null;
        return (Get("PluginManager.SafeMode", manager) is true, Get("ProfileManager.IsBusy", profiles) is true);
    }

    /// <summary>
    /// What the plugin installer's toggle does (PluginInstallerWindow.DrawPluginControlButton): disabling unloads and then
    /// records "not wanted" in the profile; enabling records "wanted" and then loads with PluginLoadReason.Installer.
    /// The update prompt the installer may show before enabling is deliberately not mirrored: nothing is ever updated here.
    /// </summary>
    public static async Task SetEnabledAsync(InternalPlugin plugin, object profile, bool enable)
    {
        if (plugin.IsDev && Lookup("LocalDevPlugin.ReloadManifest") is MethodInfo reloadManifest && reloadManifest.DeclaringType!.IsInstanceOfType(plugin.Instance))
        {
            try
            {
                reloadManifest.Invoke(plugin.Instance, null);
            }
            catch (TargetInvocationException)
            {
                // The installer logs and carries on as well.
            }
        }

        if (enable)
        {
            await AwaitCall("Profile.AddOrUpdateAsync", profile, plugin.WorkingPluginId, plugin.InternalName, true, false).ConfigureAwait(false);
            await AwaitCall("LocalPlugin.LoadAsync", plugin.Instance, PluginLoadReason.Installer).ConfigureAwait(false);
        }
        else
        {
            await AwaitCall("LocalPlugin.UnloadAsync", plugin.Instance).ConfigureAwait(false);
            await AwaitCall("Profile.AddOrUpdateAsync", profile, plugin.WorkingPluginId, plugin.InternalName, false, false).ConfigureAwait(false);
        }
    }

    public static Task ReloadAsync(InternalPlugin plugin) => AwaitCall("LocalPlugin.ReloadAsync", plugin.Instance);

    /// <summary>Null when unreachable; <c>Enabled = false</c> when the player has not switched draw statistics on in Dalamud's Plugin Statistics window.</summary>
    public static (bool Enabled, IReadOnlyList<InternalDrawStats> Plugins)? GetDrawStats()
    {
        if (FirstMissing(DrawStatsKeys) is not null || GetPlugins() is not { } plugins)
            return null;

        var enabled = Get("UiBuilder.DoStats", null) is true;
        var result = new List<InternalDrawStats>();
        foreach (var plugin in plugins)
        {
            if (Get("LocalPlugin.DalamudInterface", plugin.Instance) is not { } pluginInterface ||
                Get("PluginInterface.LocalUiBuilder", pluginInterface) is not { } uiBuilder ||
                Get("UiBuilder.PluginDrawStatistics", uiBuilder) is not { } stats)
                continue;
            result.Add(new InternalDrawStats(
                plugin.InternalName,
                plugin.Name,
                Get("DrawStatistics.LastDrawTime", stats) as long? ?? -1,
                Get("DrawStatistics.MaxDrawTime", stats) as long? ?? -1,
                Get("DrawStatistics.AverageDrawTime", stats) as long? ?? -1));
        }

        return (enabled, result);
    }

    /// <summary>Framework.Update handler timings in milliseconds, keyed "Target::Method". Call on the framework thread: Dalamud mutates the history there.</summary>
    public static (bool Enabled, IReadOnlyDictionary<string, double[]> History)? GetFrameworkStats()
    {
        if (FirstMissing(FrameworkStatsKeys) is not null)
            return null;
        if (Get("Framework.StatsHistory", null) is not Dictionary<string, List<double>> history)
            return null;

        var copy = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (key, values) in history)
            copy[key] = [.. values];
        return (Get("Framework.StatsEnabled", null) is true, copy);
    }

    private static object? Lookup(string key) =>
        Cache.GetOrAdd(key, static k => Names.FirstOrDefault(n => n.Key == k) is { } name ? Resolve(DalamudAssembly, name) : null);

    private static object? Get(string key, object? target)
    {
        try
        {
            return (Lookup(key) as PropertyInfo)?.GetValue(target);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static object? Call(string key, object? target, params object?[] leadingArguments)
    {
        if (Lookup(key) is not MethodInfo method)
            return null;
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            arguments[i] = i < leadingArguments.Length ? leadingArguments[i] : DefaultArgument(parameters[i]);
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static async Task AwaitCall(string key, object? target, params object?[] leadingArguments)
    {
        if (Call(key, target, leadingArguments) is Task task)
            await task.ConfigureAwait(false);
        else
            throw new InvalidOperationException($"Dalamud's {key} did not return a task.");
    }

    /// <summary>The declared default of an optional parameter, typed for Invoke (enum defaults come back as their underlying integer).</summary>
    private static object? DefaultArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var value = parameter.HasDefaultValue ? parameter.RawDefaultValue : null;
        if (value is null || value is DBNull || value == Missing.Value)
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        return type.IsEnum ? Enum.ToObject(type, value) : value;
    }

    /// <summary>
    /// Service&lt;T&gt;.GetNullable(): the instance when the service is up, else null, without waiting. Only meaningful inside
    /// a running Dalamud: the first touch of Service&lt;T&gt; runs a static constructor that waits for Dalamud's service container.
    /// </summary>
    private static object? Service(string typeKey)
    {
        // The table's open-generic MethodInfo cannot be invoked; take its name and find it again on Service<T>.
        if (Lookup("Service") is not Type open || Lookup(typeKey) is not Type service || Lookup("Service.GetNullable") is not MethodInfo getNullable)
            return null;
        try
        {
            var closed = open.MakeGenericType(service);
            var method = closed.GetMethod(getNullable.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method is null)
                return null;
            var parameters = method.GetParameters();
            return method.Invoke(null, parameters.Select(DefaultArgument).ToArray());
        }
        catch (Exception e) when (e is TargetInvocationException or ArgumentException or InvalidOperationException or AmbiguousMatchException)
        {
            return null;
        }
    }
}

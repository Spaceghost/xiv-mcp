using System.Reflection;
using Dalamud.Plugin;
using XivMcp.Plugin.Providers.DalamudInfo;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>
/// DalamudInternals reaches non-public Dalamud members by name. This checks every name in its table against the
/// Dalamud.dll the build references (metadata only: nothing in Dalamud is constructed or run), so a Dalamud update that
/// renames or reshapes one fails here instead of turning into "unavailable" in game. The feature methods themselves are
/// not called: Service&lt;T&gt;'s static constructor waits for Dalamud's service container, which never comes up in a test process.
/// </summary>
public class DalamudInternalsTests
{
    private static Assembly? ReferenceDalamud()
    {
        if (!File.Exists(Path.Combine(DalamudAssemblyResolver.LibPath, "Dalamud.dll")))
            return null;
        return typeof(IDalamudPluginInterface).Assembly;
    }

    [Fact]
    public void EveryInternalNameExistsInTheReferencedDalamud()
    {
        var dalamud = ReferenceDalamud();
        if (dalamud is null)
            Assert.Skip("Dalamud.dll is not available to this test run.");

        var missing = DalamudInternals.FindMissing(dalamud);
        Assert.True(missing.Count == 0, $"Dalamud {dalamud.GetName().Version} no longer has: {string.Join(", ", missing)}. Update DalamudInternals.Names and the code that uses them.");
    }

    [Fact]
    public void ReflectedMembersHaveTheShapesTheCodeAssumes()
    {
        var dalamud = ReferenceDalamud();
        if (dalamud is null)
            Assert.Skip("Dalamud.dll is not available to this test run.");

        Type PropertyType(string key) => Assert.IsAssignableFrom<PropertyInfo>(DalamudInternals.Resolve(dalamud, DalamudInternals.Names.Single(n => n.Key == key))).PropertyType;
        MethodInfo Method(string key) => Assert.IsAssignableFrom<MethodInfo>(DalamudInternals.Resolve(dalamud, DalamudInternals.Names.Single(n => n.Key == key)));

        Assert.Equal(typeof(string), PropertyType("RepoSettings.Url"));
        Assert.Equal(typeof(bool), PropertyType("RepoSettings.IsEnabled"));
        Assert.Equal(typeof(bool), PropertyType("PluginManager.SafeMode"));
        Assert.Equal(typeof(bool), PropertyType("ProfileManager.IsBusy"));
        Assert.Equal(typeof(Guid), PropertyType("LocalPlugin.EffectiveWorkingPluginId"));
        Assert.True(PropertyType("LocalPlugin.State").IsEnum);
        Assert.Equal(typeof(long), PropertyType("DrawStatistics.AverageDrawTime"));
        Assert.Equal(typeof(bool), PropertyType("UiBuilder.DoStats"));
        Assert.Equal(typeof(Dictionary<string, List<double>>), PropertyType("Framework.StatsHistory"));
        Assert.True(typeof(System.Collections.IEnumerable).IsAssignableFrom(PropertyType("Configuration.ThirdRepoList")));
        Assert.True(typeof(System.Collections.IEnumerable).IsAssignableFrom(PropertyType("PluginManager.InstalledPlugins")));
        Assert.True(typeof(System.Collections.IEnumerable).IsAssignableFrom(PropertyType("ProfileManager.Profiles")));

        Assert.Equal(typeof(bool?), Method("Profile.WantsPlugin").ReturnType);
        foreach (var key in new[] { "Profile.AddOrUpdateAsync", "LocalPlugin.LoadAsync", "LocalPlugin.UnloadAsync", "LocalPlugin.ReloadAsync" })
            Assert.Equal(typeof(Task), Method(key).ReturnType);

        // The state names PluginToggleRules and the summaries compare against.
        var states = Enum.GetNames(PropertyType("LocalPlugin.State"));
        foreach (var state in new[] { "Loaded", "Unloaded", "Loading", "Unloading", "LoadError", "UnloadError", "DependencyResolutionFailed" })
            Assert.Contains(state, states);

        // Arguments passed positionally by SetEnabledAsync: (Guid workingPluginId, string internalName, bool state, bool apply) and (PluginLoadReason reason, ...).
        Assert.Equal("workingPluginId,internalName,state,apply", string.Join(",", Method("Profile.AddOrUpdateAsync").GetParameters().Select(p => p.Name)));
        Assert.Equal(typeof(PluginLoadReason), Method("LocalPlugin.LoadAsync").GetParameters()[0].ParameterType);
        Assert.All(Method("LocalPlugin.LoadAsync").GetParameters().Skip(1), p => Assert.True(p.HasDefaultValue));
        Assert.All(Method("LocalPlugin.UnloadAsync").GetParameters(), p => Assert.True(p.HasDefaultValue));
    }

    [Fact]
    public void TheTableIsConsistent()
    {
        var keys = DalamudInternals.Names.Select(n => n.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(DalamudInternals.Names, n => Assert.StartsWith("Dalamud.", n.TypeName, StringComparison.Ordinal));

        string[][] features =
        [
            DalamudInternals.RepositoryKeys, DalamudInternals.SafeModeKeys, DalamudInternals.PluginKeys, DalamudInternals.UpdatableKeys,
            DalamudInternals.ToggleKeys, DalamudInternals.ReloadKeys, DalamudInternals.DrawStatsKeys, DalamudInternals.FrameworkStatsKeys,
        ];
        foreach (var key in features.SelectMany(f => f))
            Assert.Contains(key, keys);
    }

    [Fact]
    public void AMissingNameResolvesToNullInsteadOfThrowing()
    {
        var here = typeof(DalamudInternalsTests).Assembly;
        Assert.Null(DalamudInternals.Resolve(here, new DalamudInternalName("x", "Dalamud.No.Such.Type")));
        Assert.Null(DalamudInternals.Resolve(here, new DalamudInternalName("x", typeof(DalamudInternalsTests).FullName!, "NoSuchProperty")));
        Assert.Null(DalamudInternals.Resolve(here, new DalamudInternalName("x", typeof(DalamudInternalsTests).FullName!, "ReferenceDalamud", IsStatic: true, Parameters: ["String"])));
        Assert.NotNull(DalamudInternals.Resolve(here, new DalamudInternalName("x", typeof(DalamudInternalsTests).FullName!, "ReferenceDalamud", IsStatic: true, Parameters: [])));
        Assert.Equal(DalamudInternals.Names.Count, DalamudInternals.FindMissing(here).Count);
    }
}

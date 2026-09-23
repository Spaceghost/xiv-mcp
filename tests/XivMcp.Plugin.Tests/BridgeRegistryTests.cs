using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivMcp.Plugin.Bridges;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>
/// Which install a bridge looks at when one plugin is installed more than once. Seen in game on v0.1.1-test.1:
/// XivDesktop 1.0.2.1 (testing) loaded, and a disabled dev copy (1.0.1.0) under the same internal name listed first,
/// and every XivDesktop tool answered "installed but not loaded".
/// </summary>
public class BridgeRegistryTests
{
    private static readonly BridgeDefinition Desktop = BridgeCatalog.All.Single(d => d.Key == "xivdesktop");

    private static IExposedPlugin Plugin(string internalName, bool loaded, string version, bool dev = false) =>
        FakeProxy.Create<IExposedPlugin>(new()
        {
            ["get_InternalName"] = _ => internalName,
            ["get_Name"] = _ => internalName,
            ["get_IsLoaded"] = _ => loaded,
            ["get_IsDev"] = _ => dev,
            ["get_Version"] = _ => Version.Parse(version),
        });

    private static BridgeRegistry Registry(params IExposedPlugin[] installed)
    {
        var gate = FakeProxy.Create<ICallGateSubscriber<string>>(new() { ["InvokeFunc"] = _ => "ok" });
        var pi = FakeProxy.Create<IDalamudPluginInterface>(new()
        {
            ["get_InstalledPlugins"] = _ => installed.ToList(),
            ["GetIpcSubscriber"] = _ => gate,
        });
        return new BridgeRegistry(pi, FakeProxy.Create<IPluginLog>());
    }

    [Fact]
    public void TheLoadedCopyWinsOverADisabledDevCopyListedFirst()
    {
        var status = Registry(
                Plugin("XivDesktop", loaded: false, "1.0.1.0", dev: true),
                Plugin("XivDesktop", loaded: true, "1.0.2.1"))
            .Probe(Desktop);

        Assert.True(status.Installed);
        Assert.True(status.Loaded);
        Assert.Equal("1.0.2.1", status.Version);
        Assert.True(status.IpcAvailable);
        Assert.Null(status.Unavailable);
    }

    [Fact]
    public void TheLoadedCopyWinsWhicheverOrderDalamudListsThem()
    {
        var status = Registry(
                Plugin("XivDesktop", loaded: true, "1.0.2.1"),
                Plugin("xivdesktop", loaded: false, "1.0.1.0", dev: true))
            .Probe(Desktop);

        Assert.True(status.Loaded);
        Assert.Equal("1.0.2.1", status.Version);
    }

    [Fact]
    public void WithNoLoadedCopyItIsStillInstalledButNotLoaded()
    {
        var status = Registry(
                Plugin("XivDesktop", loaded: false, "1.0.1.0", dev: true),
                Plugin("XivDesktop", loaded: false, "1.0.2.1"))
            .Probe(Desktop);

        Assert.True(status.Installed);
        Assert.False(status.Loaded);
        Assert.Equal("1.0.1.0", status.Version);
        Assert.Contains("installed but not loaded", status.Unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public void NotInstalledIsNotInstalled()
    {
        var status = Registry(Plugin("SomethingElse", loaded: true, "1.0.0.0")).Probe(Desktop);

        Assert.False(status.Installed);
        Assert.False(status.Loaded);
        Assert.Contains("not installed", status.Unavailable, StringComparison.Ordinal);
    }
}

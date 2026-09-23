using Dalamud.Interface;
using XivMcp.Core;
using XivMcp.Plugin.Providers.DalamudInfo;

namespace XivMcp.Plugin.Tests;

/// <summary>The decisions behind the Dalamud action tools, without Dalamud: name resolution, URL rules, window tabs, toggle rules.</summary>
public class DalamudToolLogicTests
{
    private static readonly (string InternalName, string Name)[] Installed =
    [
        ("XivMcp", "XIV MCP"),
        ("SomePlugin", "Some Plugin"),
        ("SomePluginExtra", "Some Plugin Extra"),
        ("TwinA", "Twin"),
        ("TwinB", "Twin"),
        ("Dup", "Dup (repo)"),
        ("Dup", "Dup (dev)"),
        ("Some Plugin", "Named Like Another's Display Name"),
    ];

    private static string Code(Action call) => Assert.Throws<McpToolException>(call).Code;

    [Theory]
    [InlineData("SomePlugin", 1)]
    [InlineData("  someplugin ", 1)]
    [InlineData("Some Plugin Extra", 2)]
    [InlineData("xiv mcp", 0)]
    [InlineData("Some Plugin", 7)] // an exact internal name wins over another plugin's display name
    public void ResolvesInternalNameFirstThenDisplayName(string wanted, int expected) =>
        Assert.Equal(expected, PluginNameResolver.Resolve(Installed, wanted));

    [Theory]
    [InlineData("Twin")]
    [InlineData("Dup")]
    public void AmbiguousNamesAreRejectedNotGuessed(string wanted)
    {
        var ex = Assert.Throws<McpToolException>(() => PluginNameResolver.Resolve(Installed, wanted));
        Assert.Equal(McpErrorCodes.InvalidArguments, ex.Code);
        Assert.Contains("matches 2", ex.Message, StringComparison.Ordinal);
    }

    private static readonly (string InternalName, string Name, bool IsLoaded)[] InstalledTwice =
    [
        ("XivDesktop", "XivDesktop", false), // the disabled dev copy, listed first
        ("XivDesktop", "XivDesktop", true),
        ("TwinA", "Twin", true),
        ("TwinB", "Twin", false),
        ("Both", "Both (repo)", true),
        ("Both", "Both (dev)", true),
        ("Neither", "Neither (repo)", false),
        ("Neither", "Neither (dev)", false),
    ];

    [Theory]
    [InlineData("XivDesktop", 1)]
    [InlineData("xivdesktop", 1)]
    public void OnePluginInstalledTwiceResolvesToTheLoadedCopy(string wanted, int expected) =>
        Assert.Equal(expected, PluginNameResolver.Resolve(InstalledTwice, wanted));

    [Theory]
    [InlineData("Twin")] // two different plugins with one display name: never guessed, loaded or not
    [InlineData("Both")] // both copies loaded
    [InlineData("Neither")] // no copy loaded
    public void CopiesStayAmbiguousUnlessExactlyOneIsLoaded(string wanted) =>
        Assert.Equal(McpErrorCodes.InvalidArguments, Code(() => PluginNameResolver.Resolve(InstalledTwice, wanted)));

    private sealed record Install(string InternalName, bool IsLoaded, bool IsDev, string State);

    [Fact]
    public void PreferLoadedTakesTheFirstLoadedElseTheFirst()
    {
        bool[] secondLoaded = [false, true, true];
        bool[] noneLoaded = [false, false];
        Assert.Equal(1, PluginInstances.PreferLoaded(secondLoaded, static l => l));
        Assert.Equal(0, PluginInstances.PreferLoaded(noneLoaded, static l => l));
        Assert.Equal(-1, PluginInstances.PreferLoaded(Array.Empty<bool>(), static l => l));
    }

    [Fact]
    public void FindTakesTheLoadedInstallOfThatName()
    {
        Install[] installs =
        [
            new("Other", true, false, "Loaded"),
            new("XivDesktop", false, true, "Unloaded"),
            new("XIVDESKTOP", true, false, "Loaded"),
        ];

        Assert.Same(installs[2], PluginInstances.Find(installs, "XivDesktop", static p => p.InternalName, static p => p.IsLoaded));
        Assert.Same(installs[1], PluginInstances.Find(installs.Take(2), "XivDesktop", static p => p.InternalName, static p => p.IsLoaded));
        Assert.Null(PluginInstances.Find(installs, "Missing", static p => p.InternalName, static p => p.IsLoaded));
    }

    [Fact]
    public void FindByKindPairsADevCopyWithItsOwnState()
    {
        Install[] installs =
        [
            new("XivDesktop", false, true, "Unloaded"),
            new("XivDesktop", true, false, "Loaded"),
        ];

        Assert.Equal("Unloaded", PluginInstances.Find(installs, "XivDesktop", isDev: true, static p => p.InternalName, static p => p.IsDev, static p => p.IsLoaded)!.State);
        Assert.Equal("Loaded", PluginInstances.Find(installs, "XivDesktop", isDev: false, static p => p.InternalName, static p => p.IsDev, static p => p.IsLoaded)!.State);
        // With no install of that kind, the loaded one of the other kind.
        Assert.Equal("Loaded", PluginInstances.Find(installs[1..], "XivDesktop", isDev: true, static p => p.InternalName, static p => p.IsDev, static p => p.IsLoaded)!.State);
    }

    [Fact]
    public void NotLoadedLeavesOutNamesThatHaveALoadedCopy()
    {
        Install[] installs =
        [
            new("XivDesktop", false, true, "Unloaded"),
            new("XivDesktop", true, false, "Loaded"),
            new("Sleeping", false, false, "Unloaded"),
        ];

        Assert.Equal(["Sleeping"], PluginInstances.NotLoaded(installs, static p => p.InternalName, static p => p.IsLoaded).Select(static p => p.InternalName));
    }

    [Fact]
    public void UnknownNamesAreNotFoundWithSuggestionsAndNoInstallOffer()
    {
        var ex = Assert.Throws<McpToolException>(() => PluginNameResolver.Resolve(Installed, "Some"));
        Assert.Equal(McpErrorCodes.NotFound, ex.Code);
        Assert.Contains("SomePlugin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("never installs", ex.Message, StringComparison.Ordinal);

        Assert.Equal(McpErrorCodes.NotFound, Code(() => PluginNameResolver.Resolve(Installed, "Nothing Like It")));
        Assert.Equal(McpErrorCodes.InvalidArguments, Code(() => PluginNameResolver.Resolve(Installed, "  ")));
        Assert.Equal(McpErrorCodes.InvalidArguments, Code(() => PluginNameResolver.Resolve(Installed, null)));
        Assert.Equal(McpErrorCodes.InvalidArguments, Code(() => PluginNameResolver.Resolve(Installed, new string('x', 121))));
    }

    [Theory]
    [InlineData("https://example.test/pluginmaster.json")]
    [InlineData("  https://raw.example.test/someone/repo/main/repo.json  ")]
    [InlineData("HTTPS://example.test:8443/a?b=c")]
    [InlineData("https://192.0.2.10/repo.json")]
    public void AcceptsPlainHttpsUrlsVerbatim(string url) => Assert.Equal(url.Trim(), RepositoryUrlValidator.Validate(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example.test/repo.json")]
    [InlineData("ftp://example.test/repo.json")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.test/repo.json")]
    [InlineData("https://")]
    [InlineData("https:///repo.json")]
    [InlineData("https://localhost/repo.json")]
    [InlineData("https://user:pw@example.test/repo.json")]
    [InlineData("https://user@example.test/repo.json")]
    [InlineData("https://example.test/a b")]
    [InlineData("https://example.test/a\nb")]
    [InlineData("https://exаmple.test/repo.json")]
    public void RejectsEverythingElse(string? url) => Assert.Equal(McpErrorCodes.InvalidArguments, Code(() => RepositoryUrlValidator.Validate(url)));

    [Fact]
    public void RejectsOverlongUrls() =>
        Assert.Equal(McpErrorCodes.InvalidArguments, Code(() => RepositoryUrlValidator.Validate("https://example.test/" + new string('a', RepositoryUrlValidator.MaxLength))));

    [Fact]
    public void ParsesDalamudWindowsAndTabs()
    {
        var installer = DalamudWindowTarget.Parse("installer", "updateablePlugins", "  Some Plugin ");
        Assert.True(installer.IsInstaller);
        Assert.Equal(PluginInstallerOpenKind.UpdateablePlugins, installer.InstallerTab);
        Assert.Equal("Some Plugin", installer.SearchText);

        var settings = DalamudWindowTarget.Parse("Settings", "EXPERIMENTAL", "");
        Assert.False(settings.IsInstaller);
        Assert.Equal(SettingsOpenKind.Experimental, settings.SettingsTab);
        Assert.Null(settings.SearchText);

        Assert.Equal(PluginInstallerOpenKind.AllPlugins, DalamudWindowTarget.Parse("installer", null, null).InstallerTab);
        Assert.Equal(SettingsOpenKind.General, DalamudWindowTarget.Parse("settings", " ", null).SettingsTab);
    }

    [Fact]
    public void EveryAdvertisedTabIsARealEnumMemberAndEveryMemberIsAdvertised()
    {
        Assert.Equal(Enum.GetNames<PluginInstallerOpenKind>().Order(StringComparer.OrdinalIgnoreCase), DalamudWindowTarget.InstallerTabs.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Enum.GetNames<SettingsOpenKind>().Order(StringComparer.OrdinalIgnoreCase), DalamudWindowTarget.SettingsTabs.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("devmenu", null, null)]
    [InlineData(null, null, null)]
    [InlineData("installer", "experimental", null)] // a settings tab
    [InlineData("settings", "allPlugins", null)]
    [InlineData("installer", "2", null)] // numeric enum values are not tabs
    [InlineData("installer", null, "two\nlines")]
    public void RejectsUnknownWindowsTabsAndSearches(string? window, string? tab, string? search) =>
        Assert.Equal(McpErrorCodes.InvalidArguments, Code(() => DalamudWindowTarget.Parse(window, tab, search)));

    [Fact]
    public void RejectsOverlongSearchText() =>
        Assert.Equal(McpErrorCodes.InvalidArguments, Code(() => DalamudWindowTarget.Parse("installer", null, new string('s', DalamudWindowTarget.MaxSearchLength + 1))));

    private static PluginToggleFacts Facts(string name = "SomePlugin", string state = "Loaded") =>
        new(name, state, state == "Loaded", false, false, false, false, false, false, 1, true, true);

    [Fact]
    public void AnOrdinaryPluginCanBeToggledAndReloaded()
    {
        PluginToggleRules.EnsureCanSetEnabled(Facts(), enable: false, "XivMcp");
        PluginToggleRules.EnsureCanSetEnabled(Facts(state: "Unloaded"), enable: true, "XivMcp");
        PluginToggleRules.EnsureCanSetEnabled(Facts(state: "LoadError"), enable: true, "XivMcp");
        PluginToggleRules.EnsureCanReload(Facts(), "XivMcp");

        Assert.True(PluginToggleRules.AlreadyInState(Facts(), enable: true));
        Assert.True(PluginToggleRules.AlreadyInState(Facts(state: "Unloaded"), enable: false));
        Assert.False(PluginToggleRules.AlreadyInState(Facts(state: "LoadError"), enable: true));
        Assert.False(PluginToggleRules.AlreadyInState(Facts(state: "LoadError"), enable: false));
    }

    [Fact]
    public void RefusesToCutItsOwnConnection()
    {
        var disable = Assert.Throws<McpToolException>(() => PluginToggleRules.EnsureCanSetEnabled(Facts("xivmcp"), enable: false, "XivMcp"));
        Assert.Equal(McpErrorCodes.Refused, disable.Code);
        Assert.Contains("/xlplugins", disable.Message, StringComparison.Ordinal);

        Assert.Equal(McpErrorCodes.Refused, Code(() => PluginToggleRules.EnsureCanReload(Facts("XivMcp"), "XivMcp")));

        // Enabling itself is a no-op question, not a refusal.
        PluginToggleRules.EnsureCanSetEnabled(Facts("XivMcp"), enable: true, "XivMcp");
    }

    [Fact]
    public void MirrorsTheInstallersGreyedOutToggle()
    {
        (PluginToggleFacts Facts, bool Retryable)[] blocked =
        [
            (Facts() with { SafeMode = true }, false),
            (Facts() with { ProfilesBusy = true }, true),
            (Facts(state: "Loading"), true),
            (Facts(state: "Unloading"), true),
            (Facts(state: "UnloadError"), false),
            (Facts(state: "Unloaded") with { IsBanned = true }, false),
            (Facts(state: "Unloaded") with { IsOutdated = true }, false),
            (Facts(state: "Unloaded") with { IsOrphaned = true }, false),
            (Facts() with { WantingProfiles = 0 }, false),
            (Facts() with { WantingProfiles = 2 }, false),
            (Facts() with { SingleProfileIsDefault = false }, false),
            (Facts() with { SingleProfileEnabled = false }, false),
        ];

        foreach (var (facts, retryable) in blocked)
        {
            var ex = Assert.Throws<McpToolException>(() => PluginToggleRules.EnsureCanSetEnabled(facts, enable: true, "XivMcp"));
            Assert.Equal(McpErrorCodes.Unavailable, ex.Code);
            Assert.Equal(retryable, ex.Retryable);
        }
    }

    [Fact]
    public void DevPluginsMayBeOutdatedAndLoadedOrphansMayBeDisabled()
    {
        PluginToggleRules.EnsureCanSetEnabled(Facts(state: "Unloaded") with { IsOutdated = true, IsDev = true }, enable: true, "XivMcp");
        PluginToggleRules.EnsureCanSetEnabled(Facts() with { IsOrphaned = true }, enable: false, "XivMcp");
        Assert.Equal(McpErrorCodes.Unavailable, Code(() => PluginToggleRules.EnsureCanReload(Facts(state: "Unloaded"), "XivMcp")));
        Assert.Equal(McpErrorCodes.Unavailable, Code(() => PluginToggleRules.EnsureCanReload(Facts() with { SafeMode = true }, "XivMcp")));
    }
}

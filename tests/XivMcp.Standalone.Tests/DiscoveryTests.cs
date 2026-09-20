using XivMcp.Core;

namespace XivMcp.Standalone.Tests;

public sealed class GameLocatorTests
{
    private static Func<string, bool> Dirs(params string[] existing) => path => existing.Contains(path.Replace('\\', '/'));

    [Theory]
    [InlineData("/games/ffxiv")]
    [InlineData("/games/ffxiv/game")]
    [InlineData("/games/ffxiv/game/sqpack")]
    [InlineData("/games/ffxiv/game/sqpack/")]
    public void AcceptsTheInstallRootTheGameDirectoryOrSqpackItself(string given)
    {
        var found = GameLocator.Normalize(given, Dirs("/games/ffxiv/game/sqpack/ffxiv"));
        Assert.Equal("/games/ffxiv/game/sqpack", found?.Replace('\\', '/'));
    }

    [Fact]
    public void AWrongExplicitPathDoesNotFallThroughToAnotherInstall()
    {
        var found = GameLocator.Find("/nowhere", "/home/u", null, Dirs("/home/u/.xlcore/ffxiv/game/sqpack/ffxiv"), _ => null);
        Assert.Null(found);
    }

    [Fact]
    public void ReadsTheLauncherConfiguredPathBeforeTheDefault()
    {
        string? Read(string path) => path.Replace('\\', '/') == "/home/u/.xlcore/launcher.ini" ? "[Main]\nGamePath = /mnt/big/ffxiv\n" : null;
        var found = GameLocator.Find(null, "/home/u", null, Dirs("/mnt/big/ffxiv/game/sqpack/ffxiv", "/home/u/.xlcore/ffxiv/game/sqpack/ffxiv"), Read);
        Assert.Equal("/mnt/big/ffxiv/game/sqpack", found?.Replace('\\', '/'));
    }

    [Fact]
    public void FallsBackToTheLauncherDefaultAndToTheWindowsLauncherConfig()
    {
        Assert.Equal("/home/u/.xlcore/ffxiv/game/sqpack", GameLocator.Find(null, "/home/u", null, Dirs("/home/u/.xlcore/ffxiv/game/sqpack/ffxiv"), _ => null)?.Replace('\\', '/'));

        string? Read(string path) => path.Replace('\\', '/').EndsWith("XIVLauncher/launcherConfigV3.json", StringComparison.Ordinal) ? """{"GamePath":"D:/Games/FFXIV"}""" : null;
        Assert.Equal("D:/Games/FFXIV/game/sqpack", GameLocator.Find(null, "C:/Users/u", "C:/Users/u/AppData/Roaming", Dirs("D:/Games/FFXIV/game/sqpack/ffxiv"), Read)?.Replace('\\', '/'));
    }

    [Fact]
    public void NothingInstalledIsNull() => Assert.Null(GameLocator.Find(null, "/home/u", null, _ => false, _ => null));

    [Fact]
    public void BrokenLauncherFilesAreIgnored()
    {
        Assert.Null(GameLocator.JsonValue("{not json", "GamePath"));
        Assert.Null(GameLocator.JsonValue("[1]", "GamePath"));
        Assert.Null(GameLocator.IniValue("GamePath=", "GamePath"));
        Assert.Equal("/x", GameLocator.IniValue("Other=1\r\ngamepath=/x\r\n", "GamePath"));
    }
}

public sealed class SharedSettingsTests
{
    private const string Hash = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";

    [Fact]
    public void DefaultsMatchThePlugin()
    {
        var s = new SharedSettings();
        Assert.Equal((41800, "/mcp", true, 0), (s.Port, s.Path, s.RequireToken, s.BindMode));
        Assert.Null(s.BearerToken);
    }

    [Fact]
    public void ThePluginConfigurationSuppliesPortPathTokenAndClientTokens()
    {
        var json =
            "{\"Version\":3,\"Port\":41999,\"Path\":\"xiv/\",\"RequireToken\":true,\"BearerToken\":\"main-token-abcdef\",\"BindMode\":1," +
            "\"ClientTokens\":[{\"Name\":\"laptop\",\"TokenSha256\":\"" + Hash + "\"},{\"Name\":\"bad\",\"TokenSha256\":\"zz\"},{\"Name\":\"\",\"TokenSha256\":\"" + Hash + "\"}]," +
            "\"AllowedOrigins\":[\"https://example.test\"]}";
        var s = new SharedSettings().Overlay(json, "plugin");
        Assert.Equal((41999, "/xiv", "main-token-abcdef", 1), (s.Port, s.Path, s.BearerToken, s.BindMode));
        Assert.Equal(new ClientToken("laptop", Hash), Assert.Single(s.ClientTokens));
        Assert.Equal(["https://example.test"], s.AllowedOrigins);
        Assert.Equal(["plugin"], s.Sources);
    }

    [Fact]
    public void TheProvisioningFileWinsOverThePluginConfiguration()
    {
        var s = new SharedSettings()
            .Overlay("""{"Port":41999,"BearerToken":"from-plugin"}""", "plugin")
            .Overlay("{ // comments are fine\n \"Port\": 42001, \"BearerToken\": \"from-provision\", \"BindMode\": \"LoopbackAndTailnet\", }", "provision");
        Assert.Equal((42001, "from-provision", 1), (s.Port, s.BearerToken, s.BindMode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"Port":0,"Path":5,"BindMode":9,"ClientTokens":"x"}""")]
    public void UnusableDocumentsChangeNothing(string json)
    {
        var s = new SharedSettings().Overlay(json, "x");
        Assert.Equal((41800, "/mcp", 0), (s.Port, s.Path, s.BindMode));
        Assert.Empty(s.ClientTokens);
    }

    [Fact]
    public void ProvisionPathFollowsThePluginRules()
    {
        Assert.Equal("/etc/p.json", SharedSettings.ProvisionPath(k => k == "XIVMCP_PROVISION" ? "/etc/p.json" : null, "/home/u"));
        Assert.Equal("/cfg/xiv-mcp/provision.json", SharedSettings.ProvisionPath(k => k == "XDG_CONFIG_HOME" ? "/cfg" : null, "/home/u").Replace('\\', '/'));
        Assert.Equal("/home/u/.config/xiv-mcp/provision.json", SharedSettings.ProvisionPath(_ => null, "/home/u").Replace('\\', '/'));
    }
}

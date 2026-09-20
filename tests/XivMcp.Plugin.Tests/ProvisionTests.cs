using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XivMcp.Plugin.Services;
using Xunit;

namespace XivMcp.Plugin.Tests;

/// <summary>
/// The optional provisioning file: path resolution, parsing, the read-only overlay over the saved
/// configuration, and the rule that a malformed file keeps the last good values instead of widening the
/// bind. All addresses are documentation ranges; the tokens are obvious placeholders.
/// </summary>
public sealed class ProvisionTests
{
    private static readonly JsonSerializerSettings DalamudSettings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };

    private static Func<string, string?> Env(params (string Key, string Value)[] entries) =>
        key => entries.FirstOrDefault(e => e.Key == key).Value;

    // ---- where the file lives --------------------------------------------------------------------

    [Fact]
    public void TheEnvironmentVariableWinsOverEverythingElse()
    {
        var env = Env((ProvisionFile.PathVariable, "/etc/xiv-mcp/fleet.json"), ("XDG_CONFIG_HOME", "/home/p/.config"), ("HOME", "/home/p"));

        Assert.Equal("/etc/xiv-mcp/fleet.json", ProvisionFile.ResolvePath(env, windows: false));
        Assert.Equal(@"Z:\etc\xiv-mcp\fleet.json", ProvisionFile.ResolvePath(env, windows: true));
    }

    [Fact]
    public void XdgConfigHomeIsUsedNext()
    {
        var env = Env(("XDG_CONFIG_HOME", "/home/p/.config"), ("HOME", "/home/p"));

        Assert.Equal("/home/p/.config/xiv-mcp/provision.json", ProvisionFile.ResolvePath(env, windows: false));
    }

    [Fact]
    public void HomeIsTheLastFallbackAndIsMappedOntoTheWineDrive()
    {
        var env = Env(("HOME", "/home/p"));

        Assert.Equal("/home/p/.config/xiv-mcp/provision.json", ProvisionFile.ResolvePath(env, windows: false));
        Assert.Equal(@"Z:\home\p\.config\xiv-mcp\provision.json", ProvisionFile.ResolvePath(env, windows: true));
    }

    [Fact]
    public void WithNoEnvironmentAtAllThereIsNoProvisioningFile() =>
        Assert.Null(ProvisionFile.ResolvePath(Env(), windows: false));

    [Fact]
    public void AWindowsPathInTheVariableIsLeftAlone() =>
        Assert.Equal(@"C:\ProgramData\xiv-mcp.json", ProvisionFile.ResolvePath(Env((ProvisionFile.PathVariable, @"C:\ProgramData\xiv-mcp.json")), windows: true));

    // ---- parsing ---------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheKeysPresentInTheFileAreProvisioned()
    {
        var document = ProvisionFile.Parse("""{"BindMode": "LoopbackAndTailnet", "Port": 41900}""", "/tmp/p.json");

        Assert.Equal(BindMode.LoopbackAndTailnet, document.BindMode);
        Assert.Equal(41900, document.Port);
        Assert.True(document.Has(nameof(ProvisionDocument.BindMode)));
        Assert.True(document.Has(nameof(ProvisionDocument.Port)));
        Assert.False(document.Has(nameof(ProvisionDocument.BearerToken)));
        Assert.Equal("/tmp/p.json", document.Source);
    }

    [Fact]
    public void TheBindModeIsAcceptedByNameOrNumberAndIsCaseInsensitive()
    {
        Assert.Equal(BindMode.TailnetOnly, ProvisionFile.Parse("""{"bindmode":"tailnetonly"}""", "p").BindMode);
        Assert.Equal(BindMode.Custom, ProvisionFile.Parse("""{"BindMode":3}""", "p").BindMode);
    }

    [Fact]
    public void EveryProvisionableSettingRoundTrips()
    {
        var document = ProvisionFile.Parse(
            """
            {
              "Enabled": true,
              "BindMode": "Custom",
              "CustomHost": "198.51.100.7",
              "Port": 41801,
              "Path": "/mcp/",
              "RequireToken": true,
              "BearerToken": "EXAMPLE_TOKEN_NOT_A_REAL_ONE",
              "AllowedOrigins": ["http://192.0.2.9:3000"],
              "CallTimeoutSeconds": 45,
              "ConfirmTimeoutSeconds": 30,
              "DisabledCategories": ["chat"]
            }
            """,
            "p");

        Assert.True(document.Enabled);
        Assert.Equal(BindMode.Custom, document.BindMode);
        Assert.Equal("198.51.100.7", document.CustomHost);
        Assert.Equal(41801, document.Port);
        Assert.Equal("/mcp", document.Path);
        Assert.True(document.RequireToken);
        Assert.Equal("EXAMPLE_TOKEN_NOT_A_REAL_ONE", document.BearerToken);
        Assert.Equal(["http://192.0.2.9:3000"], document.AllowedOrigins);
        Assert.Equal(45, document.CallTimeoutSeconds);
        Assert.Equal(30, document.ConfirmTimeoutSeconds);
        Assert.Equal(["chat"], document.DisabledCategories);
        Assert.Equal(11, document.Present.Count);
    }

    [Fact]
    public void UnknownKeysAreIgnoredSoANewerFileStillLoads()
    {
        var document = ProvisionFile.Parse("""{"Port": 41900, "SomethingFromTheFuture": {"a": 1}}""", "p");

        Assert.Equal(41900, document.Port);
        Assert.Single(document.Present);
    }

    [Fact]
    public void ANullValueCountsAsAbsent() => Assert.True(ProvisionFile.Parse("""{"Port": null}""", "p").IsEmpty);

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"Port": "41900"}""")]
    [InlineData("""{"Port": 0}""")]
    [InlineData("""{"Port": 70000}""")]
    [InlineData("""{"BindMode": "Everything"}""")]
    [InlineData("""{"Enabled": "yes"}""")]
    [InlineData("""{"BearerToken": ""}""")]
    [InlineData("""{"AllowedOrigins": "http://example.com"}""")]
    [InlineData("""{"CallTimeoutSeconds": 1}""")]
    public void ABadValueIsRejectedWithAMessageRatherThanGuessedAt(string json) =>
        Assert.Throws<ArgumentException>(() => ProvisionFile.Parse(json, "p"));

    // ---- the overlay -----------------------------------------------------------------------------

    [Fact]
    public void ProvisionedValuesOverrideTheSavedOnes()
    {
        var config = new Configuration { BindMode = BindMode.Loopback, Port = 41800, RequireToken = false };
        config.Normalize();

        config.Provision = ProvisionFile.Parse("""{"BindMode":"TailnetOnly","Port":41900,"RequireToken":true}""", "p");

        Assert.Equal(BindMode.TailnetOnly, config.BindMode);
        Assert.Equal(41900, config.Port);
        Assert.True(config.RequireToken);
        Assert.True(config.IsProvisioned(nameof(ProvisionDocument.Port)));
        Assert.False(config.IsProvisioned(nameof(ProvisionDocument.CustomHost)));
    }

    [Fact]
    public void RemovingTheOverlayRestoresExactlyWhatTheOwnerHadConfigured()
    {
        var config = new Configuration { BindMode = BindMode.Loopback, Port = 41800 };
        config.Normalize();
        config.Provision = ProvisionFile.Parse("""{"BindMode":"TailnetOnly","Port":41900}""", "p");
        Assert.Equal(41900, config.Port);

        config.Provision = null;

        Assert.Equal(BindMode.Loopback, config.BindMode);
        Assert.Equal(41800, config.Port);
    }

    [Fact]
    public void ProvisionedValuesAreNeverWrittenIntoThePluginsOwnConfigFile()
    {
        var config = new Configuration { BindMode = BindMode.Loopback, Port = 41800 };
        config.Normalize();
        var ownToken = config.BearerToken;
        config.Provision = ProvisionFile.Parse("""{"BindMode":"TailnetOnly","Port":41900,"BearerToken":"EXAMPLE_FLEET_TOKEN"}""", "p");

        var saved = JObject.Parse(JsonConvert.SerializeObject(config, DalamudSettings));

        Assert.Equal(41800, saved["Port"]!.Value<int>());
        Assert.Equal((int)BindMode.Loopback, saved["BindMode"]!.Value<int>());
        Assert.Equal(ownToken, saved["BearerToken"]!.Value<string>());
        Assert.DoesNotContain("EXAMPLE_FLEET_TOKEN", saved.ToString());
        Assert.Null(saved["Provision"]);
    }

    [Fact]
    public void NormalizeClampsTheSavedValuesNotTheProvisionedOnes()
    {
        var config = new Configuration { Port = 41800, CallTimeoutSeconds = 30 };
        config.Normalize();
        config.Provision = ProvisionFile.Parse("""{"CallTimeoutSeconds":600}""", "p");

        config.Normalize();

        Assert.Equal(600, config.CallTimeoutSeconds);
        var saved = JObject.Parse(JsonConvert.SerializeObject(config, DalamudSettings));
        Assert.Equal(30, saved["CallTimeoutSeconds"]!.Value<int>());
    }

    // ---- the store -------------------------------------------------------------------------------

    private static ProvisionStore Store(Func<string, ProvisionDocument?> reader, Func<string, (bool, DateTime, long)> stat) =>
        new("/tmp/provision.json", (_, _) => { }, TimeSpan.FromMilliseconds(1), reader, stat);

    [Fact]
    public void TheStoreAppliesTheDocumentAndReportsTheChange()
    {
        var config = new Configuration();
        var reads = 0;
        var stamp = 0;
        var store = Store(
            path =>
            {
                reads++;
                return ProvisionFile.Parse("""{"BindMode":"TailnetOnly"}""", path);
            },
            _ => (true, new DateTime(2026, 1, 1).AddMinutes(stamp), 10));

        Assert.True(store.Poll(config, force: true));
        Assert.Equal(BindMode.TailnetOnly, config.BindMode);
        Assert.Equal(1, reads);

        // Unchanged file: not re-read at all.
        Assert.False(store.Poll(config, force: true));
        Assert.Equal(1, reads);

        // Same contents but a new timestamp: re-read, and the effective settings did not change.
        stamp = 1;
        Assert.False(store.Poll(config, force: true));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void AMalformedFileKeepsTheLastGoodValuesAndSurfacesTheError()
    {
        var config = new Configuration();
        var broken = false;
        var stamp = 0;
        var store = Store(
            _ => broken ? throw new ArgumentException("Provision file is not valid JSON: boom.") : ProvisionFile.Parse("""{"BindMode":"TailnetOnly","Port":41900}""", "/tmp/provision.json"),
            _ => (true, new DateTime(2026, 1, 1).AddMinutes(stamp), 10));

        store.Poll(config, force: true);
        Assert.Equal(BindMode.TailnetOnly, config.BindMode);
        Assert.Null(store.Error);

        broken = true;
        stamp = 1;
        Assert.False(store.Poll(config, force: true));

        // Still provisioned: a typo must never quietly hand the bind back to a different mode.
        Assert.Equal(BindMode.TailnetOnly, config.BindMode);
        Assert.Equal(41900, config.Port);
        Assert.NotNull(store.Error);
        Assert.Contains("boom", store.Error!);
    }

    [Fact]
    public void AFileThatGoesAwayHandsTheSettingsBack()
    {
        var config = new Configuration { Port = 41800 };
        config.Normalize();
        var exists = true;
        var store = Store(
            path => exists ? ProvisionFile.Parse("""{"Port":41900}""", path) : null,
            _ => exists ? (true, new DateTime(2026, 1, 1), 10) : (false, default, 0));

        store.Poll(config, force: true);
        Assert.Equal(41900, config.Port);

        exists = false;
        Assert.True(store.Poll(config, force: true));
        Assert.Equal(41800, config.Port);
        Assert.Null(store.Current);
    }

    [Fact]
    public void AnEmptyDocumentIsTreatedAsNoProvisioning()
    {
        var config = new Configuration();
        var store = Store(path => ProvisionFile.Parse("{}", path), _ => (true, new DateTime(2026, 1, 1), 2));

        Assert.False(store.Poll(config, force: true));
        Assert.Null(store.Current);
        Assert.Null(config.Provision);
    }

    [Fact]
    public void ABrokenFileIsRetriedEvenThoughItsTimestampDidNotChangeAgain()
    {
        var config = new Configuration();
        var reads = 0;
        var store = Store(
            _ =>
            {
                reads++;
                throw new ArgumentException("Provision file is not valid JSON: boom.");
            },
            _ => (true, new DateTime(2026, 1, 1), 10));

        store.Poll(config, force: true);
        store.Poll(config, force: true);

        Assert.Equal(2, reads);
        Assert.NotNull(store.Error);
    }

    [Fact]
    public void WithNoPathThereIsNothingToPoll()
    {
        var config = new Configuration();
        var store = new ProvisionStore(null, (_, _) => { });

        Assert.False(store.Poll(config, force: true));
        Assert.Null(store.Current);
        Assert.True(store.HasChecked);
    }
}

using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XivMcp.Core.Net;
using XivMcp.Plugin.Services;
using Xunit;

namespace XivMcp.Plugin.Tests;

/// <summary>
/// Bind modes: the mode → addresses mapping, the v2 → v3 migration of the old <c>Host</c> string, and the
/// rule that anything reachable off this machine must require the bearer token. Every address here is a
/// documentation range (RFC 5737) or a synthetic tailnet address.
/// </summary>
public sealed class BindModeTests
{
    private static readonly JsonSerializerSettings DalamudSettings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };

    private static TailnetAddress Tailnet(string address = "100.90.1.2", string? magicDns = "box.example-tailnet.ts.net") =>
        new(IPAddress.Parse(address), "tailscale0", magicDns, TailnetSource.Interface);

    // ---- mode -> addresses ---------------------------------------------------------------------

    [Fact]
    public void LoopbackModeBindsLoopbackOnly()
    {
        var plan = BindPlanner.Resolve(BindMode.Loopback, "", Tailnet());

        Assert.Equal(["127.0.0.1"], plan.Hosts);
        Assert.Equal("127.0.0.1", plan.PreferredHost);
        Assert.Null(plan.TailnetHost);
        Assert.False(plan.RequiresToken);
        Assert.True(plan.LoopbackOnly);
        Assert.Null(plan.Notice);
    }

    [Fact]
    public void LoopbackAndTailnetBindsBothWithTheTailnetAddressPreferred()
    {
        var plan = BindPlanner.Resolve(BindMode.LoopbackAndTailnet, "", Tailnet());

        Assert.Equal(["127.0.0.1", "100.90.1.2"], plan.Hosts);
        Assert.Equal("100.90.1.2", plan.PreferredHost);
        Assert.Equal("127.0.0.1", plan.LoopbackHost);
        Assert.Equal("100.90.1.2", plan.TailnetHost);
        Assert.Equal("box.example-tailnet.ts.net", plan.MagicDnsName);
        Assert.True(plan.RequiresToken);
        Assert.Contains("100.90.1.2", plan.AllowedHostNames);
        Assert.Contains("box.example-tailnet.ts.net", plan.AllowedHostNames);
    }

    [Fact]
    public void TailnetOnlyBindsTheTailnetAddressAlone()
    {
        var plan = BindPlanner.Resolve(BindMode.TailnetOnly, "", Tailnet());

        Assert.Equal(["100.90.1.2"], plan.Hosts);
        Assert.Null(plan.LoopbackHost);
        Assert.True(plan.RequiresToken);
    }

    [Theory]
    [InlineData(BindMode.LoopbackAndTailnet)]
    [InlineData(BindMode.TailnetOnly)]
    public void ATailnetModeWithoutATailnetFallsBackToLoopbackAndSaysSo(BindMode mode)
    {
        var plan = BindPlanner.Resolve(mode, "", null);

        Assert.Equal(["127.0.0.1"], plan.Hosts);
        Assert.False(plan.RequiresToken);
        Assert.NotNull(plan.Notice);
        Assert.Contains("Tailscale", plan.Notice!);
    }

    [Fact]
    public void AnIpv6TailnetAddressIsBracketedInTheHostsButNotInTheHeaderNames()
    {
        var plan = BindPlanner.Resolve(BindMode.TailnetOnly, "", Tailnet("fd7a:115c:a1e0::9", null));

        Assert.Equal(["[fd7a:115c:a1e0::9]"], plan.Hosts);
        Assert.Contains("fd7a:115c:a1e0::9", plan.AllowedHostNames);
        Assert.Equal("http://[fd7a:115c:a1e0::9]:41800/mcp", BindPlanner.Endpoints(plan, 41800, "/mcp")[0]);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", false)]
    [InlineData("::1", false)]
    [InlineData("198.51.100.7", true)]
    [InlineData("100.90.1.2", true)]
    public void CustomModeBindsWhatWasTypedAndDecidesExposureFromIt(string host, bool requiresToken)
    {
        var plan = BindPlanner.Resolve(BindMode.Custom, host, null);

        Assert.Equal([host], plan.Hosts);
        Assert.Equal(requiresToken, plan.RequiresToken);
    }

    [Fact]
    public void AnEmptyCustomAddressFallsBackToLoopback()
    {
        var plan = BindPlanner.Resolve(BindMode.Custom, "   ", null);

        Assert.Equal(["127.0.0.1"], plan.Hosts);
        Assert.False(plan.RequiresToken);
    }

    [Fact]
    public void EndpointsFollowTheBindOrderAndThePath()
    {
        var plan = BindPlanner.Resolve(BindMode.LoopbackAndTailnet, "", Tailnet());

        Assert.Equal(
            ["http://127.0.0.1:41800/mcp", "http://100.90.1.2:41800/mcp"],
            BindPlanner.Endpoints(plan, 41800, "/mcp"));
        Assert.Equal("http://100.90.1.2:9000/games", BindPlanner.Endpoint("100.90.1.2", 9000, "games/"));
    }

    // ---- migration from the schema-2 Host string -------------------------------------------------

    private const string V2Loopback = """
        {"$type":"XivMcp.Plugin.Configuration, XivMcp","Version":2,"Enabled":true,"Host":"127.0.0.1","Port":41800,
         "BearerToken":"KEEP_THIS_TOKEN_abcdefghijklmnopqrstuvwxyz0","RequireToken":true,"AllowedOrigins":[],"CallTimeoutSeconds":30,
         "AllowRead":true,"AllowUi":true,"AllowAction":true,"AllowChat":false,"ConfirmActions":true,"ConfirmTimeoutSeconds":20}
        """;

    private const string V2Custom = """
        {"$type":"XivMcp.Plugin.Configuration, XivMcp","Version":2,"Enabled":true,"Host":"198.51.100.7","Port":41800,
         "BearerToken":"KEEP_THIS_TOKEN_abcdefghijklmnopqrstuvwxyz0","RequireToken":true,"AllowedOrigins":[],"CallTimeoutSeconds":30,
         "AllowRead":true,"AllowUi":true,"AllowAction":true,"AllowChat":false,"ConfirmActions":true,"ConfirmTimeoutSeconds":20}
        """;

    [Fact]
    public void ALoopbackHostMigratesToTheLoopbackMode()
    {
        var config = JsonConvert.DeserializeObject<Configuration>(V2Loopback, DalamudSettings)!;

        Assert.True(config.Normalize());
        Assert.Equal(3, config.Version);
        Assert.Equal(BindMode.Loopback, config.BindMode);
        Assert.Equal("KEEP_THIS_TOKEN_abcdefghijklmnopqrstuvwxyz0", config.BearerToken);
        Assert.Equal("/mcp", config.Path);
        Assert.Equal("http://127.0.0.1:41800/mcp", config.EndpointUrl);
    }

    [Fact]
    public void AnyOtherHostMigratesToCustomWithThatAddressKept()
    {
        var config = JsonConvert.DeserializeObject<Configuration>(V2Custom, DalamudSettings)!;

        Assert.True(config.Normalize());
        Assert.Equal(BindMode.Custom, config.BindMode);
        Assert.Equal("198.51.100.7", config.CustomHost);
        Assert.False(config.HostIsLoopback);
        Assert.Equal(["198.51.100.7"], BindPlanner.Resolve(config.BindMode, config.CustomHost, null).Hosts);
    }

    [Fact]
    public void MigrationRunsOnceAndKeepsTheLegacyHostFieldInStep()
    {
        var config = JsonConvert.DeserializeObject<Configuration>(V2Custom, DalamudSettings)!;
        config.Normalize();

        // Round-trip through Dalamud's serializer: the mode survives and "Host" still names the address,
        // so an older build (which only knows Host) keeps binding the same thing.
        var saved = JObject.Parse(JsonConvert.SerializeObject(config, DalamudSettings));
        Assert.Equal(3, saved["Version"]!.Value<int>());
        Assert.Equal("198.51.100.7", saved["Host"]!.Value<string>());
        Assert.Equal((int)BindMode.Custom, saved["BindMode"]!.Value<int>());

        var reloaded = JsonConvert.DeserializeObject<Configuration>(saved.ToString(), DalamudSettings)!;
        Assert.False(reloaded.Normalize());
        Assert.Equal(BindMode.Custom, reloaded.BindMode);
        Assert.Equal("198.51.100.7", reloaded.CustomHost);
    }

    [Fact]
    public void ASwitchBackToLoopbackResetsTheLegacyHostField()
    {
        var config = JsonConvert.DeserializeObject<Configuration>(V2Custom, DalamudSettings)!;
        config.Normalize();
        config.BindMode = BindMode.LoopbackAndTailnet;
        config.Normalize();

        Assert.Equal("127.0.0.1", config.Host);
    }

    [Fact]
    public void AnUnknownBindModeFallsBackToLoopback()
    {
        var config = new Configuration { BindMode = (BindMode)99 };

        Assert.True(config.Normalize());
        Assert.Equal(BindMode.Loopback, config.BindMode);
    }

    [Theory]
    [InlineData("", "/mcp")]
    [InlineData("mcp", "/mcp")]
    [InlineData("/games/", "/games")]
    [InlineData("/", "/")]
    public void ThePathIsNormalised(string input, string expected)
    {
        var config = new Configuration { Path = input };
        config.Normalize();

        Assert.Equal(expected, config.Path);
    }

    // ---- "token required off loopback" -----------------------------------------------------------

    [Theory]
    [InlineData(BindMode.Loopback, null, false)]
    [InlineData(BindMode.LoopbackAndTailnet, "100.90.1.2", true)]
    [InlineData(BindMode.TailnetOnly, "100.90.1.2", true)]
    [InlineData(BindMode.LoopbackAndTailnet, null, false)] // fell back to loopback
    public void ABindThatLeavesThisMachineRequiresTheToken(BindMode mode, string? tailnet, bool expected)
    {
        var plan = BindPlanner.Resolve(mode, "", tailnet is null ? null : Tailnet(tailnet));

        Assert.Equal(expected, plan.RequiresToken);
        Assert.Equal(!expected, plan.LoopbackOnly);
    }

    [Fact]
    public void ACustomLoopbackAddressDoesNotRequireTheToken() =>
        Assert.False(BindPlanner.Resolve(BindMode.Custom, "127.0.0.2", null).RequiresToken);

    [Fact]
    public void TheHostHeaderNamesForAPlanCoverEveryBoundAddress()
    {
        var names = BindPlanner.AllowedHostHeaders(BindPlanner.Resolve(BindMode.LoopbackAndTailnet, "", Tailnet("fd7a:115c:a1e0::9")));

        Assert.Contains("127.0.0.1", names);
        Assert.Contains("fd7a:115c:a1e0::9", names);
        Assert.DoesNotContain("[fd7a:115c:a1e0::9]", names);
    }
}

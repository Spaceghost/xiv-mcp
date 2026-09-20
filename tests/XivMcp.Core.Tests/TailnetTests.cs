using System.Net;
using XivMcp.Core;
using XivMcp.Core.Net;
using Xunit;

namespace XivMcp.Core.Tests;

/// <summary>
/// Tailnet detection rules and the DNS-rebinding host check. Addresses here are documentation ranges
/// (RFC 5737 / RFC 3849) or synthetic tailnet addresses; none of them belong to a real machine.
/// </summary>
public sealed class TailnetTests
{
    [Theory]
    [InlineData("100.64.0.0", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.90.12.34", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)] // just below the range
    [InlineData("100.128.0.0", false)] // just above the range
    [InlineData("100.0.0.1", false)]
    [InlineData("101.64.0.1", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.7", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fd7a:115c:a1e0:ab12:3456:7890:abcd:ef12", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("fd00::1", false)]
    public void CgnatAndUlaRangesAreRecognised(string text, bool expected) =>
        Assert.Equal(expected, Tailnet.IsTailnetAddress(IPAddress.Parse(text)));

    [Fact]
    public void NullAndNonIpFamiliesAreNotTailnet() => Assert.False(Tailnet.IsTailnetAddress(null));

    [Fact]
    public void Ipv4MappedTailnetAddressIsRecognised() =>
        Assert.True(Tailnet.IsTailnetAddress(IPAddress.Parse("100.90.1.2").MapToIPv6()));

    // ---- adapter selection ---------------------------------------------------------------------

    private static NetAdapter Adapter(string name, bool up, params string[] addresses) =>
        new(name, name, up, addresses.Select(IPAddress.Parse).ToArray());

    [Fact]
    public void PicksTheCgnatAddressOutOfAMixedAdapterList()
    {
        var found = Tailnet.Select(
        [
            Adapter("lo", true, "127.0.0.1", "::1"),
            Adapter("enp5s0", true, "192.0.2.15"),
            Adapter("wlo1", true, "198.51.100.23", "2001:db8::5"),
            Adapter("tailscale0", true, "fd7a:115c:a1e0::763a:1111", "100.90.1.2"),
        ]);

        Assert.NotNull(found);
        Assert.Equal(IPAddress.Parse("100.90.1.2"), found!.Address);
        Assert.Equal("tailscale0", found.AdapterName);
        Assert.Equal(TailnetSource.Interface, found.Source);
    }

    [Fact]
    public void FindsTheAddressEvenWhenTheAdapterIsNotCalledTailscale()
    {
        // Under Wine the adapter keeps its Linux name, but a Windows host calls it something else
        // entirely; the address range is what actually decides.
        var found = Tailnet.Select([Adapter("{4E657444-0000-0000-0000-000000000004}", true, "100.101.102.103")]);

        Assert.NotNull(found);
        Assert.Equal(IPAddress.Parse("100.101.102.103"), found!.Address);
    }

    [Fact]
    public void PrefersTheAdapterThatNamesTailscaleWhenTwoCarryTailnetAddresses()
    {
        var found = Tailnet.Select(
        [
            Adapter("veth0", true, "100.120.0.9"),
            Adapter("tailscale0", true, "100.64.0.9"),
        ]);

        Assert.Equal("tailscale0", found!.AdapterName);
    }

    [Fact]
    public void PrefersIpv4OverIpv6OnTheSameAdapter()
    {
        var found = Tailnet.Select([Adapter("ts0", true, "fd7a:115c:a1e0::9", "100.77.0.9")]);

        Assert.Equal(IPAddress.Parse("100.77.0.9"), found!.Address);
    }

    [Fact]
    public void AnAdapterThatIsDownStillCountsButLosesToOneThatIsUp()
    {
        Assert.Equal("ts1", Tailnet.Select([Adapter("ts0", false, "100.64.0.1"), Adapter("ts1", true, "100.64.0.2")])!.AdapterName);
        Assert.Equal("ts0", Tailnet.Select([Adapter("ts0", false, "100.64.0.1")])!.AdapterName);
    }

    [Fact]
    public void NoTailnetAddressReturnsNull()
    {
        Assert.Null(Tailnet.Select([Adapter("lo", true, "127.0.0.1"), Adapter("eth0", true, "203.0.113.4")]));
        Assert.Null(Tailnet.Select([]));
        Assert.Null(Tailnet.Select(null));
    }

    [Theory]
    [InlineData("tailscale0", true)]
    [InlineData("Tailscale Tunnel", true)]
    [InlineData("ts0", true)]
    [InlineData("ts12", true)]
    [InlineData("tsomething", false)]
    [InlineData("enp5s0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AdapterNamesAreClassified(string? name, bool expected) =>
        Assert.Equal(expected, Tailnet.LooksLikeTailscaleAdapter(name, null));

    // ---- MagicDNS ------------------------------------------------------------------------------

    [Fact]
    public void MagicDnsNameIsReadFromStatusJsonWithoutTheTrailingDot()
    {
        var json = """{"Self":{"HostName":"box","DNSName":"box.example-tailnet.ts.net."},"Peer":{}}""";

        Assert.Equal("box.example-tailnet.ts.net", Tailnet.ParseMagicDnsName(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"Self":{}}""")]
    [InlineData("""{"Self":{"DNSName":""}}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData(null)]
    public void MagicDnsNameIsNullWhenAbsentOrUnparseable(string? json) => Assert.Null(Tailnet.ParseMagicDnsName(json));

    // ---- DNS-rebinding defence -------------------------------------------------------------------

    private static IReadOnlySet<string> Allowed(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void LoopbackBindAcceptsOnlyLoopbackHostNames()
    {
        var allowed = Allowed("127.0.0.1");

        Assert.True(McpServer.IsAllowedHostHeader("127.0.0.1:41800", allowed));
        Assert.True(McpServer.IsAllowedHostHeader("localhost:41800", allowed));
        Assert.True(McpServer.IsAllowedHostHeader("[::1]:41800", allowed));
        Assert.False(McpServer.IsAllowedHostHeader("evil.example.com", allowed));
        Assert.False(McpServer.IsAllowedHostHeader("100.90.1.2:41800", allowed));
    }

    [Fact]
    public void TailnetBindAlsoAcceptsTheTailnetAddressAndMagicDnsName()
    {
        var allowed = Allowed("127.0.0.1", "100.90.1.2", "box.example-tailnet.ts.net");

        Assert.True(McpServer.IsAllowedHostHeader("127.0.0.1:41800", allowed));
        Assert.True(McpServer.IsAllowedHostHeader("100.90.1.2:41800", allowed));
        Assert.True(McpServer.IsAllowedHostHeader("box.example-tailnet.ts.net:41800", allowed));
        Assert.True(McpServer.IsAllowedHostHeader("BOX.EXAMPLE-TAILNET.TS.NET", allowed));

        // A name that merely resolves to the tailnet address is still a rebinding attempt.
        Assert.False(McpServer.IsAllowedHostHeader("attacker.example.com:41800", allowed));
        Assert.False(McpServer.IsAllowedHostHeader("100.90.1.3:41800", allowed));
    }

    [Fact]
    public void TailnetOnlyBindDoesNotStopAcceptingLoopbackNames()
    {
        // Loopback is always allowed as a Host header even when it is not bound: nothing off-machine
        // can reach a loopback name anyway, and the check is about the header, not the socket.
        var allowed = Allowed("100.90.1.2");

        Assert.True(McpServer.IsAllowedHostHeader("localhost:41800", allowed));
        Assert.True(McpServer.IsAllowedHostHeader("100.90.1.2:41800", allowed));
        Assert.False(McpServer.IsAllowedHostHeader("example.com", allowed));
    }

    [Theory]
    [InlineData("127.0.0.1:41800", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("[fd7a:115c:a1e0::9]:41800", "fd7a:115c:a1e0::9")]
    [InlineData("[fd7a:115c:a1e0::9]", "fd7a:115c:a1e0::9")]
    [InlineData("  example.com:8080  ", "example.com")]
    public void HostHeaderPortIsStripped(string header, string expected) => Assert.Equal(expected, McpServer.StripPort(header));
}

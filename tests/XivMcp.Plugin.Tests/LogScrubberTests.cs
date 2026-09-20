using XivMcp.Plugin.Providers.DalamudInfo;

namespace XivMcp.Plugin.Tests;

/// <summary>Everything read_plugin_log returns goes through LogScrubber.Scrub; these are the promises its description makes.</summary>
public class LogScrubberTests
{
    [Theory]
    [InlineData("GET /x Authorization: Bearer abcdef1234567890XYZ done", "Authorization: <redacted:authorization> done")]
    [InlineData("authorization=Basic dXNlcjpwYXNz", "authorization=<redacted:authorization>")]
    [InlineData("{\"Authorization\":\"Bearer abc.def.ghi\"}", "\"Authorization\":\"<redacted:authorization>\"")]
    [InlineData("Proxy-Authorization: Digest qwertyuiop", "Proxy-Authorization: <redacted:authorization>")]
    [InlineData("sending bearer abcdef1234567890 now", "bearer <redacted:token> now")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl end", "jwt <redacted:token> end")] // gitleaks:allow - a made-up JWT the scrubber has to remove, not a credential
    public void RemovesAuthorizationAndBearerValues(string input, string expected)
    {
        var scrubbed = LogScrubber.Scrub(input);
        Assert.Contains(expected, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef1234567890", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("dXNlcjpwYXNz", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("qwertyuiop", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.test/api?token=abc123&page=2", "token=<redacted:secret>&page=2")]
    [InlineData("https://example.test/api?api_key=abc123", "api_key=<redacted:secret>")]
    [InlineData("url?key=abc123;next", "key=<redacted:secret>;next")]
    [InlineData("password=hunter2 user=x", "password=<redacted:secret> user=x")]
    [InlineData("{\"accessToken\":\"abc123\",\"n\":1}", "\"accessToken\":\"<redacted:secret>\",\"n\":1")]
    [InlineData("{ \"client_secret\" : \"abc123\" }", "\"client_secret\" : \"<redacted:secret>\"")]
    [InlineData("Password: abc123", "Password: <redacted:secret>")]
    [InlineData("X-Api-Key: abc123", "X-Api-Key: <redacted:secret>")]
    public void RemovesSecretAssignments(string input, string expected)
    {
        var scrubbed = LogScrubber.Scrub(input);
        Assert.Contains(expected, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovesCredentialsInUrls()
    {
        var scrubbed = LogScrubber.Scrub("fetching https://alice:s3cr3t@example.test/repo.json failed");
        Assert.Equal("fetching https://<redacted:credentials>@example.test/repo.json failed", scrubbed);
    }

    [Theory]
    [InlineData("hash 0123456789abcdef0123456789abcdef ok", "hash <redacted:hex> ok")]
    [InlineData("sha 0123456789ABCDEF0123456789ABCDEF01234567", "sha <redacted:hex>")]
    [InlineData("blob QWxhZGRpbjpvcGVuIHNlc2FtZTEyMzQ1Njc4OTBhYmNkZWZnaGlqa2xtbm9w== end", "blob <redacted:base64> end")]
    public void RemovesLongHexAndBase64(string input, string expected) => Assert.Equal(expected, LogScrubber.Scrub(input));

    [Theory]
    [InlineData("guid 3f2504e0-4f89-11d3-9a0c-0305e82c3301 stays")]
    [InlineData("short hex deadbeef stays")]
    [InlineData("path installedPlugins/SomePlugin/cache/textures/icons/large/folder/another/folder stays")]
    [InlineData("type Some.Very.Long.Namespace.With.Many.Parts.AndAClassNameThatGoesOnAndOn stays")]
    [InlineData("AVeryLongIdentifierWithoutAnyDigitsThatIsNotASecretAtAllReally stays")]
    public void LeavesOrdinaryTextAlone(string input) => Assert.Equal(input, LogScrubber.Scrub(input));

    [Theory]
    [InlineData("mail alice@example.test now", "mail <redacted:email> now")]
    [InlineData("<first.last+tag@mail.example.test>", "<<redacted:email>>")]
    public void RemovesEmailAddresses(string input, string expected) => Assert.Equal(expected, LogScrubber.Scrub(input));

    [Theory]
    [InlineData("tell from Warrior Light@Balmung")]
    [InlineData("Y'shtola Rhul joined the party")]
    public void KeepsCharacterNames(string input) => Assert.Equal(input, LogScrubber.Scrub(input));

    [Theory]
    [InlineData("connect 192.0.2.17:8080 failed", "connect <redacted:ipv4>:8080 failed")]
    [InlineData("listening on 127.0.0.1", "listening on <redacted:ipv4>")]
    [InlineData("peer 2001:db8:0:0:0:0:0:1 up", "peer <redacted:ipv6> up")]
    [InlineData("peer 2001:db8::1 up", "peer <redacted:ipv6> up")]
    [InlineData("bound [::1]:41800", "bound [<redacted:ipv6>]:41800")]
    [InlineData("link fe80::1%eth0 up", "link <redacted:ipv6> up")]
    [InlineData("mapped ::ffff:192.0.2.1 x", "mapped ::ffff:<redacted:ipv4> x")]
    public void RemovesIpAddresses(string input, string expected) => Assert.Equal(expected, LogScrubber.Scrub(input));

    [Theory]
    [InlineData("Dalamud v13.0.0.4 started")]
    [InlineData("Plugin version 1.2.3.4 loaded")]
    [InlineData("Version=1.2.3.4, Culture=neutral")]
    [InlineData("game 2025.08.01.0000.0000")]
    [InlineData("at 12:34:56.789 Foo::Bar ran in 1.5 ms")]
    [InlineData("Plugin.Handler::OnUpdate")]
    public void KeepsVersionsTimesAndScopeOperators(string input) => Assert.Equal(input, LogScrubber.Scrub(input));

    [Theory]
    [InlineData("/home/alice/.xlcore/dalamud.log", "~/.xlcore/dalamud.log")]
    [InlineData("/var/home/alice/.xlcore/x", "~/.xlcore/x")]
    [InlineData("/Users/alice/Library/x", "~/Library/x")]
    [InlineData(@"C:\Users\alice\AppData\Roaming\XIVLauncher", @"~\AppData\Roaming\XIVLauncher")]
    [InlineData(@"C:\Users\Alice Smith\AppData\x", @"~\AppData\x")]
    [InlineData(@"C:\\Users\\alice\\AppData", @"~\\AppData")]
    [InlineData("C:/Users/alice/AppData", "~/AppData")]
    [InlineData(@"Z:\home\alice\.xlcore\installedPlugins", @"~\.xlcore\installedPlugins")]
    [InlineData(@"Z:\var\home\alice\.xlcore", @"~\.xlcore")]
    [InlineData("loaded \"/home/alice\" ok", "loaded \"~\" ok")]
    public void ReplacesHomeDirectories(string input, string expected)
    {
        var scrubbed = LogScrubber.Scrub(input);
        Assert.Equal(expected, scrubbed);
        Assert.DoesNotContain("alice", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovesTheOsUserNameWhereverItAppears()
    {
        Assert.Equal("/run/user/1000 owned by <redacted:user>; prefix=/mnt/games/<redacted:user>-wine", LogScrubber.Scrub("/run/user/1000 owned by alice; prefix=/mnt/games/alice-wine", "alice"));
        Assert.Equal("malice stays", LogScrubber.Scrub("malice stays", "alice"));
        Assert.Equal("al stays", LogScrubber.Scrub("al stays", "al"));
    }

    [Fact]
    public void HandlesEmptyHugeAndRepeatedInput()
    {
        Assert.Equal("", LogScrubber.Scrub(null));
        Assert.Equal("", LogScrubber.Scrub(""));

        var huge = new string('a', 50_000) + " token=abc123";
        var scrubbed = LogScrubber.Scrub(huge);
        Assert.True(scrubbed.Length <= LogScrubber.MaxInput);
        Assert.DoesNotContain("abc123", scrubbed, StringComparison.Ordinal);

        var once = LogScrubber.Scrub("token=abc123 from 192.0.2.1 by alice@example.test in /home/alice/x");
        Assert.Equal(once, LogScrubber.Scrub(once));
    }

    [Fact]
    public void ScrubsEverySecretOnALine()
    {
        var scrubbed = LogScrubber.Scrub("[ERR] POST https://bob:pw@example.test/v1?key=K1&token=T1 from 198.51.100.7 Authorization: Bearer ZZZZZZZZZZZZ (/home/bob/x, bob@example.test)");
        foreach (var leak in new[] { "bob", "pw@", "K1", "T1", "198.51.100.7", "ZZZZZZZZZZZZ" })
            Assert.DoesNotContain(leak, scrubbed, StringComparison.Ordinal);
    }
}

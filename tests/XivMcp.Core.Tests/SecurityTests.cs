using System.Net;
using System.Text.Json.Nodes;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public class SecurityTests
{
    private static JsonObject DiscoverBody() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "server/discover",
        ["params"] = new JsonObject { ["_meta"] = TestServer.ModernMeta() },
    };

    [Fact]
    public async Task MissingTokenIs401WithChallenge()
    {
        await using var s = await TestServer.StartAsync();
        var request = s.ModernPost("server/discover");
        request.Headers.Authorization = null;
        s.Http.DefaultRequestHeaders.Authorization = null;
        using var response = await s.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer realm=\"xiv-mcp\"", response.Headers.WwwAuthenticate.ToString());
        var body = await TestServer.ReadJsonAsync(response);
        Assert.NotNull(body["error"]);
    }

    [Fact]
    public async Task WrongTokenIs401InvalidToken()
    {
        await using var s = await TestServer.StartAsync();
        s.Http.DefaultRequestHeaders.Authorization = new("Bearer", "nope");
        using var response = await s.Http.SendAsync(s.ModernPost("server/discover"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString());
        Assert.Contains(s.Server.GetActivity(5), a => a.Target == "401" && !a.Success);
    }

    [Theory]
    [InlineData("bearer test-token-123")]
    [InlineData("Bearer   test-token-123  ")]
    public async Task TokenSchemeIsCaseInsensitive(string header)
    {
        await using var s = await TestServer.StartAsync();
        s.Http.DefaultRequestHeaders.Authorization = null;
        var request = s.ModernPost("server/discover");
        request.Headers.TryAddWithoutValidation("Authorization", header);
        using var response = await s.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoTokenConfiguredAllowsAnonymous()
    {
        await using var s = await TestServer.StartAsync(token: null);
        s.Http.DefaultRequestHeaders.Authorization = null;
        using var response = await s.Http.SendAsync(s.ModernPost("server/discover"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    [InlineData("http://127.0.0.1:5173", HttpStatusCode.OK)]
    [InlineData("http://localhost:6274", HttpStatusCode.OK)]
    [InlineData("http://[::1]:3000", HttpStatusCode.OK)]
    [InlineData("https://app.example.com", HttpStatusCode.OK)]
    [InlineData("https://app.example.com:8443", HttpStatusCode.Forbidden)]
    public async Task OriginValidation(string origin, HttpStatusCode expected)
    {
        await using var s = await TestServer.StartAsync(o => o.AllowedOrigins.Add("https://app.example.com/"));
        var request = s.ModernPost("server/discover");
        request.Headers.Add("Origin", origin);
        using var response = await s.Http.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("Mcp-Session-Id", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        }
        else
        {
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }

    [Fact]
    public async Task OriginIsCheckedBeforeAuthentication()
    {
        await using var s = await TestServer.StartAsync();
        s.Http.DefaultRequestHeaders.Authorization = null;
        var request = s.ModernPost("server/discover");
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await s.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CorsPreflightNeedsNoToken()
    {
        await using var s = await TestServer.StartAsync();
        s.Http.DefaultRequestHeaders.Authorization = null;
        var request = new HttpRequestMessage(HttpMethod.Options, s.Endpoint);
        request.Headers.Add("Origin", "http://localhost:6274");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type, mcp-protocol-version, mcp-method");
        using var response = await s.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:6274", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Contains("mcp-method", response.Headers.GetValues("Access-Control-Allow-Headers").Single());

        var evil = new HttpRequestMessage(HttpMethod.Options, s.Endpoint);
        evil.Headers.Add("Origin", "http://evil.example");
        using var denied = await s.Http.SendAsync(evil);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task NonLoopbackHostHeaderIsRejectedOnLoopbackBind()
    {
        await using var s = await TestServer.StartAsync();
        var body = DiscoverBody().ToJsonString();
        var raw = $"POST /mcp HTTP/1.1\r\nHost: rebind.attacker.example:{s.Port}\r\n{s.AuthHeader}Content-Type: application/json\r\n" +
                  $"MCP-Protocol-Version: 2026-07-28\r\nMcp-Method: server/discover\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        Assert.StartsWith("HTTP/1.1 403", await s.RawAsync(raw));
        Assert.StartsWith("HTTP/1.1 200", await s.RawAsync(raw.Replace("rebind.attacker.example", "localhost")));
    }
}

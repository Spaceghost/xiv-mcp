using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Core.Http;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public class HttpTransportTests
{
    private const string Modern = TestServer.Modern;

    private static string ModernEcho(int id, string text) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "echo",
                ["arguments"] = new JsonObject { ["text"] = text },
                ["_meta"] = TestServer.ModernMeta(),
            },
        }.ToJsonString();

    private static string ModernHeaders(TestServer s) =>
        $"Host: 127.0.0.1:{s.Port}\r\n{s.AuthHeader}Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n" +
        $"MCP-Protocol-Version: {Modern}\r\nMcp-Method: tools/call\r\nMcp-Name: echo\r\n";

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public async Task KeepAliveServesSequentialRequestsOnOneConnection()
    {
        await using var s = await TestServer.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, s.Port);
        var stream = client.GetStream();
        for (var i = 0; i < 3; i++)
        {
            var body = ModernEcho(i, "hi" + i);
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"POST /mcp HTTP/1.1\r\n{ModernHeaders(s)}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}"));
            var response = await TestServer.ReadAllAsync(stream, TimeSpan.FromMilliseconds(500));
            Assert.StartsWith("HTTP/1.1 200 OK", response);
            Assert.Contains("\"text\":\"hi" + i + "\"", response);
        }
    }

    [Fact]
    public async Task PipelinedRequestsAreAnsweredInOrder()
    {
        await using var s = await TestServer.StartAsync();
        var sb = new StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            var body = ModernEcho(i, "p" + i);
            sb.Append(CultureInfo.InvariantCulture, $"POST /mcp HTTP/1.1\r\n{ModernHeaders(s)}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
        }

        var response = await s.RawAsync(sb.ToString(), TimeSpan.FromSeconds(1));
        Assert.Equal(3, CountOccurrences(response, "HTTP/1.1 200 OK"));
        Assert.True(response.IndexOf("\"p0\"", StringComparison.Ordinal) < response.IndexOf("\"p1\"", StringComparison.Ordinal));
        Assert.True(response.IndexOf("\"p1\"", StringComparison.Ordinal) < response.IndexOf("\"p2\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkedRequestBodyIsDecoded()
    {
        await using var s = await TestServer.StartAsync();
        var body = ModernEcho(7, "chunky");
        var half = body.Length / 2;
        var chunked = $"{half:X}\r\n{body[..half]}\r\n{body.Length - half:X}\r\n{body[half..]}\r\n0\r\n\r\n";
        var response = await s.RawAsync($"POST /mcp HTTP/1.1\r\n{ModernHeaders(s)}Transfer-Encoding: chunked\r\n\r\n{chunked}");
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("chunky", response);
    }

    [Fact]
    public async Task ExpectContinueIsHonored()
    {
        await using var s = await TestServer.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, s.Port);
        var stream = client.GetStream();
        var body = ModernEcho(1, "continued");
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"POST /mcp HTTP/1.1\r\n{ModernHeaders(s)}Expect: 100-continue\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n"));
        var interim = await TestServer.ReadAllAsync(stream, TimeSpan.FromMilliseconds(500));
        Assert.StartsWith("HTTP/1.1 100 Continue", interim);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
        var final = await TestServer.ReadAllAsync(stream, TimeSpan.FromMilliseconds(500));
        Assert.Contains("continued", final);
    }

    [Fact]
    public async Task OversizedBodyGets413()
    {
        await using var s = await TestServer.StartAsync(o => o.MaxRequestBytes = 2048);
        var response = await s.RawAsync($"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n{s.AuthHeader}Content-Type: application/json\r\nContent-Length: 999999\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 413", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task SlowlorisHeadersTimeOut()
    {
        var limits = new HttpLimits { HeaderTimeout = TimeSpan.FromMilliseconds(400) };
        await using var s = await TestServer.StartAsync(limits: limits);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, s.Port);
        var stream = client.GetStream();
        await stream.WriteAsync("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n"u8.ToArray());
        var started = DateTime.UtcNow;
        var response = await TestServer.ReadAllAsync(stream, TimeSpan.FromSeconds(3));
        Assert.StartsWith("HTTP/1.1 408", response);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task SilentConnectionIsClosed()
    {
        var limits = new HttpLimits { HeaderTimeout = TimeSpan.FromMilliseconds(300) };
        await using var s = await TestServer.StartAsync(limits: limits);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, s.Port);
        var stream = client.GetStream();
        var buffer = new byte[16];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var read = await stream.ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task SlowBodyTimesOut()
    {
        var limits = new HttpLimits { BodyTimeout = TimeSpan.FromMilliseconds(400) };
        await using var s = await TestServer.StartAsync(limits: limits);
        var response = await s.RawAsync($"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n{s.AuthHeader}Content-Type: application/json\r\nContent-Length: 100\r\n\r\n{{\"jsonrpc\"", TimeSpan.FromSeconds(2));
        Assert.StartsWith("HTTP/1.1 408", response);
    }

    [Fact]
    public async Task MalformedRequestGets400AndClose()
    {
        await using var s = await TestServer.StartAsync();
        var response = await s.RawAsync("POST /mcp HTTP/1.1\r\nHost: a\r\nContent-Length: 3\r\nTransfer-Encoding: chunked\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task Http10GetsCloseDelimitedResponse()
    {
        await using var s = await TestServer.StartAsync();
        var body = ModernEcho(1, "old");
        var response = await s.RawAsync($"POST /mcp HTTP/1.0\r\n{ModernHeaders(s)}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Connection: close", response);
        Assert.Contains("\"old\"", response);
    }

    [Fact]
    public async Task UnknownPathAndMethod()
    {
        await using var s = await TestServer.StartAsync();
        Assert.StartsWith("HTTP/1.1 404", await s.RawAsync($"GET /other HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"));
        var put = await s.RawAsync($"PUT /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n{s.AuthHeader}Content-Length: 0\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 405", put);
        Assert.Contains("Allow: GET, POST, DELETE, OPTIONS", put);
        // Trailing slash is tolerated.
        var slash = await s.RawAsync($"OPTIONS /mcp/ HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 204", slash);
    }

    [Fact]
    public async Task WrongContentTypeAndAccept()
    {
        await using var s = await TestServer.StartAsync();
        var body = ModernEcho(1, "x");
        var headers = ModernHeaders(s).Replace("Content-Type: application/json", "Content-Type: text/plain");
        Assert.StartsWith("HTTP/1.1 415", await s.RawAsync($"POST /mcp HTTP/1.1\r\n{headers}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
        headers = ModernHeaders(s).Replace("Accept: application/json, text/event-stream", "Accept: text/html");
        Assert.StartsWith("HTTP/1.1 406", await s.RawAsync($"POST /mcp HTTP/1.1\r\n{headers}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
    }

    [Fact]
    public async Task StopReleasesPortPromptlyEvenWithOpenConnections()
    {
        var s = await TestServer.StartAsync();
        var port = s.Port;

        // An open SSE stream and an idle keep-alive connection must not block shutdown or rebinding.
        var sessionId = await s.InitializeAsync();
        var get = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        get.Headers.Add("Mcp-Session-Id", sessionId);
        get.Headers.Add("Accept", "text/event-stream");
        var stream = await s.Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, port);

        var started = DateTime.UtcNow;
        await s.Server.StopAsync();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "StopAsync took too long");
        Assert.False(s.Server.IsRunning);

        await using var again = new McpServer(new McpServerOptions { Port = port }, s.Game, s.Host);
        await again.StartAsync();
        Assert.Equal(port, again.ListeningPort);
        await again.StopAsync();

        // Start/Stop repeatedly on the same instance.
        s.Server.Options.Port = port;
        await s.Server.StartAsync();
        Assert.Equal(port, s.Server.ListeningPort);
        await s.Server.StopAsync();
        await s.Server.StartAsync();
        await s.Server.StartAsync();
        Assert.True(s.Server.IsRunning);
        await s.DisposeAsync();
    }

    [Fact]
    public async Task ManyConcurrentClients()
    {
        await using var s = await TestServer.StartAsync();
        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            var (status, response, _) = await s.ModernCallAsync("tools/call", new JsonObject { ["name"] = "add", ["arguments"] = new JsonObject { ["a"] = i, ["b"] = 1 } });
            Assert.Equal(HttpStatusCode.OK, status);
            return response["result"]!["structuredContent"]!["result"]!.GetValue<long>();
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), results.Order());
    }
}

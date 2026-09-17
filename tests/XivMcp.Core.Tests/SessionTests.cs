using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Core.Http;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public class SessionTests
{
    [Fact]
    public async Task IdleSessionsExpire()
    {
        await using var s = await TestServer.StartAsync(o => o.SessionIdleTimeout = TimeSpan.FromMilliseconds(1200));
        var idle = await s.InitializeAsync(clientName: "idle");
        var busy = await s.InitializeAsync(clientName: "busy");
        Assert.Equal(2, s.Server.GetStatus().ActiveSessions);

        // Keep one alive with an open GET stream; the other idles out.
        var get = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        get.Headers.Add("Mcp-Session-Id", busy);
        get.Headers.Add("Accept", "text/event-stream");
        using var stream = await s.Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);

        await Task.Delay(3500);
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" };
        using (var gone = await s.Http.SendAsync(s.Post(body, idle, "2025-11-25")))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using (var alive = await s.Http.SendAsync(s.Post(body, busy, "2025-11-25")))
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.DoesNotContain("idle 1.0", s.Server.GetStatus().ConnectedClients);
        Assert.Contains(s.Logs, l => l.Contains("expired"));
    }

    [Fact]
    public async Task PostStreamCanBeResumedAfterDisconnect()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync();
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 11,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "progress",
                ["arguments"] = new JsonObject { ["steps"] = 10 },
                ["_meta"] = new JsonObject { ["progressToken"] = "p" },
            },
        }.ToJsonString();

        string lastId;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, s.Port);
            var ns = client.GetStream();
            var raw = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n{s.AuthHeader}Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n" +
                      $"Mcp-Session-Id: {sessionId}\r\nMCP-Protocol-Version: 2025-11-25\r\nContent-Length: {Encoding.UTF8.GetByteCount(call)}\r\n\r\n{call}";
            await ns.WriteAsync(Encoding.UTF8.GetBytes(raw));
            var text = await TestServer.ReadAllAsync(ns, TimeSpan.FromMilliseconds(120));
            var ids = text.Split('\n').Where(l => l.StartsWith("id: ", StringComparison.Ordinal)).Select(l => l[4..].Trim()).ToList();
            Assert.NotEmpty(ids);
            lastId = ids[^1];
            client.Client.LingerState = new LingerOption(true, 0);
        }

        var get = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        get.Headers.Add("Mcp-Session-Id", sessionId);
        get.Headers.Add("Accept", "text/event-stream");
        get.Headers.Add("Last-Event-ID", lastId);
        using var resumed = await s.Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var (response, notifications) = await TestServer.ReadRpcAsync(resumed, TimeSpan.FromSeconds(10));
        Assert.Equal(11, response["id"]!.GetValue<int>());
        Assert.Equal(10, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        var progress = notifications.Where(n => n["method"]!.GetValue<string>() == "notifications/progress").Select(n => n["params"]!["progress"]!.GetValue<double>()).ToList();
        Assert.Equal(10d, progress[^1]);
        Assert.Equal(progress.Order(), progress);
    }

    [Fact]
    public async Task NewStandaloneStreamReplacesOldOne()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync("2025-06-18");

        HttpRequestMessage Get()
        {
            var get = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
            get.Headers.Add("Mcp-Session-Id", sessionId);
            get.Headers.Add("Accept", "text/event-stream");
            return get;
        }

        using var first = await s.Http.SendAsync(Get(), HttpCompletionOption.ResponseHeadersRead);
        using var firstReader = new SseReader(await first.Content.ReadAsStreamAsync());
        using var second = await s.Http.SendAsync(Get(), HttpCompletionOption.ResponseHeadersRead);
        using var secondReader = new SseReader(await second.Content.ReadAsStreamAsync());
        Assert.Null(await firstReader.NextAsync(TimeSpan.FromSeconds(5)));

        s.Server.Notifier.ToolListChanged();
        Assert.Equal("notifications/tools/list_changed", (await secondReader.NextMessageAsync())!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task KeepAliveCommentsOnIdleStreams()
    {
        await using var s = await TestServer.StartAsync(o => o.SseKeepAliveInterval = TimeSpan.FromMilliseconds(150));
        var sessionId = await s.InitializeAsync("2025-06-18");
        var get = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        get.Headers.Add("Mcp-Session-Id", sessionId);
        get.Headers.Add("Accept", "text/event-stream");
        using var response = await s.Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new SseReader(await response.Content.ReadAsStreamAsync());
        await Task.Delay(700);
        s.Server.Notifier.ToolListChanged();
        await reader.NextMessageAsync();
        Assert.True(reader.CommentCount >= 2, $"expected keep-alive comments, got {reader.CommentCount}");
    }

    [Fact]
    public async Task DeletingSessionEndsItsStream()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync();
        var get = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        get.Headers.Add("Mcp-Session-Id", sessionId);
        get.Headers.Add("Accept", "text/event-stream");
        using var response = await s.Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new SseReader(await response.Content.ReadAsStreamAsync());
        await reader.NextAsync(); // priming

        var delete = new HttpRequestMessage(HttpMethod.Delete, s.Endpoint);
        delete.Headers.Add("Mcp-Session-Id", sessionId);
        using (await s.Http.SendAsync(delete))
        {
        }

        Assert.Null(await reader.NextAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SlowConsumerIsDroppedInsteadOfBlocking()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync("2025-06-18");
        using var client = new TcpClient();
        client.ReceiveBufferSize = 4096;
        await client.ConnectAsync(IPAddress.Loopback, s.Port);
        var ns = client.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n{s.AuthHeader}Accept: text/event-stream\r\nMcp-Session-Id: {sessionId}\r\n\r\n"));
        await Task.Delay(200);

        // Never read: the socket buffers fill, then the bounded queue, then the stream is dropped.
        var big = new string('x', 64 * 1024);
        var started = DateTime.UtcNow;
        for (var i = 0; i < 3000; i++)
            s.Server.Notifier.Log(McpLogLevel.Error, "flood", big);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "notifier blocked on a slow consumer");

        // Other clients are unaffected.
        var (status, _, _) = await s.ModernCallAsync("tools/list");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public void SseEventIdParsing()
    {
        Assert.True(Protocol.SseLogicalStream.TryParseEventId("p12_34", out var stream, out var seq));
        Assert.Equal("p12", stream);
        Assert.Equal(34, seq);
        Assert.False(Protocol.SseLogicalStream.TryParseEventId("nope", out _, out _));
        Assert.False(Protocol.SseLogicalStream.TryParseEventId("g_", out _, out _));
        Assert.False(Protocol.SseLogicalStream.TryParseEventId("g_-1", out _, out _));
    }

    [Fact]
    public async Task HttpLimitsOverrideIsUsed()
    {
        await using var s = await TestServer.StartAsync(limits: new HttpLimits { MaxConnections = 2 });
        using var a = new TcpClient();
        using var b = new TcpClient();
        await a.ConnectAsync(IPAddress.Loopback, s.Port);
        await b.ConnectAsync(IPAddress.Loopback, s.Port);
        await Task.Delay(200);
        var response = await s.RawAsync("GET /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 503", response);
    }
}

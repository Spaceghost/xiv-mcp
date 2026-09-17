using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public class ModernProtocolTests
{
    private static async Task<(HttpStatusCode Status, JsonObject Body)> SendAsync(TestServer s, HttpRequestMessage request)
    {
        using var response = await s.Http.SendAsync(request);
        return (response.StatusCode, (JsonObject)await TestServer.ReadJsonAsync(response));
    }

    [Fact]
    public async Task DiscoverAdvertisesVersionsCapabilitiesAndCaching()
    {
        await using var s = await TestServer.StartAsync();
        var (status, response, _) = await s.ModernCallAsync("server/discover");
        Assert.Equal(HttpStatusCode.OK, status);
        var result = response["result"]!;
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
        Assert.Equal(["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26"], result["supportedVersions"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.True(result["capabilities"]!["resources"]!["subscribe"]!.GetValue<bool>());
        Assert.Equal("xiv-mcp", result["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"]!.GetValue<string>());
        Assert.True(result["ttlMs"]!.GetValue<int>() >= 0);
        Assert.Equal("private", result["cacheScope"]!.GetValue<string>());
        Assert.Equal("test instructions", result["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListsAreCacheableAndDeterministic()
    {
        await using var s = await TestServer.StartAsync();
        foreach (var method in new[] { "tools/list", "resources/list", "resources/templates/list", "prompts/list" })
        {
            var (status, first, _) = await s.ModernCallAsync(method);
            var (_, second, _) = await s.ModernCallAsync(method);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("complete", first["result"]!["resultType"]!.GetValue<string>());
            Assert.NotNull(first["result"]!["ttlMs"]);
            Assert.Equal("private", first["result"]!["cacheScope"]!.GetValue<string>());
            first["id"] = second["id"]!.DeepClone();
            Assert.Equal(second.ToJsonString(), first.ToJsonString());
        }

        var (_, call, _) = await s.ModernCallAsync("tools/call", new JsonObject { ["name"] = "echo", ["arguments"] = new JsonObject { ["text"] = "x" } });
        Assert.Equal("complete", call["result"]!["resultType"]!.GetValue<string>());
        Assert.Null(call["result"]!["ttlMs"]);
    }

    [Fact]
    public async Task MissingMetaIsInvalidParams400()
    {
        await using var s = await TestServer.StartAsync();
        var request = s.Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list", ["params"] = new JsonObject() }, protocolVersion: TestServer.Modern);
        request.Headers.Add("Mcp-Method", "tools/list");
        var (status, body) = await SendAsync(s, request);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(-32602, body["error"]!["code"]!.GetValue<int>());

        var noCaps = s.ModernPost("tools/list");
        var content = JsonNode.Parse(await noCaps.Content!.ReadAsStringAsync())!;
        content["params"]!["_meta"]!.AsObject().Remove("io.modelcontextprotocol/clientCapabilities");
        noCaps.Content = new StringContent(content.ToJsonString(), Encoding.UTF8, "application/json");
        (status, body) = await SendAsync(s, noCaps);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(-32602, body["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("2027-01-01")]
    [InlineData("2025-11-25")]
    public async Task UnsupportedVersionListsSupported(string version)
    {
        await using var s = await TestServer.StartAsync();
        var p = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = version,
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
            },
        };
        var request = s.Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/list", ["params"] = p }, protocolVersion: version);
        request.Headers.Add("Mcp-Method", "tools/list");
        var (status, body) = await SendAsync(s, request);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(-32022, body["error"]!["code"]!.GetValue<int>());
        Assert.Equal(version, body["error"]!["data"]!["requested"]!.GetValue<string>());
        Assert.Contains("2026-07-28", body["error"]!["data"]!["supported"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(3, body["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task HeaderValidation()
    {
        await using var s = await TestServer.StartAsync();

        async Task ExpectMismatch(HttpRequestMessage request)
        {
            var (status, body) = await SendAsync(s, request);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(-32020, body["error"]!["code"]!.GetValue<int>());
        }

        var noVersionHeader = s.ModernPost("tools/list");
        noVersionHeader.Headers.Remove("MCP-Protocol-Version");
        await ExpectMismatch(noVersionHeader);

        var noMethodHeader = s.ModernPost("tools/list");
        noMethodHeader.Headers.Remove("Mcp-Method");
        await ExpectMismatch(noMethodHeader);

        var wrongMethodHeader = s.ModernPost("tools/list");
        wrongMethodHeader.Headers.Remove("Mcp-Method");
        wrongMethodHeader.Headers.Add("Mcp-Method", "prompts/list");
        await ExpectMismatch(wrongMethodHeader);

        var callArgs = new JsonObject { ["name"] = "echo", ["arguments"] = new JsonObject { ["text"] = "héllo" } };
        var noName = s.ModernPost("tools/call", callArgs);
        noName.Headers.Remove("Mcp-Name");
        await ExpectMismatch(noName);

        var wrongName = s.ModernPost("tools/call", callArgs);
        wrongName.Headers.Remove("Mcp-Name");
        wrongName.Headers.Add("Mcp-Name", "add");
        await ExpectMismatch(wrongName);

        var badBase64 = s.ModernPost("tools/call", callArgs);
        badBase64.Headers.Remove("Mcp-Name");
        badBase64.Headers.Add("Mcp-Name", "=?base64?!!!?=");
        await ExpectMismatch(badBase64);

        var encoded = s.ModernPost("tools/call", callArgs);
        encoded.Headers.Remove("Mcp-Name");
        encoded.Headers.Add("Mcp-Name", "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("echo")) + "?=");
        var (status, body) = await SendAsync(s, encoded);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("héllo", body["result"]!["structuredContent"]!["result"]!.GetValue<string>());

        var resource = s.ModernPost("resources/read", new JsonObject { ["uri"] = "test://text" });
        resource.Headers.Remove("Mcp-Name");
        resource.Headers.Add("Mcp-Name", "test://json");
        await ExpectMismatch(resource);
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("initialize/extra")]
    [InlineData("logging/setLevel")]
    [InlineData("resources/subscribe")]
    public async Task RemovedOrUnknownMethodsAre404(string method)
    {
        await using var s = await TestServer.StartAsync();
        var (status, body) = await SendAsync(s, s.ModernPost(method));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(-32601, body["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task SessionHeaderIsIgnoredAndNotissued()
    {
        await using var s = await TestServer.StartAsync();
        var request = s.ModernPost("tools/list");
        request.Headers.Add("Mcp-Session-Id", "whatever");
        using var response = await s.Http.SendAsync(request);
        // A modern request that carries an unknown session id is served statelessly.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));

        using var clean = await s.Http.SendAsync(s.ModernPost("tools/list"));
        Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
        Assert.False(clean.Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task ResourceErrorsUseInvalidParams()
    {
        await using var s = await TestServer.StartAsync();
        var (status, response, _) = await s.ModernCallAsync("resources/read", new JsonObject { ["uri"] = "test://missing" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());

        var (_, ok, _) = await s.ModernCallAsync("resources/read", new JsonObject { ["uri"] = "test://item/3" });
        Assert.Equal(0, ok["result"]!["ttlMs"]!.GetValue<int>());
        Assert.Equal("test://item/3", ok["result"]!["contents"]![0]!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task NotificationsAre202AndResponsesAre400()
    {
        await using var s = await TestServer.StartAsync();
        using (var n = await s.Http.SendAsync(s.Post(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = 1 } })))
            Assert.Equal(HttpStatusCode.Accepted, n.StatusCode);
        using (var r = await s.Http.SendAsync(s.Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject() })))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task LogsOnlyWhenRequestedAndProgressStreams()
    {
        await using var s = await TestServer.StartAsync();
        var args = new JsonObject { ["name"] = "progress", ["arguments"] = new JsonObject { ["steps"] = 2 } };

        var (_, quiet, quietNotes) = await s.ModernCallAsync("tools/call", args);
        Assert.Empty(quietNotes);
        Assert.Equal(2, quiet["result"]!["structuredContent"]!["count"]!.GetValue<int>());

        var (_, _, notes) = await s.ModernCallAsync("tools/call", args, new JsonObject
        {
            ["io.modelcontextprotocol/logLevel"] = "debug",
            ["progressToken"] = 99,
        });
        Assert.Equal(4, notes.Count(n => n["method"]!.GetValue<string>() == "notifications/message"));
        Assert.Equal(2, notes.Count(n => n["method"]!.GetValue<string>() == "notifications/progress"));
        Assert.All(notes.Where(n => n["method"]!.GetValue<string>() == "notifications/progress"), n => Assert.Equal(99, n["params"]!["progressToken"]!.GetValue<int>()));

        var (_, _, warnOnly) = await s.ModernCallAsync("tools/call", args, new JsonObject { ["io.modelcontextprotocol/logLevel"] = "warning" });
        Assert.Equal(2, warnOnly.Count);

        using var bad = await s.Http.SendAsync(s.ModernPost("tools/call", args, new JsonObject { ["io.modelcontextprotocol/logLevel"] = "chatty" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task ClosingTheConnectionCancelsTheRequest()
    {
        await using var s = await TestServer.StartAsync();
        SlowProvider.Reset();
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "wait_for_cancel", ["_meta"] = TestServer.ModernMeta() },
        }.ToJsonString();
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, s.Port);
            var raw = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n{s.AuthHeader}Content-Type: application/json\r\nMCP-Protocol-Version: 2026-07-28\r\n" +
                      $"Mcp-Method: tools/call\r\nMcp-Name: wait_for_cancel\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(raw));
            await SlowProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(await SlowProvider.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        Assert.Contains(s.Server.GetActivity(10), a => a.Target == "wait_for_cancel" && !a.Success && a.Error!.Contains("cancelled"));
    }

    [Fact]
    public async Task SubscriptionsListenStream()
    {
        await using var s = await TestServer.StartAsync();
        var request = s.ModernPost("subscriptions/listen", new JsonObject
        {
            ["notifications"] = new JsonObject
            {
                ["toolsListChanged"] = true,
                ["resourceSubscriptions"] = new JsonArray("test://json"),
            },
        }, id: 42);
        using var response = await s.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        var ack = await reader.NextMessageAsync();
        Assert.Equal("notifications/subscriptions/acknowledged", ack!["method"]!.GetValue<string>());
        Assert.Equal(42, ack["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<int>());
        Assert.True(ack["params"]!["notifications"]!["toolsListChanged"]!.GetValue<bool>());
        Assert.Null(ack["params"]!["notifications"]!["promptsListChanged"]);

        s.Server.Notifier.PromptListChanged(); // not requested
        s.Server.Notifier.Log(McpLogLevel.Emergency, "x", "never on listen streams");
        s.Server.Notifier.ResourceUpdated("test://text"); // not subscribed
        s.Server.Notifier.ResourceUpdated("test://json");
        s.Server.Notifier.ToolListChanged();

        var updated = await reader.NextMessageAsync();
        Assert.Equal("notifications/resources/updated", updated!["method"]!.GetValue<string>());
        Assert.Equal("test://json", updated["params"]!["uri"]!.GetValue<string>());
        Assert.Equal(42, updated["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<int>());
        var toolsChanged = await reader.NextMessageAsync();
        Assert.Equal("notifications/tools/list_changed", toolsChanged!["method"]!.GetValue<string>());

        // Gate changes are detected by housekeeping and announced.
        s.Host.Permissions[ToolPermission.Action] = true;
        var gateChange = await reader.NextMessageAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("notifications/tools/list_changed", gateChange!["method"]!.GetValue<string>());

        // Graceful closure on server stop: a result response for the listen request.
        var stop = s.Server.StopAsync();
        var closing = await reader.NextMessageAsync();
        Assert.Equal(42, closing!["id"]!.GetValue<int>());
        Assert.Equal("complete", closing["result"]!["resultType"]!.GetValue<string>());
        Assert.Equal(42, closing["result"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<int>());
        await stop;
    }

    [Fact]
    public async Task ListenRequiresFilterAndEventStream()
    {
        await using var s = await TestServer.StartAsync();
        var (status, body) = await SendAsync(s, s.ModernPost("subscriptions/listen"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(-32602, body["error"]!["code"]!.GetValue<int>());

        using var jsonOnly = await s.Http.SendAsync(s.ModernPost("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject() }, accept: "application/json"));
        Assert.Equal(HttpStatusCode.NotAcceptable, jsonOnly.StatusCode);
    }

    [Fact]
    public async Task ModernClientsAppearInStatus()
    {
        await using var s = await TestServer.StartAsync();
        await s.ModernCallAsync("tools/list");
        var legacy = await s.InitializeAsync(clientName: "legacy-client");
        var status = s.Server.GetStatus();
        Assert.True(status.Running);
        Assert.Contains("modern-test 2.0", status.ConnectedClients);
        Assert.Contains("legacy-client 1.0", status.ConnectedClients);
        Assert.Equal(2, status.ActiveSessions);
        Assert.StartsWith("http://127.0.0.1:", status.Endpoint);
        Assert.EndsWith("/mcp", status.Endpoint);
        Assert.NotNull(legacy);
    }
}

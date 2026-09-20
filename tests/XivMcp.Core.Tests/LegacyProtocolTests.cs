using System.Net;
using System.Text.Json.Nodes;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public class LegacyProtocolTests
{
    private static JsonObject Rpc(int id, string method, JsonObject? p = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (p is not null)
            o["params"] = p;
        return o;
    }

    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2024-11-05", "2025-11-25")]
    [InlineData("2099-01-01", "2025-11-25")]
    [InlineData("2026-07-28", "2025-11-25")]
    public async Task InitializeNegotiatesVersion(string requested, string expected)
    {
        await using var s = await TestServer.StartAsync();
        var body = Rpc(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = requested,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "neg", ["version"] = "0" },
        });
        using var response = await s.Http.SendAsync(s.Post(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var sessionId = response.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.Matches("^[0-9a-f]{32}$", sessionId);
        var result = (await TestServer.ReadJsonAsync(response))["result"]!;
        Assert.Equal(expected, result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("xiv-mcp", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("9.9.9", result["serverInfo"]!["version"]!.GetValue<string>());
        Assert.Equal("test instructions", result["instructions"]!.GetValue<string>());
        var caps = result["capabilities"]!;
        Assert.True(caps["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.True(caps["resources"]!["subscribe"]!.GetValue<bool>());
        Assert.NotNull(caps["logging"]);
        Assert.NotNull(caps["completions"]);
        Assert.True(caps["prompts"]!["listChanged"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SessionHeaderRules()
    {
        await using var s = await TestServer.StartAsync();

        using (var missing = await s.Http.SendAsync(s.Post(Rpc(1, "tools/list"))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal(-32600, (await TestServer.ReadJsonAsync(missing))["error"]!["code"]!.GetValue<int>());
        }

        using (var unknown = await s.Http.SendAsync(s.Post(Rpc(2, "tools/list"), "0123456789abcdef0123456789abcdef", "2025-11-25")))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            var body = await TestServer.ReadJsonAsync(unknown);
            Assert.Equal(-32001, body["error"]!["code"]!.GetValue<int>());
            Assert.Equal(2, body["id"]!.GetValue<int>());
        }

        var sessionId = await s.InitializeAsync("2025-06-18");
        using (var wrongVersion = await s.Http.SendAsync(s.Post(Rpc(3, "tools/list"), sessionId, "2025-11-25")))
            Assert.Equal(HttpStatusCode.BadRequest, wrongVersion.StatusCode);
        using (var noVersionHeader = await s.Http.SendAsync(s.Post(Rpc(4, "ping"), sessionId)))
            Assert.Equal(HttpStatusCode.OK, noVersionHeader.StatusCode);

        var ping = await s.LegacyCallAsync(sessionId, "ping", version: "2025-06-18");
        Assert.Empty(ping["result"]!.AsObject());
        Assert.Null(ping["result"]!["resultType"]);
    }

    [Fact]
    public async Task DeleteEndsSession()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync();
        var delete = new HttpRequestMessage(HttpMethod.Delete, s.Endpoint);
        delete.Headers.Add("Mcp-Session-Id", sessionId);
        using (var response = await s.Http.SendAsync(delete))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var after = await s.Http.SendAsync(s.Post(Rpc(1, "ping"), sessionId, "2025-11-25")))
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        var again = new HttpRequestMessage(HttpMethod.Delete, s.Endpoint);
        again.Headers.Add("Mcp-Session-Id", sessionId);
        using (var response = await s.Http.SendAsync(again))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var noHeader = await s.Http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, s.Endpoint)))
            Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
    }

    [Fact]
    public async Task JsonRpcErrors()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync();

        var parse = s.Post(new JsonObject(), sessionId, "2025-11-25");
        parse.Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        using (var response = await s.Http.SendAsync(parse))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await TestServer.ReadJsonAsync(response);
            Assert.Equal(-32700, body["error"]!["code"]!.GetValue<int>());
            Assert.Null(body["id"]);
        }

        using (var response = await s.Http.SendAsync(s.Post(new JsonObject { ["jsonrpc"] = "1.0", ["id"] = 5, ["method"] = "ping" }, sessionId, "2025-11-25")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(-32600, (await TestServer.ReadJsonAsync(response))["error"]!["code"]!.GetValue<int>());
        }

        using (var response = await s.Http.SendAsync(s.Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = true, ["method"] = "ping" }, sessionId, "2025-11-25")))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var notFound = await s.LegacyCallAsync(sessionId, "tools/frobnicate");
        Assert.Equal(-32601, notFound["error"]!["code"]!.GetValue<int>());
        var modernOnly = await s.LegacyCallAsync(sessionId, "server/discover");
        Assert.Equal(-32601, modernOnly["error"]!["code"]!.GetValue<int>());

        var missingName = await s.LegacyCallAsync(sessionId, "tools/call", new JsonObject());
        Assert.Equal(-32602, missingName["error"]!["code"]!.GetValue<int>());
        var unknownTool = await s.LegacyCallAsync(sessionId, "tools/call", new JsonObject { ["name"] = "nope" });
        Assert.Equal(-32602, unknownTool["error"]!["code"]!.GetValue<int>());

        // Notifications and responses are acknowledged with 202 and no body.
        using (var response = await s.Http.SendAsync(s.Post(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/whatever" }, sessionId, "2025-11-25")))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        using (var response = await s.Http.SendAsync(s.Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 9, ["result"] = new JsonObject() }, sessionId, "2025-11-25")))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task ToolsListAndCallAcrossVersions()
    {
        await using var s = await TestServer.StartAsync();
        foreach (var version in new[] { "2025-11-25", "2025-06-18", "2025-03-26" })
        {
            var sessionId = await s.InitializeAsync(version);
            var list = await s.LegacyCallAsync(sessionId, "tools/list", version: version);
            var tools = list["result"]!["tools"]!.AsArray();
            var echo = tools.Single(t => t!["name"]!.GetValue<string>() == "echo")!;
            Assert.Null(list["result"]!["nextCursor"]);
            Assert.Equal("Echo", echo["annotations"]!["title"]!.GetValue<string>());
            Assert.True(echo["annotations"]!["readOnlyHint"]!.GetValue<bool>());
            var waymark = tools.Single(t => t!["name"]!.GetValue<string>() == "make_waymark")!;

            var call = await s.LegacyCallAsync(sessionId, "tools/call", new JsonObject { ["name"] = "add", ["arguments"] = new JsonObject { ["a"] = 2, ["b"] = 40 } }, version);
            var result = call["result"]!;
            Assert.Null(result["isError"]);
            Assert.Equal("{\"result\":42}", result["content"]![0]!["text"]!.GetValue<string>());
            Assert.Null(result["resultType"]);

            if (version == "2025-03-26")
            {
                Assert.Null(echo["title"]);
                Assert.Null(waymark["outputSchema"]);
                Assert.Null(result["structuredContent"]);
            }
            else
            {
                Assert.Equal("Echo", echo["title"]!.GetValue<string>());
                Assert.NotNull(waymark["outputSchema"]);
                Assert.Equal(42, result["structuredContent"]!["result"]!.GetValue<int>());
            }

            // Gated tools are hidden.
            Assert.DoesNotContain(tools, t => t!["name"]!.GetValue<string>() == "do_action");
        }
    }

    [Fact]
    public async Task ToolsListPagination()
    {
        await using var s = await TestServer.StartAsync(o => o.ListPageSize = 4);
        var sessionId = await s.InitializeAsync();
        var names = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var p = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
            var page = await s.LegacyCallAsync(sessionId, "tools/list", p);
            var tools = page["result"]!["tools"]!.AsArray();
            Assert.True(tools.Count <= 4);
            names.AddRange(tools.Select(t => t!["name"]!.GetValue<string>()));
            cursor = page["result"]!["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null && pages < 50);

        var expected = s.Server.ListRegisteredTools().Where(t => t.Permission <= ToolPermission.Ui).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, names);
        Assert.True(pages > 1);

        var bad = await s.LegacyCallAsync(sessionId, "tools/list", new JsonObject { ["cursor"] = "garbage!" });
        Assert.Equal(-32602, bad["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task BatchOnlyFor20250326()
    {
        await using var s = await TestServer.StartAsync();
        var old = await s.InitializeAsync("2025-03-26");
        var batch = new JsonArray(
            Rpc(1, "ping"),
            Rpc(2, "tools/call", new JsonObject { ["name"] = "echo", ["arguments"] = new JsonObject { ["text"] = "b" } }),
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            Rpc(3, "nope/nope"),
            new JsonObject { ["bad"] = true });
        using (var response = await s.Http.SendAsync(s.Post(batch, old, "2025-03-26")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responses = (await TestServer.ReadJsonAsync(response)).AsArray();
            Assert.Equal(4, responses.Count);
            Assert.Contains(responses, r => r!["id"]?.GetValue<int>() == 2 && r["result"]!["content"]![0]!["text"]!.GetValue<string>() == "b");
            Assert.Contains(responses, r => r!["id"]?.GetValue<int>() == 3 && r["error"]!["code"]!.GetValue<int>() == -32601);
            Assert.Contains(responses, r => r!["id"] is null && r["error"]!["code"]!.GetValue<int>() == -32600);
        }

        using (var onlyNotifications = await s.Http.SendAsync(s.Post(new JsonArray(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }), old, "2025-03-26")))
            Assert.Equal(HttpStatusCode.Accepted, onlyNotifications.StatusCode);

        var newer = await s.InitializeAsync("2025-06-18");
        using (var rejected = await s.Http.SendAsync(s.Post(new JsonArray(Rpc(1, "ping")), newer, "2025-06-18")))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var noSession = await s.Http.SendAsync(s.Post(new JsonArray(Rpc(1, "initialize")))))
            Assert.Equal(HttpStatusCode.BadRequest, noSession.StatusCode);
    }

    [Fact]
    public async Task ResourcesAndTemplates()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync();
        var list = await s.LegacyCallAsync(sessionId, "resources/list");
        var uris = list["result"]!["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>()).ToArray();
        Assert.Contains("test://text", uris);
        Assert.Contains("test://gated", uris);

        var templates = await s.LegacyCallAsync(sessionId, "resources/templates/list");
        Assert.Contains(templates["result"]!["resourceTemplates"]!.AsArray(), t => t!["uriTemplate"]!.GetValue<string>() == "test://item/{id}");

        var text = await s.LegacyCallAsync(sessionId, "resources/read", new JsonObject { ["uri"] = "test://text" });
        var content = text["result"]!["contents"]![0]!;
        Assert.Equal("hello", content["text"]!.GetValue<string>());
        Assert.Equal("text/plain", content["mimeType"]!.GetValue<string>());

        var json = await s.LegacyCallAsync(sessionId, "resources/read", new JsonObject { ["uri"] = "test://json" });
        Assert.Equal("{\"x\":1,\"y\":2,\"z\":3}", json["result"]!["contents"]![0]!["text"]!.GetValue<string>());

        var blob = await s.LegacyCallAsync(sessionId, "resources/read", new JsonObject { ["uri"] = "test://blob" });
        Assert.Equal(Convert.ToBase64String(new byte[] { 0, 1, 2, 255 }), blob["result"]!["contents"]![0]!["blob"]!.GetValue<string>());

        var item = await s.LegacyCallAsync(sessionId, "resources/read", new JsonObject { ["uri"] = "test://item/7" });
        Assert.Contains("\"x\":7", item["result"]!["contents"]![0]!["text"]!.GetValue<string>());

        var reserved = await s.LegacyCallAsync(sessionId, "resources/read", new JsonObject { ["uri"] = "test://colour/dark_blue/a/b%20c" });
        Assert.Equal("DarkBlue:a/b c", reserved["result"]!["contents"]![0]!["text"]!.GetValue<string>());

        foreach (var missing in new[] { "test://nope", "test://item/404", "test://item/notanumber" })
        {
            var error = await s.LegacyCallAsync(sessionId, "resources/read", new JsonObject { ["uri"] = missing });
            Assert.Equal(-32002, error["error"]!["code"]!.GetValue<int>());
            Assert.Equal(missing, error["error"]!["data"]!["uri"]!.GetValue<string>());
        }

        s.Host.LoggedIn = false;
        var login = await s.LegacyCallAsync(sessionId, "resources/read", new JsonObject { ["uri"] = "test://login" });
        Assert.Contains("logged-in", login["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task PromptsAndCompletion()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync();
        var prompts = await s.LegacyCallAsync(sessionId, "prompts/list");
        var greet = prompts["result"]!["prompts"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "greet")!;
        Assert.Equal("Greeting", greet["title"]!.GetValue<string>());
        var args = greet["arguments"]!.AsArray();
        Assert.True(args.Single(a => a!["name"]!.GetValue<string>() == "name")!["required"]!.GetValue<bool>());
        Assert.False(args.Single(a => a!["name"]!.GetValue<string>() == "colour")!["required"]!.GetValue<bool>());

        var get = await s.LegacyCallAsync(sessionId, "prompts/get", new JsonObject { ["name"] = "greet", ["arguments"] = new JsonObject { ["name"] = "Wyn", ["colour"] = "lightGreen" } });
        Assert.Equal("Hello Wyn in LightGreen", get["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>());
        Assert.Equal("user", get["result"]!["messages"]![0]!["role"]!.GetValue<string>());

        var structured = await s.LegacyCallAsync(sessionId, "prompts/get", new JsonObject { ["name"] = "structured", ["arguments"] = new JsonObject { ["count"] = "5" } });
        Assert.Equal("structured", structured["result"]!["description"]!.GetValue<string>());
        Assert.Equal("assistant", structured["result"]!["messages"]![1]!["role"]!.GetValue<string>());

        var missing = await s.LegacyCallAsync(sessionId, "prompts/get", new JsonObject { ["name"] = "greet" });
        Assert.Equal(-32602, missing["error"]!["code"]!.GetValue<int>());
        var unknown = await s.LegacyCallAsync(sessionId, "prompts/get", new JsonObject { ["name"] = "nope" });
        Assert.Equal(-32602, unknown["error"]!["code"]!.GetValue<int>());

        var completion = await s.LegacyCallAsync(sessionId, "completion/complete", new JsonObject
        {
            ["ref"] = new JsonObject { ["type"] = "ref/prompt", ["name"] = "greet" },
            ["argument"] = new JsonObject { ["name"] = "colour", ["value"] = "l" },
        });
        var values = completion["result"]!["completion"]!["values"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        Assert.Equal(["lightGreen", "darkBlue"], values); // prefix matches first, then substring matches

        var templateCompletion = await s.LegacyCallAsync(sessionId, "completion/complete", new JsonObject
        {
            ["ref"] = new JsonObject { ["type"] = "ref/resource", ["uri"] = "test://colour/{colour}/{+rest}" },
            ["argument"] = new JsonObject { ["name"] = "colour", ["value"] = "" },
        });
        Assert.Equal(3, templateCompletion["result"]!["completion"]!["values"]!.AsArray().Count);

        var badRef = await s.LegacyCallAsync(sessionId, "completion/complete", new JsonObject
        {
            ["ref"] = new JsonObject { ["type"] = "ref/prompt", ["name"] = "nope" },
            ["argument"] = new JsonObject { ["name"] = "x", ["value"] = "" },
        });
        Assert.Equal(-32602, badRef["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task StandaloneStreamDeliversSubscribedUpdatesListChangesAndLogs()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync();
        await s.LegacyCallAsync(sessionId, "resources/subscribe", new JsonObject { ["uri"] = "test://json" });
        await s.LegacyCallAsync(sessionId, "logging/setLevel", new JsonObject { ["level"] = "warning" });
        var badLevel = await s.LegacyCallAsync(sessionId, "logging/setLevel", new JsonObject { ["level"] = "loud" });
        Assert.Equal(-32602, badLevel["error"]!["code"]!.GetValue<int>());

        var get = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        get.Headers.Add("Mcp-Session-Id", sessionId);
        get.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        get.Headers.Add("Accept", "text/event-stream");
        using var response = await s.Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        var priming = await reader.NextAsync();
        Assert.NotNull(priming!.Id);
        Assert.Equal("", priming.Data);

        s.Server.Notifier.ResourceUpdated("test://other");
        s.Server.Notifier.ResourceUpdated("test://json");
        s.Server.Notifier.Log(McpLogLevel.Info, "test", "filtered out");
        s.Server.Notifier.Log(McpLogLevel.Error, "test", new { answer = 42 });
        s.Server.RegisterProvider(new ExtraProvider());

        var updated = await reader.NextAsync();
        Assert.Equal("notifications/resources/updated", updated!.Json!["method"]!.GetValue<string>());
        Assert.Equal("test://json", updated.Json["params"]!["uri"]!.GetValue<string>());
        Assert.NotNull(updated.Id);

        var log = await reader.NextMessageAsync();
        Assert.Equal("notifications/message", log!["method"]!.GetValue<string>());
        Assert.Equal("error", log["params"]!["level"]!.GetValue<string>());
        Assert.Equal(42, log["params"]!["data"]!["answer"]!.GetValue<int>());

        var changed = await reader.NextMessageAsync();
        Assert.Equal("notifications/tools/list_changed", changed!["method"]!.GetValue<string>());

        await s.LegacyCallAsync(sessionId, "resources/unsubscribe", new JsonObject { ["uri"] = "test://json" });
        s.Server.Notifier.ResourceUpdated("test://json");
        s.Server.Notifier.PromptListChanged();
        var next = await reader.NextMessageAsync();
        Assert.Equal("notifications/prompts/list_changed", next!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task StandaloneStreamResumesWithLastEventId()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync("2025-06-18");

        HttpRequestMessage Get(string? lastEventId)
        {
            var get = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
            get.Headers.Add("Mcp-Session-Id", sessionId);
            get.Headers.Add("Accept", "text/event-stream");
            if (lastEventId is not null)
                get.Headers.Add("Last-Event-ID", lastEventId);
            return get;
        }

        string firstId;
        using (var first = await s.Http.SendAsync(Get(null), HttpCompletionOption.ResponseHeadersRead))
        {
            using var reader = new SseReader(await first.Content.ReadAsStreamAsync());
            s.Server.Notifier.ToolListChanged();
            var e = await reader.NextAsync();
            firstId = e!.Id!;
        }

        // Missed while disconnected:
        await Task.Delay(100);
        s.Server.Notifier.PromptListChanged();
        s.Server.Notifier.ResourceListChanged();

        using var resumed = await s.Http.SendAsync(Get(firstId), HttpCompletionOption.ResponseHeadersRead);
        using var r2 = new SseReader(await resumed.Content.ReadAsStreamAsync());
        Assert.Equal("notifications/prompts/list_changed", (await r2.NextMessageAsync())!["method"]!.GetValue<string>());
        Assert.Equal("notifications/resources/list_changed", (await r2.NextMessageAsync())!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetRequiresSessionAndEventStream()
    {
        await using var s = await TestServer.StartAsync();
        var noSession = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        noSession.Headers.Add("Accept", "text/event-stream");
        using (var response = await s.Http.SendAsync(noSession))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var wrongAccept = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        wrongAccept.Headers.Add("Accept", "application/json");
        using (var response = await s.Http.SendAsync(wrongAccept))
            Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);

        var unknown = new HttpRequestMessage(HttpMethod.Get, s.Endpoint);
        unknown.Headers.Add("Accept", "text/event-stream");
        unknown.Headers.Add("Mcp-Session-Id", "ffffffffffffffffffffffffffffffff");
        using (var response = await s.Http.SendAsync(unknown))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelledNotificationCancelsInFlightCall()
    {
        await using var s = await TestServer.StartAsync();
        SlowProvider.Reset();
        var sessionId = await s.InitializeAsync();
        var call = s.Http.SendAsync(s.Post(Rpc(77, "tools/call", new JsonObject { ["name"] = "wait_for_cancel" }), sessionId, "2025-11-25"), HttpCompletionOption.ResponseHeadersRead);
        await SlowProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (var cancel = await s.Http.SendAsync(s.Post(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject { ["requestId"] = 77, ["reason"] = "test" },
        }, sessionId, "2025-11-25")))
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        Assert.True(await SlowProvider.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        using var response = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"result\"", body);
        Assert.Contains(s.Server.GetActivity(10), a => a.Target == "wait_for_cancel" && a.Error!.Contains("cancelled"));
    }

    [Fact]
    public async Task ProgressAndLogsSwitchToEventStream()
    {
        await using var s = await TestServer.StartAsync();
        var sessionId = await s.InitializeAsync();
        await s.LegacyCallAsync(sessionId, "logging/setLevel", new JsonObject { ["level"] = "warning" });
        var body = Rpc(5, "tools/call", new JsonObject
        {
            ["name"] = "progress",
            ["arguments"] = new JsonObject { ["steps"] = 3 },
            ["_meta"] = new JsonObject { ["progressToken"] = "tok-1" },
        });
        using var response = await s.Http.SendAsync(s.Post(body, sessionId, "2025-11-25"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var (rpc, notifications) = await TestServer.ReadRpcAsync(response);
        Assert.Equal(3, rpc["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        var progress = notifications.Where(n => n["method"]!.GetValue<string>() == "notifications/progress").ToArray();
        Assert.Equal([1d, 2d, 3d], progress.Select(p => p["params"]!["progress"]!.GetValue<double>()));
        Assert.All(progress, p => Assert.Equal("tok-1", p["params"]!["progressToken"]!.GetValue<string>()));
        Assert.Equal(3, progress[0]["params"]!["total"]!.GetValue<double>());
        var logs = notifications.Where(n => n["method"]!.GetValue<string>() == "notifications/message").ToArray();
        Assert.Equal(3, logs.Length);
        Assert.All(logs, l => Assert.Equal("warning", l["params"]!["level"]!.GetValue<string>()));

        // Without a progress token or SSE acceptance the same call is a plain JSON response.
        using var plain = await s.Http.SendAsync(s.Post(Rpc(6, "tools/call", new JsonObject { ["name"] = "progress", ["arguments"] = new JsonObject { ["steps"] = 1 } }), sessionId, "2025-11-25", accept: "application/json"));
        Assert.Equal("application/json", plain.Content.Headers.ContentType!.MediaType);
    }

    [McpProvider("extra")]
    public sealed class ExtraProvider
    {
        [McpTool("extra_tool", Description = "Registered late.", GameThread = false, RequiresLogin = false)]
        public string Extra() => "extra";
    }
}

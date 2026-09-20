using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Core;

namespace XivMcp.Standalone.Tests;

public sealed class StandaloneHostTests
{
    private const string Token = "standalone-test-token";
    private const string ClientSecret = "laptop-secret-token";

    private static async Task<JsonObject> RpcAsync(HttpClient http, Uri endpoint, string method, JsonObject? parameters = null)
    {
        parameters ??= [];
        parameters["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "standalone-test", ["version"] = "1.0" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("Mcp-Method", method);
        if (parameters["name"]?.GetValue<string>() is { } name)
            request.Headers.TryAddWithoutValidation("Mcp-Name", name);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = text.Split('\n').Last(l => l.StartsWith("data:", StringComparison.Ordinal))[5..];
        return (JsonObject)JsonNode.Parse(text)!;
    }

    private static async Task<(StandaloneHost Host, Uri Endpoint, HttpClient Http)> StartAsync()
    {
        var port = HandoffEndToEndTests.FreePort();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientSecret)));
        var host = new StandaloneHost(new StandaloneHostOptions
        {
            Settings = new SharedSettings { Port = port, BearerToken = Token, ClientTokens = [new ClientToken("laptop", hash)] },
            SqpackPath = null,
        });
        await host.Coordinator.StartAsync();
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return (host, new Uri($"http://127.0.0.1:{port}/mcp"), http);
    }

    [Fact]
    public async Task ListsEveryCatalogueToolAndAnswersLiveOnesWithGameNotRunning()
    {
        var (host, endpoint, http) = await StartAsync();
        await using var hostScope = host;
        using var httpScope = http;

        var catalogue = LiveToolStubs.LoadCatalogue()["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await RpcAsync(http, endpoint, "tools/list", cursor is null ? null : new JsonObject { ["cursor"] = cursor });
            foreach (var tool in page["result"]!["tools"]!.AsArray())
                listed.Add(tool!["name"]!.GetValue<string>());
            cursor = page["result"]!["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        Assert.Empty(catalogue.Except(listed));
        Assert.Contains("get_server_info", listed);

        if (catalogue.Contains("get_player"))
        {
            var result = (await RpcAsync(http, endpoint, "tools/call", new JsonObject { ["name"] = "get_player" }))["result"]!;
            Assert.True(result["isError"]!.GetValue<bool>());
            var error = result["_meta"]!["dev.xivmcp/error"]!;
            Assert.Equal(McpErrorCodes.GameNotRunning, error["code"]!.GetValue<string>());
            Assert.True(error["retryable"]!.GetValue<bool>());
            Assert.Contains("not running", result["content"]![0]!["text"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ServerInfoSaysStandaloneAndNothingCanChange()
    {
        var (host, endpoint, http) = await StartAsync();
        await using var hostScope = host;
        using var httpScope = http;

        var info = (await RpcAsync(http, endpoint, "tools/call", new JsonObject { ["name"] = "get_server_info" }))["result"]!["structuredContent"]!;
        Assert.Equal("standalone", info["host"]!.GetValue<string>());
        Assert.False(info["gameRunning"]!.GetValue<bool>());
        Assert.False(info["loggedIn"]!.GetValue<bool>());
        Assert.False(info["permissions"]!["action"]!.GetValue<bool>());
        Assert.Equal(McpServer.CatalogueVersion, info["catalogueVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task UsesTheSameTokenModelAsThePlugin()
    {
        var (host, endpoint, http) = await StartAsync();
        await using var hostScope = host;
        using var httpScope = http;

        using var anonymous = new HttpClient();
        using var refused = await anonymous.PostAsync(endpoint, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var perClient = new HttpClient();
        perClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ClientSecret);
        var listed = await RpcAsync(perClient, endpoint, "tools/list");
        Assert.NotNull(listed["result"]);
    }

    /// <summary>
    /// The contract that lets a client not care which host is up: every tool this host serves itself has exactly the
    /// arguments, result schema, tier and hints the plugin's catalogue (docs/tools.json) publishes for that name.
    /// </summary>
    [Fact]
    public void ServedToolsMatchThePluginCatalogueExactly()
    {
        var catalogue = LiveToolStubs.LoadCatalogue()["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!, StringComparer.Ordinal);
        if (catalogue.Count == 0)
            return; // placeholder catalogue before the first generation

        var server = new McpServer(new McpServerOptions(), new NoGame(), new Open());
        foreach (var type in typeof(StandaloneHost).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<McpProviderAttribute>() is not null))
            server.RegisterProvider(RuntimeHelpers.GetUninitializedObject(type));

        var served = server.ExportCatalogue()["tools"]!.AsArray();
        Assert.NotEmpty(served);
        foreach (var tool in served)
        {
            var name = tool!["name"]!.GetValue<string>();
            Assert.True(catalogue.TryGetValue(name, out var published), $"'{name}' is served by the standalone host but is not in docs/tools.json");
            Assert.Equal("static", published!["availability"]!.GetValue<string>());
            foreach (var key in new[] { "inputSchema", "outputSchema", "permission", "category", "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
                Assert.True(JsonNode.DeepEquals(published[key], tool[key]), $"'{name}': {key} differs between the standalone host and docs/tools.json");
        }

        // And nothing marked static is missing here.
        var servedNames = served.Select(t => t!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var missing = catalogue.Values.Where(t => t["availability"]!.GetValue<string>() == "static").Select(t => t["name"]!.GetValue<string>()).Where(n => !servedNames.Contains(n)).ToArray();
        Assert.True(missing.Length == 0, "marked availability=static in the plugin but not served by the standalone host: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task StubsCopyTheCatalogueEntry()
    {
        var catalogue = new JsonObject
        {
            ["tools"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "send_chat",
                    ["title"] = "Send chat",
                    ["description"] = "d",
                    ["category"] = "chat",
                    ["permission"] = "chat",
                    ["availability"] = "live",
                    ["needsApproval"] = true,
                    ["approvalSummary"] = "Send {message}",
                    ["destructiveHint"] = false,
                    ["idempotentHint"] = false,
                    ["openWorldHint"] = true,
                    ["dataSources"] = new JsonArray("client:chat"),
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("message") },
                },
                new JsonObject { ["name"] = "get_item", ["permission"] = "read", ["availability"] = "static" },
                new JsonObject { ["name"] = "served_here", ["permission"] = "read", ["availability"] = "static" }),
        };

        var stubs = LiveToolStubs.Build(catalogue, new HashSet<string> { "served_here" }, gameDataMissing: true);
        Assert.Equal(["send_chat", "get_item"], stubs.Select(s => s.Name));
        var chat = stubs[0];
        Assert.Equal((ToolPermission.Chat, true, false, "chat", "Send {message}"), (chat.Permission, chat.OpenWorld, chat.Idempotent, chat.Category, chat.ApprovalSummary));
        var live = await Assert.ThrowsAsync<McpToolException>(() => chat.Handler(null, null!));
        Assert.Equal(McpErrorCodes.GameNotRunning, live.Code);
        var noData = await Assert.ThrowsAsync<McpToolException>(() => stubs[1].Handler(null, null!));
        Assert.Equal(McpErrorCodes.Unavailable, noData.Code);
    }

    private sealed class NoGame : IGameThread
    {
        public bool IsOnGameThread => true;

        public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default) => Task.FromResult(func());

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Open : IHostState
    {
        public bool IsLoggedIn => false;

        public bool IsPermitted(ToolPermission permission) => true;

        public bool IsCategoryEnabled(string category) => true;
    }
}

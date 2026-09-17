using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Core.Http;

namespace XivMcp.Core.Tests.Infrastructure;

public sealed record SseEvent(string? Id, string? Event, string Data)
{
    public JsonObject? Json => string.IsNullOrWhiteSpace(Data) ? null : JsonNode.Parse(Data) as JsonObject;
}

public sealed class SseReader : IDisposable
{
    private readonly StreamReader _reader;

    public SseReader(Stream stream) => _reader = new StreamReader(stream, Encoding.UTF8);

    public int CommentCount { get; private set; }

    public async Task<SseEvent?> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        string? id = null, evt = null;
        StringBuilder? data = null;
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("no SSE event within timeout");
            }

            if (line is null)
                return null;
            if (line.Length == 0)
            {
                if (data is not null)
                    return new SseEvent(id, evt, data.ToString());
                id = evt = null;
                continue;
            }

            if (line.StartsWith(':'))
            {
                CommentCount++;
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            switch (field)
            {
                case "id": id = value; break;
                case "event": evt = value; break;
                case "data":
                    data = data is null ? new StringBuilder(value) : data.Append('\n').Append(value);
                    break;
            }
        }
    }

    /// <summary>Next event that carries a JSON-RPC message (skips priming events).</summary>
    public async Task<JsonObject?> NextMessageAsync(TimeSpan? timeout = null)
    {
        while (true)
        {
            var e = await NextAsync(timeout);
            if (e is null)
                return null;
            if (e.Json is { } json)
                return json;
        }
    }

    public void Dispose() => _reader.Dispose();
}

public sealed class TestServer : IAsyncDisposable
{
    public const string Token = "test-token-123";
    public const string Modern = "2026-07-28";

    private int _nextId = 1000;

    private TestServer(McpServer server, FakeGameThread game, FakeHostState host)
    {
        Server = server;
        Game = game;
        Host = host;
        Http = new HttpClient(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public McpServer Server { get; }

    public FakeGameThread Game { get; }

    public FakeHostState Host { get; }

    public HttpClient Http { get; }

    public List<string> Logs { get; } = [];

    public int Port => Server.ListeningPort;

    public Uri Endpoint => new($"http://127.0.0.1:{Port}/mcp");

    public int NextId() => Interlocked.Increment(ref _nextId);

    internal static async Task<TestServer> StartAsync(Action<McpServerOptions>? configure = null, bool registerDefaults = true, HttpLimits? limits = null, string? token = Token)
    {
        var options = new McpServerOptions { Port = 0, BearerToken = token, ServerVersion = "9.9.9", Instructions = "test instructions" };
        configure?.Invoke(options);
        var game = new FakeGameThread();
        var host = new FakeHostState { GameThread = game };
        TestServer? self = null;
        var server = new McpServer(options, game, host, (m, e) =>
        {
            lock (self!.Logs)
                self.Logs.Add(e is null ? m : $"{m}: {e}");
        });
        self = new TestServer(server, game, host);
        server.HttpLimitsOverride = limits;
        if (registerDefaults)
        {
            server.RegisterProvider(new BasicProvider(game));
            server.RegisterProvider(new SlowProvider());
            server.RegisterProvider(new GatedProvider());
            server.RegisterProvider(new DataProvider());
        }

        await server.StartAsync();
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Server.DisposeAsync();
        Game.Dispose();
    }

    // ---- raw requests -----------------------------------------------------------------------------

    public HttpRequestMessage Post(JsonNode body, string? sessionId = null, string? protocolVersion = null, string accept = "application/json, text/event-stream")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (sessionId is not null)
            request.Headers.Add("Mcp-Session-Id", sessionId);
        if (protocolVersion is not null)
            request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        return request;
    }

    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text)!;
    }

    /// <summary>Reads the JSON-RPC response from a JSON or SSE body; notifications before it are collected.</summary>
    public static async Task<(JsonObject Response, List<JsonObject> Notifications)> ReadRpcAsync(HttpResponseMessage response, TimeSpan? timeout = null)
    {
        var notifications = new List<JsonObject>();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new SseReader(await response.Content.ReadAsStreamAsync());
            while (true)
            {
                var message = await reader.NextMessageAsync(timeout) ?? throw new InvalidOperationException("SSE stream ended without a response");
                if (message.ContainsKey("id") && (message.ContainsKey("result") || message.ContainsKey("error")))
                    return (message, notifications);
                notifications.Add(message);
            }
        }

        return ((JsonObject)await ReadJsonAsync(response), notifications);
    }

    // ---- legacy -----------------------------------------------------------------------------------

    public async Task<string> InitializeAsync(string version = "2025-11-25", string clientName = "test-client")
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = NextId(),
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = "1.0" },
            },
        };
        using var response = await Http.SendAsync(Post(body));
        response.EnsureSuccessStatusCode();
        var sessionId = response.Headers.GetValues("Mcp-Session-Id").Single();
        var negotiated = (await ReadJsonAsync(response))["result"]!["protocolVersion"]!.GetValue<string>();
        using var initialized = await Http.SendAsync(Post(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, sessionId, negotiated));
        Assert.Equal(HttpStatusCode.Accepted, initialized.StatusCode);
        return sessionId;
    }

    public async Task<JsonObject> LegacyCallAsync(string sessionId, string method, JsonObject? parameters = null, string version = "2025-11-25", int? id = null)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id ?? NextId(), ["method"] = method };
        if (parameters is not null)
            body["params"] = parameters;
        using var response = await Http.SendAsync(Post(body, sessionId, version));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadRpcAsync(response)).Response;
    }

    // ---- modern -----------------------------------------------------------------------------------

    public static JsonObject ModernMeta(JsonObject? extra = null)
    {
        var meta = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = Modern,
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "modern-test", ["version"] = "2.0" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        if (extra is not null)
        {
            foreach (var kv in extra)
                meta[kv.Key] = kv.Value?.DeepClone();
        }

        return meta;
    }

    public HttpRequestMessage ModernPost(string method, JsonObject? parameters = null, JsonObject? extraMeta = null, int? id = null, string accept = "application/json, text/event-stream")
    {
        var p = parameters?.DeepClone() as JsonObject ?? new JsonObject();
        p["_meta"] = ModernMeta(extraMeta);
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id ?? NextId(), ["method"] = method, ["params"] = p };
        var request = Post(body, protocolVersion: Modern, accept: accept);
        request.Headers.Add("Mcp-Method", method);
        var name = method switch
        {
            "tools/call" or "prompts/get" => p["name"]?.GetValue<string>(),
            "resources/read" => p["uri"]?.GetValue<string>(),
            _ => null,
        };
        if (name is not null)
            request.Headers.Add("Mcp-Name", name);
        return request;
    }

    public async Task<(HttpStatusCode Status, JsonObject Response, List<JsonObject> Notifications)> ModernCallAsync(string method, JsonObject? parameters = null, JsonObject? extraMeta = null)
    {
        using var response = await Http.SendAsync(ModernPost(method, parameters, extraMeta), HttpCompletionOption.ResponseHeadersRead);
        var (rpc, notes) = await ReadRpcAsync(response);
        return (response.StatusCode, rpc, notes);
    }

    // ---- raw sockets ------------------------------------------------------------------------------

    public async Task<string> RawAsync(string request, TimeSpan? readFor = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.Latin1.GetBytes(request));
        return await ReadAllAsync(stream, readFor ?? TimeSpan.FromSeconds(2));
    }

    public static async Task<string> ReadAllAsync(NetworkStream stream, TimeSpan duration)
    {
        var sb = new StringBuilder();
        var buffer = new byte[65536];
        using var cts = new CancellationTokenSource(duration);
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, cts.Token);
                if (n == 0)
                    break;
                sb.Append(Encoding.Latin1.GetString(buffer, 0, n));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        return sb.ToString();
    }

    public string AuthHeader => $"Authorization: Bearer {Token}\r\n";
}

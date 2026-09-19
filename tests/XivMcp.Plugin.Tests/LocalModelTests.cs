using System.Net;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using XivMcp.Plugin.Ipc;
using XivMcp.Plugin.Services;
using XivMcp.Shared;

namespace XivMcp.Plugin.Tests;

public sealed class LocalModelProbeTests
{
    /// <summary>Answers from a url → (status, body) table; unknown urls refuse the connection. Records every request.</summary>
    private sealed class FakeHandler(Dictionary<string, (HttpStatusCode Status, string Body)> routes) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? Body)> Requests { get; } = [];

        public TimeSpan Delay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            if (!routes.TryGetValue($"{request.Method} {url}", out var route))
                throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
            return new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(" http://127.0.0.1:11434/v1/ ", "http://127.0.0.1:11434/v1")]
    [InlineData("https://models.lan/v1", "https://models.lan/v1")]
    [InlineData("http://localhost:1234/v1//", "http://localhost:1234/v1")]
    [InlineData("127.0.0.1:11434/v1", null)]
    [InlineData("ftp://127.0.0.1/v1", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("/v1", null)]
    [InlineData("http://127.0.0.1:1234/v1?x=1", null)]
    [InlineData("http://user:pw@127.0.0.1:1234/v1", null)]
    public void NormalizeEndpoint(string input, string? expected) => Assert.Equal(expected, LocalModelProbe.NormalizeEndpoint(input));

    [Fact]
    public void ParsesOpenAiAndOllamaModelLists()
    {
        Assert.Equal(["llama3.2:3b", "qwen2.5:7b"], LocalModelProbe.ParseModels("""{"object":"list","data":[{"id":"llama3.2:3b"},{"id":"qwen2.5:7b"},{"id":"llama3.2:3b"},{"x":1}]}"""));
        Assert.Equal(["mistral:latest"], LocalModelProbe.ParseModels("""{"models":[{"name":"mistral:latest","model":"mistral:latest"}]}"""));
        Assert.Equal(["m"], LocalModelProbe.ParseModels("""{"models":[{"model":"m"}]}"""));
        Assert.Empty(LocalModelProbe.ParseModels("""{"data":[]}""")!);
        Assert.Null(LocalModelProbe.ParseModels("<html>nope</html>"));
        Assert.Null(LocalModelProbe.ParseModels("""{"error":"x"}"""));
        Assert.Null(LocalModelProbe.ParseModels("[]"));
        Assert.Null(LocalModelProbe.ParseModels(null));
    }

    [Fact]
    public void ParsesCompletion()
    {
        Assert.Equal("OK", LocalModelProbe.ParseCompletion("""{"choices":[{"message":{"role":"assistant","content":"OK"}}]}"""));
        Assert.Equal("OK", LocalModelProbe.ParseCompletion("""{"choices":[{"text":"OK"}]}"""));
        Assert.Null(LocalModelProbe.ParseCompletion("""{"choices":[]}"""));
        Assert.Null(LocalModelProbe.ParseCompletion("not json"));
    }

    [Fact]
    public async Task DetectFindsAnsweringServersInOrder()
    {
        var handler = new FakeHandler(new()
        {
            ["GET http://127.0.0.1:1234/v1/models"] = (HttpStatusCode.OK, """{"data":[{"id":"lmstudio-model"}]}"""),
            // Ollama without the OpenAI layer: /v1/models 404s, /api/tags answers.
            ["GET http://127.0.0.1:11434/v1/models"] = (HttpStatusCode.NotFound, "404 page not found"),
            ["GET http://127.0.0.1:11434/api/tags"] = (HttpStatusCode.OK, """{"models":[{"name":"llama3.2:3b"}]}"""),
            ["GET http://127.0.0.1:8080/v1/models"] = (HttpStatusCode.OK, "<html>some other web app</html>"),
        });
        using var http = new HttpClient(handler);
        var found = await new LocalModelProbe(http).DetectAsync(null, Short, TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);
        Assert.Equal("http://127.0.0.1:11434/v1", found[0].BaseUrl);
        Assert.Equal(["llama3.2:3b"], found[0].Models);
        Assert.Equal("http://127.0.0.1:1234/v1", found[1].BaseUrl);
        Assert.Equal(["lmstudio-model"], found[1].Models);
        Assert.All(handler.Requests, r => Assert.Null(r.Auth));
        Assert.Contains(handler.Requests, r => r.Url == "http://127.0.0.1:5001/v1/models");
    }

    [Fact]
    public async Task DetectTimesOutQuickly()
    {
        var handler = new FakeHandler(new() { ["GET http://127.0.0.1:11434/v1/models"] = (HttpStatusCode.OK, """{"data":[]}""") })
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        using var http = new HttpClient(handler);
        var started = DateTime.UtcNow;
        var found = await new LocalModelProbe(http).DetectAsync(null, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        Assert.Empty(found);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TestListsThenCompletesWithTheKey()
    {
        var handler = new FakeHandler(new()
        {
            ["GET http://127.0.0.1:8080/v1/models"] = (HttpStatusCode.OK, """{"data":[{"id":"phi"}]}"""),
            ["POST http://127.0.0.1:8080/v1/chat/completions"] = (HttpStatusCode.OK, """{"choices":[{"message":{"content":" OK\n"}}]}"""),
        });
        using var http = new HttpClient(handler);
        var result = await new LocalModelProbe(http).TestAsync("http://127.0.0.1:8080/v1/", "phi", "sekrit-key", Short, Short, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.LatencyMs);
        Assert.DoesNotContain("sekrit", result.Message);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer sekrit-key", r.Auth));
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal("phi", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(8, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("Reply with OK", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TestReportsMissingModelWithoutPosting()
    {
        var handler = new FakeHandler(new() { ["GET http://127.0.0.1:1234/v1/models"] = (HttpStatusCode.OK, """{"data":[{"id":"a"},{"id":"b"}]}""") });
        using var http = new HttpClient(handler);
        var result = await new LocalModelProbe(http).TestAsync("http://127.0.0.1:1234/v1", "c", null, Short, Short, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("'c' is not listed", result.Message);
        Assert.Contains("a, b", result.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TestReportsServerErrorsAndRefusals()
    {
        var handler = new FakeHandler(new()
        {
            ["GET http://127.0.0.1:1234/v1/models"] = (HttpStatusCode.OK, """{"data":[{"id":"a"}]}"""),
            ["POST http://127.0.0.1:1234/v1/chat/completions"] = (HttpStatusCode.InternalServerError, """{"error":"model failed to load"}"""),
        });
        using var http = new HttpClient(handler);
        var probe = new LocalModelProbe(http);
        var ct = TestContext.Current.CancellationToken;

        var failed = await probe.TestAsync("http://127.0.0.1:1234/v1", "a", null, Short, Short, ct);
        Assert.False(failed.Ok);
        Assert.Contains("HTTP 500", failed.Message);
        Assert.Contains("model failed to load", failed.Message);

        var refused = await probe.TestAsync("http://127.0.0.1:9/v1", "a", null, Short, Short, ct);
        Assert.False(refused.Ok);
        Assert.Contains("connection refused", refused.Message);

        Assert.False((await probe.TestAsync("nope", "a", null, Short, Short, ct)).Ok);
        Assert.False((await probe.TestAsync("http://127.0.0.1:1234/v1", " ", null, Short, Short, ct)).Ok);
    }
}

public sealed class LocalModelConfigTests
{
    [Fact]
    public void DefaultsAreUnconfiguredAndAllowIpcTokens()
    {
        var config = new Configuration();
        config.Normalize();
        Assert.Equal("", config.LocalModelEndpoint);
        Assert.Equal("", config.LocalModelName);
        Assert.Equal("", config.LocalModelApiKey);
        Assert.True(config.AllowIpcClientTokens);
    }

    [Fact]
    public void NormalizeTrimsAndDropsBadEndpoints()
    {
        var config = new Configuration { BearerToken = "t", LocalModelEndpoint = " http://127.0.0.1:11434/v1/ ", LocalModelName = " llama3.2:3b ", LocalModelApiKey = " k " };
        Assert.True(config.Normalize());
        Assert.Equal("http://127.0.0.1:11434/v1", config.LocalModelEndpoint);
        Assert.Equal("llama3.2:3b", config.LocalModelName);
        Assert.Equal("k", config.LocalModelApiKey);
        Assert.False(config.Normalize());

        config.LocalModelEndpoint = "ftp://example/v1";
        Assert.True(config.Normalize());
        Assert.Equal("", config.LocalModelEndpoint);

        config.LocalModelName = null!;
        config.LocalModelApiKey = null!;
        Assert.True(config.Normalize());
        Assert.Equal("", config.LocalModelName);
        Assert.Equal("", config.LocalModelApiKey);
    }

    [Fact]
    public void OldFilesLoadWithDefaults()
    {
        var config = JsonConvert.DeserializeObject<Configuration>("""{"Version":2,"BearerToken":"KEEP_THIS_TOKEN_abcdefghijklmnopqrstuvwxyz0"}""")!;
        Assert.False(config.Normalize());
        Assert.Equal("", config.LocalModelEndpoint);
        Assert.True(config.AllowIpcClientTokens);
        Assert.Equal(Configuration.CurrentVersion, config.Version);
    }
}

public sealed class ClientConnectorTests
{
    [Fact]
    public void IssuesATokenMarkedAsIpc()
    {
        var config = new Configuration();
        var result = ClientConnector.Connect(config, " almanac ", DateTimeOffset.UnixEpoch);

        Assert.True(result.Ok);
        Assert.Equal("almanac", result.ClientName);
        Assert.Equal(43, result.Token!.Length);
        var entry = Assert.Single(config.ClientTokens);
        Assert.Equal("almanac", entry.Name);
        Assert.Equal(ClientConnector.CreatedViaIpc, entry.CreatedVia);
        Assert.Equal(Configuration.HashToken(result.Token), entry.TokenSha256);
        Assert.Equal("almanac", Assert.Single(config.ClientTokenHashes()).Name);
    }

    [Fact]
    public void ReconnectReplacesTheTokenAndKeepsRulesAndSpelling()
    {
        var config = new Configuration();
        var manual = config.AddClientToken("Ghostty", DateTimeOffset.UnixEpoch);
        config.AddClientToken("other", DateTimeOffset.UnixEpoch);
        config.AutoApproveRules = [new AutoApproveRule { Client = "Ghostty", Tool = "execute_command", Prefixes = ["/echo"] }];

        var first = ClientConnector.Connect(config, "ghostty", DateTimeOffset.UnixEpoch);
        var second = ClientConnector.Connect(config, "GHOSTTY", DateTimeOffset.UnixEpoch);

        Assert.Equal("Ghostty", first.ClientName);
        Assert.Equal("Ghostty", second.ClientName);
        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(manual, second.Token);
        Assert.Equal(2, config.ClientTokens.Count);
        var ghostty = Assert.Single(config.ClientTokens, t => t.Name == "Ghostty");
        Assert.Equal(Configuration.HashToken(second.Token!), ghostty.TokenSha256);
        Assert.Single(config.AutoApproveRules, r => r.Client == "Ghostty");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("semi;colon")]
    public void RejectsInvalidNames(string? name)
    {
        var config = new Configuration();
        var result = ClientConnector.Connect(config, name, DateTimeOffset.UnixEpoch);
        Assert.Equal(ClientConnector.ErrorInvalidName, result.Error);
        Assert.Null(result.Token);
        Assert.Empty(config.ClientTokens);
    }

    [Fact]
    public void DisabledIssuesNothing()
    {
        var config = new Configuration { AllowIpcClientTokens = false };
        var result = ClientConnector.Connect(config, "almanac", DateTimeOffset.UnixEpoch);
        Assert.Equal(ClientConnector.ErrorDisabled, result.Error);
        Assert.Empty(config.ClientTokens);
    }
}

public sealed class LocalModelIpcJsonTests
{
    [Fact]
    public void LocalModelNeverContainsTheKey()
    {
        using var json = JsonDocument.Parse(IpcJson.LocalModel("http://127.0.0.1:11434/v1", "llama3.2:3b", "sekrit-key"));
        var root = json.RootElement;
        Assert.True(root.GetProperty("configured").GetBoolean());
        Assert.Equal("http://127.0.0.1:11434/v1", root.GetProperty("endpoint").GetString());
        Assert.Equal("llama3.2:3b", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(["configured", "endpoint", "model", "hasApiKey"], root.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("sekrit", json.RootElement.GetRawText());
    }

    [Fact]
    public void UnconfiguredLocalModelIsNulls()
    {
        Assert.Equal("""{"configured":false,"endpoint":null,"model":null,"hasApiKey":false}""", IpcJson.LocalModel("", " ", null));
        Assert.Equal("""{"configured":false,"endpoint":"http://x/v1","model":null,"hasApiKey":false}""", IpcJson.LocalModel("http://x/v1", "", ""));
    }

    [Fact]
    public void ConnectAndErrorShapes()
    {
        Assert.Equal("""{"endpoint":"http://127.0.0.1:41800/mcp","token":"T","clientName":"almanac"}""", IpcJson.Connect("http://127.0.0.1:41800/mcp", "T", "almanac"));
        Assert.Equal("""{"error":"disabled"}""", IpcJson.Error(ClientConnector.ErrorDisabled));
    }

    [Fact]
    public void ContractRevision()
    {
        Assert.Equal(1, IpcContract.Version);
        Assert.Equal(2, IpcContract.Revision);
        Assert.Equal("XivMcp.ApiRevision", IpcContract.ApiRevision);
        Assert.Equal("XivMcp.GetLocalModel", IpcContract.GetLocalModel);
        Assert.Equal("XivMcp.ConnectClient", IpcContract.ConnectClient);
        Assert.Equal("XivMcp.LocalModelChanged", IpcContract.LocalModelChanged);
    }
}

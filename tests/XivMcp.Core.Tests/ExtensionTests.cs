using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

[McpProvider("ext")]
public sealed class ExtensionProvider
{
    public int Runs;

    [McpTool("ext_static", Description = "Static data.", GameThread = false, RequiresLogin = false, Availability = ToolAvailability.Static, Sources = ["lumina:Item"])]
    public string Static() => "static";

    [McpTool("ext_flag", Description = "Ui tier that asks.", Permission = ToolPermission.Ui, RequiresApproval = true, GameThread = false, RequiresLogin = false,
        ApprovalSummary = "Place the map flag in {zone} at {x}")]
    public string Flag(string zone, double x = 1)
    {
        Interlocked.Increment(ref Runs);
        return zone;
    }

    [McpTool("ext_coded", Description = "Fails with a code.", GameThread = false, RequiresLogin = false)]
    public string Coded() => throw McpToolException.WithCode(McpErrorCodes.NotFound, "No such thing.", retryable: false);
}

public sealed class SummaryApprover : ISessionAwareToolCallApprover
{
    public readonly ConcurrentQueue<ToolCallApprovalRequest> Calls = new();

    public bool Answer { get; set; } = true;

    public Task<bool> ApproveToolCallAsync(string toolName, ToolPermission permission, string? clientName, string? argumentsJson, CancellationToken cancellationToken) =>
        Task.FromResult(Answer);

    public Task<bool> ApproveToolCallAsync(ToolCallApprovalRequest call, CancellationToken cancellationToken)
    {
        Calls.Enqueue(call);
        return Task.FromResult(Answer);
    }
}

public class ExtensionTests
{
    private static async Task<JsonObject> CallAsync(TestServer s, string name, JsonObject? arguments = null)
    {
        var p = new JsonObject { ["name"] = name };
        if (arguments is not null)
            p["arguments"] = arguments;
        var (status, response, _) = await s.ModernCallAsync("tools/call", p);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(response["error"]);
        return response["result"]!.AsObject();
    }

    private static string? ErrorCode(JsonObject result) => result["_meta"]?["dev.xivmcp/error"]?["code"]?.GetValue<string>();

    [Fact]
    public async Task ToolListCarriesAvailabilitySourcesAndApprovalMeta()
    {
        await using var s = await TestServer.StartAsync();
        s.Server.RegisterProvider(new ExtensionProvider());
        var (_, response, _) = await s.ModernCallAsync("tools/list", new JsonObject());
        var tools = response["result"]!["tools"]!.AsArray();
        var statics = tools.Single(t => t!["name"]!.GetValue<string>() == "ext_static")!;
        Assert.Equal("static", statics["_meta"]!["dev.xivmcp/availability"]!.GetValue<string>());
        Assert.Equal("lumina:Item", statics["_meta"]!["dev.xivmcp/dataSources"]![0]!.GetValue<string>());
        Assert.False(statics["_meta"]!["dev.xivmcp/needsApproval"]!.GetValue<bool>());
        Assert.True(statics["annotations"]!["readOnlyHint"]!.GetValue<bool>());

        var flag = tools.Single(t => t!["name"]!.GetValue<string>() == "ext_flag")!;
        Assert.True(flag["_meta"]!["dev.xivmcp/needsApproval"]!.GetValue<bool>());
        Assert.False(flag["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UiToolThatAsksGoesThroughTheApproverWithARenderedSummary()
    {
        await using var s = await TestServer.StartAsync();
        var provider = new ExtensionProvider();
        s.Server.RegisterProvider(provider);
        var approver = new SummaryApprover();
        s.Server.Approver = approver;
        var executed = new ConcurrentQueue<GatedToolExecution>();
        s.Server.GatedToolExecuted += executed.Enqueue;

        var ok = await CallAsync(s, "ext_flag", new JsonObject { ["zone"] = "Limsa‮" });
        Assert.Null(ok["isError"]);
        var call = Assert.Single(approver.Calls);
        Assert.Equal("Place the map flag in Limsa\\u202E at (default)", call.Summary);
        var record = Assert.Single(executed);
        Assert.Equal(("ext_flag", true, false), (record.ToolName, record.Success, record.PreApproved));
        Assert.Equal(call.Summary, record.Summary);

        approver.Answer = false;
        var denied = await CallAsync(s, "ext_flag", new JsonObject { ["zone"] = "x" });
        Assert.Equal(McpErrorCodes.Denied, ErrorCode(denied));
        Assert.Equal(1, provider.Runs);
        Assert.Single(executed);
    }

    [Fact]
    public async Task ErrorsCarryStableCodes()
    {
        await using var s = await TestServer.StartAsync();
        s.Server.RegisterProvider(new ExtensionProvider());
        Assert.Equal(McpErrorCodes.NotFound, ErrorCode(await CallAsync(s, "ext_coded")));
        Assert.Equal(McpErrorCodes.InvalidArguments, ErrorCode(await CallAsync(s, "ext_flag", new JsonObject())));
        s.Host.DisabledCategories["ext"] = true;
        Assert.Equal(McpErrorCodes.CategoryDisabled, ErrorCode(await CallAsync(s, "ext_static")));
    }

    [Fact]
    public async Task RateLimitRejectsWithRetryAfterAndRefills()
    {
        var clock = new ManualClock();
        await using var s = await TestServer.StartAsync(o =>
        {
            o.RateLimitPerMinute = 60;
            o.RateLimitBurst = 2;
        });
        s.Server.Clock = clock;
        s.Server.RegisterProvider(new ExtensionProvider());

        Assert.Null((await CallAsync(s, "ext_static"))["isError"]);
        Assert.Null((await CallAsync(s, "ext_static"))["isError"]);
        var limited = await CallAsync(s, "ext_static");
        Assert.Equal(McpErrorCodes.RateLimited, ErrorCode(limited));
        Assert.True(limited["_meta"]!["dev.xivmcp/error"]!["retryAfterSeconds"]!.GetValue<double>() > 0);

        clock.Advance(TimeSpan.FromSeconds(1.1));
        Assert.Null((await CallAsync(s, "ext_static"))["isError"]);
    }

    [Fact]
    public async Task ExternalToolsListAndAnswerUnderTheirOwnSchema()
    {
        await using var s = await TestServer.StartAsync();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } } };
        s.Server.RegisterExternalTools(
        [
            new ExternalTool
            {
                Name = "get_player",
                Description = "Needs the game.",
                Category = "character",
                InputSchema = schema,
                Handler = static (_, _) => throw McpToolException.WithCode(McpErrorCodes.GameNotRunning, "The game is not running.", retryable: true),
            },
        ]);
        Assert.Throws<ArgumentException>(() => s.Server.RegisterExternalTools([new ExternalTool { Name = "get_player", Handler = static (_, _) => Task.FromResult(ToolResult.Text("x")) }]));

        var (_, response, _) = await s.ModernCallAsync("tools/list", new JsonObject());
        var listed = response["result"]!["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "get_player")!;
        Assert.True(JsonNode.DeepEquals(schema, listed["inputSchema"]));

        var result = await CallAsync(s, "get_player", new JsonObject { ["anything"] = 1 });
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(McpErrorCodes.GameNotRunning, ErrorCode(result));
        Assert.True(result["_meta"]!["dev.xivmcp/error"]!["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ControlRoutesNeedTheTokenAndReachTheHandler()
    {
        await using var s = await TestServer.StartAsync();
        s.Server.ControlHandler = static request => Task.FromResult<ControlResponse?>(
            request.SubPath == "host" ? new ControlResponse(200, new JsonObject { ["method"] = request.Method, ["loopback"] = request.FromThisMachine, ["echo"] = request.Body?["a"]?.GetValue<int>() }) : null);

        using var anonymous = new HttpClient();
        using var refused = await anonymous.GetAsync(new Uri(s.Endpoint + "/host"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(s.Endpoint + "/host")) { Content = new StringContent("{\"a\":7}", Encoding.UTF8, "application/json") };
        using var ok = await s.Http.SendAsync(post);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await TestServer.ReadJsonAsync(ok);
        Assert.Equal("POST", body["method"]!.GetValue<string>());
        Assert.True(body["loopback"]!.GetValue<bool>());
        Assert.Equal(7, body["echo"]!.GetValue<int>());

        using var missing = await s.Http.GetAsync(new Uri(s.Endpoint + "/nothing"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CatalogueExportDescribesEveryTool()
    {
        await using var s = await TestServer.StartAsync();
        s.Server.RegisterProvider(new ExtensionProvider());
        var catalogue = s.Server.ExportCatalogue();
        Assert.Equal(McpServer.CatalogueVersion, catalogue["catalogueVersion"]!.GetValue<int>());
        var flag = catalogue["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "ext_flag")!;
        Assert.Equal("ui", flag["permission"]!.GetValue<string>());
        Assert.True(flag["needsApproval"]!.GetValue<bool>());
        Assert.Equal("live", flag["availability"]!.GetValue<string>());
        Assert.NotNull(flag["inputSchema"]!["properties"]!["zone"]);
    }

    [Fact]
    public void SummaryRendering()
    {
        Assert.Equal("Run t with the arguments shown.", McpServer.RenderApprovalSummary(null, "t", null));
        Assert.Equal("Say \"hi\" 3 times", McpServer.RenderApprovalSummary("Say \"{message}\" {count} times", "t", new JsonObject { ["message"] = "hi", ["count"] = 3 }));
        Assert.EndsWith("…", McpServer.RenderApprovalSummary("{m}", "t", new JsonObject { ["m"] = new string('a', 600) }));
    }
}

public sealed class ManualClock : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

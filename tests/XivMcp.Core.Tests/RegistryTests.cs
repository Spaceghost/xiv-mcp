using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using XivMcp.Core.Registry;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public class RegistryTests
{
    public sealed record Inner(int Count, string? Label);

    public sealed record Outer(string Name, Inner Inner, IReadOnlyList<Inner> Many, Dictionary<string, double> Scores, Colour Colour, Guid Id, DateTimeOffset At, byte[] Raw, ulong Big, Outer? Self);

    public sealed class SchemaProvider
    {
        [McpTool("schema_tool", Description = "d")]
        public Outer Tool(
            [McpParam("A name")] string name,
            int count,
            byte small,
            double ratio,
            bool flag,
            Colour colour,
            List<string> tags,
            Colour[] colours,
            Inner inner,
            JsonObject extra,
            int? maybe,
            string? note,
            [McpParam("Limit", Minimum = 1, Maximum = 500)] int limit = 25,
            Colour fallback = Colour.LightGreen,
            [McpParam("Mode", Enum = ["x", "y"])] string mode = "x",
            ToolContext? context = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private static ToolDescriptor Describe<T>(string name) where T : new()
    {
        var registry = new ProviderRegistry();
        registry.Register(new T());
        return registry.Snapshot.ToolsByName[name];
    }

    [Fact]
    public void InputSchemaCoversParameterShapes()
    {
        var tool = Describe<SchemaProvider>("schema_tool");
        var schema = tool.InputSchema;
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var p = schema["properties"]!;
        Assert.Equal(15, p.AsObject().Count);
        Assert.DoesNotContain("context", p.AsObject().Select(kv => kv.Key));
        Assert.DoesNotContain("cancellationToken", p.AsObject().Select(kv => kv.Key));

        Assert.Equal("""{"description":"A name","type":"string"}""", p["name"]!.ToJsonString());
        Assert.Equal("""{"type":"integer"}""", p["count"]!.ToJsonString());
        Assert.Equal("""{"type":"integer","minimum":0,"maximum":255}""", p["small"]!.ToJsonString());
        Assert.Equal("""{"type":"number"}""", p["ratio"]!.ToJsonString());
        Assert.Equal("""{"type":"boolean"}""", p["flag"]!.ToJsonString());
        Assert.Equal("""{"type":"string","enum":["red","darkBlue","lightGreen"]}""", p["colour"]!.ToJsonString());
        Assert.Equal("""{"type":"array","items":{"type":"string"}}""", p["tags"]!.ToJsonString());
        Assert.Equal("""{"type":"array","items":{"type":"string","enum":["red","darkBlue","lightGreen"]}}""", p["colours"]!.ToJsonString());
        Assert.Equal("""{"type":"object","properties":{"count":{"type":"integer"},"label":{"type":"string"}}}""", p["inner"]!.ToJsonString());
        Assert.Equal("""{"type":"object"}""", p["extra"]!.ToJsonString());
        Assert.Equal("""{"type":"integer"}""", p["maybe"]!.ToJsonString());
        Assert.Equal("""{"description":"Limit","type":"integer","minimum":1,"maximum":500,"default":25}""", p["limit"]!.ToJsonString());
        Assert.Equal("""{"type":"string","enum":["red","darkBlue","lightGreen"],"default":"lightGreen"}""", p["fallback"]!.ToJsonString());
        Assert.Equal("""{"description":"Mode","type":"string","enum":["x","y"],"default":"x"}""", p["mode"]!.ToJsonString());

        var required = schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray();
        Assert.Equal(["name", "count", "small", "ratio", "flag", "colour", "tags", "colours", "inner", "extra"], required);
        Assert.Contains("limit: integer [1..500], default 25", tool.Signature);
    }

    [Fact]
    public void OutputSchemaDescribesRecordsAndRequiresOnlyAlwaysPresentMembers()
    {
        var tool = Describe<SchemaProvider>("schema_tool");
        Assert.False(tool.WrapResult);
        var schema = tool.OutputSchema!;
        var p = schema["properties"]!;
        Assert.Equal("""{"type":"string","format":"uuid"}""", p["id"]!.ToJsonString());
        Assert.Equal("""{"type":"string","format":"date-time"}""", p["at"]!.ToJsonString());
        Assert.Equal("""{"type":"string","contentEncoding":"base64"}""", p["raw"]!.ToJsonString());
        Assert.Equal("""{"type":"integer","minimum":0}""", p["big"]!.ToJsonString());
        Assert.Equal("""{"type":"object","additionalProperties":{"type":"number"}}""", p["scores"]!.ToJsonString());
        Assert.Equal("array", p["many"]!["type"]!.GetValue<string>());
        Assert.Equal("""{}""", p["self"]!.ToJsonString()); // recursion is cut
        Assert.Equal(["colour", "id", "at", "big"], schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(["count"], p["inner"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    public sealed class WrapProvider
    {
        [McpTool("t_string")] public string S() => "";
        [McpTool("t_list")] public List<Inner> L() => [];
        [McpTool("t_task_int")] public Task<int> TI() => Task.FromResult(1);
        [McpTool("t_valuetask")] public ValueTask<Inner> VT() => ValueTask.FromResult(new Inner(1, null));
        [McpTool("t_nullable_record")] public Inner? NR() => null;
        [McpTool("t_void")] public Task V() => Task.CompletedTask;
        [McpTool("t_result")] public ToolResult R() => ToolResult.Text("x");
        [McpTool("t_dict")] public Dictionary<string, int> D() => [];
        [McpTool("t_node")] public JsonNode N() => new JsonObject();
    }

    [Theory]
    [InlineData("t_string", true, """{"type":"object","properties":{"result":{"type":"string"}},"required":["result"]}""")]
    [InlineData("t_task_int", true, """{"type":"object","properties":{"result":{"type":"integer"}},"required":["result"]}""")]
    [InlineData("t_valuetask", false, """{"type":"object","properties":{"count":{"type":"integer"},"label":{"type":"string"}},"required":["count"]}""")]
    [InlineData("t_nullable_record", true, """{"type":"object","properties":{"result":{"type":"object","properties":{"count":{"type":"integer"},"label":{"type":"string"}},"required":["count"]}}}""")]
    [InlineData("t_dict", false, """{"type":"object","additionalProperties":{"type":"integer"}}""")]
    [InlineData("t_node", true, """{"type":"object","properties":{"result":{}},"required":["result"]}""")]
    public void OutputWrapping(string name, bool wrap, string expected)
    {
        var tool = Describe<WrapProvider>(name);
        Assert.Equal(wrap, tool.WrapResult);
        Assert.Equal(expected, tool.OutputSchema!.ToJsonString());
    }

    [Fact]
    public void NoOutputSchemaForVoidOrToolResult()
    {
        Assert.Null(Describe<WrapProvider>("t_void").OutputSchema);
        Assert.Null(Describe<WrapProvider>("t_result").OutputSchema);
        Assert.Equal("array", Describe<WrapProvider>("t_list").OutputSchema!["properties"]!["result"]!["type"]!.GetValue<string>());
    }

    public sealed class DuplicateA
    {
        [McpTool("dup")] public string A() => "";
    }

    public sealed class DuplicateB
    {
        [McpTool("dup")] public string B() => "";
    }

    public sealed class BadName
    {
        [McpTool("bad name!")] public string A() => "";
    }

    public sealed class BadResource
    {
        [McpResource("test://x")] public string A(int id) => "";
    }

    public sealed class BadTemplate
    {
        [McpResourceTemplate("test://x/{?query}")] public string A(string query) => "";
    }

    public sealed class UnboundTemplateParameter
    {
        [McpResourceTemplate("test://x/{id}")] public string A(int id, int other) => "";
    }

    public sealed class BadPrompt
    {
        [McpPrompt("p")] public int A() => 1;
    }

    public sealed class TwoAttributes
    {
        [McpTool("two")]
        [McpPrompt("two")]
        public string A() => "";
    }

    [Theory]
    [InlineData(typeof(BadName), "must match")]
    [InlineData(typeof(BadResource), "may only take ToolContext")]
    [InlineData(typeof(BadTemplate), "is not supported")]
    [InlineData(typeof(UnboundTemplateParameter), "is not a variable of template")]
    [InlineData(typeof(BadPrompt), "must return string or PromptResult")]
    [InlineData(typeof(TwoAttributes), "more than one MCP attribute")]
    public void InvalidDeclarationsThrow(Type providerType, string message)
    {
        var registry = new ProviderRegistry();
        var ex = Assert.Throws<ArgumentException>(() => registry.Register(Activator.CreateInstance(providerType)!));
        Assert.Contains(message, ex.Message);
        Assert.Empty(registry.Snapshot.Tools);
    }

    [Fact]
    public void DuplicatesThrowAndLeaveRegistryUnchanged()
    {
        var registry = new ProviderRegistry();
        registry.Register(new DuplicateA());
        var ex = Assert.Throws<ArgumentException>(() => registry.Register(new DuplicateB()));
        Assert.Contains("Duplicate tool name 'dup'", ex.Message);
        Assert.Single(registry.Snapshot.Tools);
        Assert.Equal("general", registry.Snapshot.Tools[0].Category);
    }

    [Fact]
    public async Task RegisterProvidersUsesFactoryAndAggregatesFailures()
    {
        await using var server = new McpServer(new McpServerOptions(), new FakeGameThread(), new FakeHostState());
        var created = new List<Type>();
        var ex = Assert.Throws<AggregateException>(() => server.RegisterProviders(typeof(BasicProvider).Assembly, type =>
        {
            created.Add(type);
            if (type == typeof(SlowProvider))
                throw new InvalidOperationException("dependency missing");
            return type == typeof(BasicProvider) ? new BasicProvider(new FakeGameThread()) : Activator.CreateInstance(type)!;
        }));
        Assert.Contains(typeof(BasicProvider), created);
        Assert.Contains(typeof(DataProvider), created);
        Assert.DoesNotContain(typeof(DuplicateA), created); // no [McpProvider]
        Assert.Contains(ex.InnerExceptions, e => e.Message.Contains("SlowProvider") && e.Message.Contains("dependency missing"));
        Assert.Contains(server.ListRegisteredTools(), t => t.Name == "echo" && t.Category == "basic" && t.Title == "Echo");
        Assert.DoesNotContain(server.ListRegisteredTools(), t => t.Name == "hang");
    }

    [Theory]
    [InlineData("ffxiv://item/{id}", "ffxiv://item/123", "id=123")]
    [InlineData("ffxiv://item/{id}", "ffxiv://item/1/2", null)]
    [InlineData("ffxiv://item/{id}", "ffxiv://item/", null)]
    [InlineData("ffxiv://zone/{territory}/weather/{hour}", "ffxiv://zone/Limsa%20Lominsa/weather/08", "territory=Limsa Lominsa;hour=08")]
    [InlineData("file:///{+path}", "file:///a/b/c.txt", "path=a/b/c.txt")]
    [InlineData("ffxiv://a.b/{x}", "ffxiv://aXb/1", null)]
    public void UriTemplateMatching(string template, string uri, string? expected)
    {
        var t = UriTemplate.Parse(template);
        var matched = t.TryMatch(uri, out var values);
        if (expected is null)
        {
            Assert.False(matched);
            return;
        }

        Assert.True(matched);
        Assert.Equal(expected, string.Join(";", values.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Theory]
    [InlineData("x://{a")]
    [InlineData("x://a}")]
    [InlineData("x://{}")]
    [InlineData("x://{a}/{a}")]
    [InlineData("x://{#frag}")]
    public void UriTemplateRejectsUnsupported(string template) =>
        Assert.Throws<ArgumentException>(() => UriTemplate.Parse(template));

    [Fact]
    public async Task LateRegistrationNotifiesListeners()
    {
        await using var s = await TestServer.StartAsync(registerDefaults: false);
        s.Server.RegisterProvider(new DataProvider());
        using var response = await s.Http.SendAsync(s.ModernPost("subscriptions/listen", new JsonObject
        {
            ["notifications"] = new JsonObject { ["toolsListChanged"] = true, ["promptsListChanged"] = true, ["resourcesListChanged"] = true },
        }), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new SseReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("notifications/subscriptions/acknowledged", (await reader.NextMessageAsync())!["method"]!.GetValue<string>());
        s.Server.RegisterProvider(new BasicProvider(s.Game));
        Assert.Equal("notifications/tools/list_changed", (await reader.NextMessageAsync())!["method"]!.GetValue<string>());
    }
}

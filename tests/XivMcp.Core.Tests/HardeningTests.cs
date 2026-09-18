using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XivMcp.Core.Registry;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public sealed record TreeNode(string Name, List<TreeNode>? Children);

public sealed record Pair(string Label, PairLink? Link);

public sealed record PairLink(int Weight, Pair? Back);

public sealed record LocalizedItem(string Name, string Note);

[McpProvider("hardening")]
public sealed class HardeningProvider
{
    [McpTool("protocol_version", Description = "Reports the negotiated protocol version.", GameThread = false, RequiresLogin = false)]
    public string ProtocolVersion(ToolContext ctx) => ctx.ProtocolVersion ?? "(none)";

    [McpTool("localized_item", Description = "Returns non-ASCII and HTML-sensitive text.", GameThread = false, RequiresLogin = false)]
    public LocalizedItem Localized() => new("ハイ・エーテル", "<b>\"quoted\" & 'apos'</b>\nline2\u2028end");

    [McpTool("count_tree", Description = "Counts the nodes of a recursive tree argument.", GameThread = false, RequiresLogin = false)]
    public int CountTree(TreeNode root) => 1 + (root.Children?.Sum(c => CountTree(c)) ?? 0);

    [McpTool("pair_out", Description = "Mutually recursive output.", GameThread = false, RequiresLogin = false)]
    public Pair PairOut() => new("a", new PairLink(1, null));

    [McpTool("any_value", Description = "Takes any JSON.", GameThread = false, RequiresLogin = false)]
    public string AnyValue(JsonNode? value) => value?.ToJsonString() ?? "null";
}

public class HardeningTests
{
    private static async Task<TestServer> StartAsync()
    {
        var s = await TestServer.StartAsync();
        s.Server.RegisterProvider(new HardeningProvider());
        return s;
    }

    private static async Task<JsonObject> CallAsync(TestServer s, string name, JsonObject? arguments = null)
    {
        var p = new JsonObject { ["name"] = name };
        if (arguments is not null)
            p["arguments"] = arguments;
        var (_, response, _) = await s.ModernCallAsync("tools/call", p);
        Assert.Null(response["error"]);
        return response["result"]!.AsObject();
    }

    [Fact]
    public async Task ToolContextCarriesTheProtocolVersion()
    {
        await using var s = await StartAsync();
        Assert.Equal("2026-07-28", (await CallAsync(s, "protocol_version"))["content"]![0]!["text"]!.GetValue<string>());

        foreach (var version in new[] { "2025-11-25", "2025-06-18", "2025-03-26" })
        {
            var session = await s.InitializeAsync(version);
            var legacy = await s.LegacyCallAsync(session, "tools/call", new JsonObject { ["name"] = "protocol_version" }, version);
            Assert.Equal(version, legacy["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task TextContentKeepsNonAsciiReadableAndStaysValidJson()
    {
        await using var s = await StartAsync();
        using var response = await s.Http.SendAsync(s.ModernPost("tools/call", new JsonObject { ["name"] = "localized_item" }, id: 77), HttpCompletionOption.ResponseHeadersRead);
        var wire = await response.Content.ReadAsStringAsync();

        // The wire format is one JSON document whose strings escape quotes, backslashes and control characters.
        var parsed = JsonNode.Parse(wire)!.AsObject();
        Assert.DoesNotContain("\n", wire);
        var result = parsed["result"]!;
        var text = result["content"]![0]!["text"]!.GetValue<string>();

        // The text block mirrors structuredContent without \uXXXX escapes for readable characters.
        Assert.Contains("ハイ・エーテル", text);
        Assert.Contains("<b>", text);
        Assert.Contains("&", text);
        Assert.DoesNotContain("\\u30", text);
        Assert.DoesNotContain("\\u003C", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\n", text);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result["structuredContent"]));
        Assert.Equal("<b>\"quoted\" & 'apos'</b>\nline2\u2028end", result["structuredContent"]!["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task LoneSurrogatesInRequestsStillGetWellFormedResponses()
    {
        await using var s = await StartAsync();
        var session = await s.InitializeAsync();
        var body = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"\\ud800x\"}}";
        var request = new HttpRequestMessage(HttpMethod.Post, s.Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Mcp-Session-Id", session);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        using var response = await s.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await TestServer.ReadJsonAsync(response);
        Assert.Equal(-32700, json["error"]!["code"]!.GetValue<int>());
        Assert.Contains("surrogate", json["error"]!["message"]!.GetValue<string>());

        // A lone surrogate in the method name and in a property name too.
        foreach (var bad in new[] { "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"\\udc00\"}", "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\",\"params\":{\"\\ud83d\":1}}" })
        {
            var r = new HttpRequestMessage(HttpMethod.Post, s.Endpoint) { Content = new StringContent(bad, Encoding.UTF8, "application/json") };
            r.Headers.Add("Mcp-Session-Id", session);
            using var resp = await s.Http.SendAsync(r);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // Valid pairs (an emoji) are fine.
        Assert.True(McpServer.HasWellFormedStrings("{\"a\":\"\\ud83d\\ude00\"}"u8));
        Assert.False(McpServer.HasWellFormedStrings("[\"\\ude00\\ud83d\"]"u8));
    }

    [Fact]
    public void RecursiveTypesUseDefsInsteadOfEmptySchemas()
    {
        var registry = new ProviderRegistry();
        registry.Register(new HardeningProvider());
        var tools = registry.Snapshot.ToolsByName;

        var input = tools["count_tree"].InputSchema;
        const string tree = "XivMcp.Core.Tests.TreeNode";
        var root = input["properties"]!["root"]!;
        Assert.Equal("object", root["type"]!.GetValue<string>());
        Assert.Equal("#/$defs/" + tree, root["properties"]!["children"]!["items"]!["$ref"]!.GetValue<string>());
        Assert.Equal("object", input["$defs"]![tree]!["type"]!.GetValue<string>());
        AssertNoEmptySchemas(input);

        var output = tools["pair_out"].OutputSchema!;
        Assert.Equal("object", output["type"]!.GetValue<string>());
        Assert.Equal("#/$defs/XivMcp.Core.Tests.Pair", output["properties"]!["link"]!["properties"]!["back"]!["$ref"]!.GetValue<string>());
        Assert.NotNull(output["$defs"]!["XivMcp.Core.Tests.Pair"]);
        AssertNoEmptySchemas(output);

        var any = tools["any_value"].InputSchema["properties"]!["value"]!;
        Assert.NotNull(any["anyOf"]);
    }

    [Fact]
    public async Task RecursiveArgumentsBind()
    {
        await using var s = await StartAsync();
        var tree = JsonNode.Parse("""{"name":"a","children":[{"name":"b","children":[{"name":"c"}]},{"name":"d","children":[]}]}""");
        var result = await CallAsync(s, "count_tree", new JsonObject { ["root"] = tree });
        Assert.Equal(4, result["structuredContent"]!["result"]!.GetValue<int>());
    }

    [Fact]
    public async Task ResourcesFollowTheReadTier()
    {
        await using var s = await TestServer.StartAsync();
        s.Host.Permissions[ToolPermission.Read] = false;

        var (_, list, _) = await s.ModernCallAsync("resources/list");
        Assert.Empty(list["result"]!["resources"]!.AsArray());
        var (_, templates, _) = await s.ModernCallAsync("resources/templates/list");
        Assert.Empty(templates["result"]!["resourceTemplates"]!.AsArray());

        var (_, read, _) = await s.ModernCallAsync("resources/read", new JsonObject { ["uri"] = "test://text" });
        Assert.Contains("'Read' permission tier", read["error"]!["message"]!.GetValue<string>());
        var (_, templated, _) = await s.ModernCallAsync("resources/read", new JsonObject { ["uri"] = "test://item/5" });
        Assert.Contains("'Read' permission tier", templated["error"]!["message"]!.GetValue<string>());

        var (_, complete, _) = await s.ModernCallAsync("completion/complete", new JsonObject
        {
            ["ref"] = new JsonObject { ["type"] = "ref/resource", ["uri"] = "test://colour/{colour}/{+rest}" },
            ["argument"] = new JsonObject { ["name"] = "colour", ["value"] = "r" },
        });
        Assert.NotNull(complete["error"]);

        s.Host.Permissions[ToolPermission.Read] = true;
        var (_, allowed, _) = await s.ModernCallAsync("resources/read", new JsonObject { ["uri"] = "test://text" });
        Assert.Equal("hello", allowed["result"]!["contents"]![0]!["text"]!.GetValue<string>());
    }

    private static void AssertNoEmptySchemas(JsonNode? node, string path = "$")
    {
        switch (node)
        {
            case JsonObject obj:
                Assert.False(obj.Count == 0, $"empty schema at {path}");
                foreach (var (key, value) in obj)
                {
                    if (key is "properties" or "$defs" && value is JsonObject map)
                    {
                        foreach (var (name, child) in map)
                            AssertNoEmptySchemas(child, $"{path}.{key}.{name}");
                    }
                    else if (key is "items" or "additionalProperties" && value is JsonObject)
                    {
                        AssertNoEmptySchemas(value, $"{path}.{key}");
                    }
                }

                break;
            case JsonArray:
                break;
        }
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public class ToolExecutionTests
{
    private static async Task<JsonObject> CallAsync(TestServer s, string name, JsonObject? arguments = null)
    {
        var p = new JsonObject { ["name"] = name };
        if (arguments is not null)
            p["arguments"] = arguments;
        var (_, response, _) = await s.ModernCallAsync("tools/call", p);
        Assert.Null(response["error"]);
        return response["result"]!.AsObject();
    }

    private static string ErrorText(JsonObject result)
    {
        Assert.True(result["isError"]?.GetValue<bool>(), result.ToJsonString());
        return result["content"]![0]!["text"]!.GetValue<string>();
    }

    [Fact]
    public async Task GameThreadDispatch()
    {
        await using var s = await TestServer.StartAsync();
        var on = await CallAsync(s, "on_game_thread");
        Assert.True(on["structuredContent"]!["onGameThread"]!.GetValue<bool>());
        var off = await CallAsync(s, "off_game_thread");
        Assert.False(off["structuredContent"]!["onGameThread"]!.GetValue<bool>());
        Assert.Equal(0, s.Host.LoginChecksOnWrongThread);
    }

    [Fact]
    public async Task RequiresLogin()
    {
        await using var s = await TestServer.StartAsync();
        Assert.Equal("logged in", (await CallAsync(s, "needs_login"))["content"]![0]!["text"]!.GetValue<string>());
        s.Host.LoggedIn = false;
        Assert.Contains("requires a logged-in character", ErrorText(await CallAsync(s, "needs_login")));
        Assert.Contains("requires a logged-in character", ErrorText(await CallAsync(s, "async_needs_login")));
        Assert.Equal("x", (await CallAsync(s, "echo", new JsonObject { ["text"] = "x" }))["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(0, s.Host.LoginChecksOnWrongThread);
    }

    [Fact]
    public async Task PermissionAndCategoryGates()
    {
        await using var s = await TestServer.StartAsync();

        async Task<string[]> ListedNames()
        {
            var (_, list, _) = await s.ModernCallAsync("tools/list");
            return list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
        }

        var names = await ListedNames();
        Assert.DoesNotContain("do_action", names);
        Assert.DoesNotContain("say_hello", names);
        Assert.Contains("gated_read", names);

        var denied = ErrorText(await CallAsync(s, "do_action"));
        Assert.Contains("'Action' permission tier", denied);
        Assert.Contains("plugin settings", denied);

        s.Host.Permissions[ToolPermission.Action] = true;
        Assert.Contains("do_action", await ListedNames());
        Assert.Equal("acted", (await CallAsync(s, "do_action"))["content"]![0]!["text"]!.GetValue<string>());

        s.Host.DisabledCategories["gated"] = true;
        names = await ListedNames();
        Assert.DoesNotContain("gated_read", names);
        Assert.DoesNotContain("do_action", names);
        Assert.Contains("'gated' tool category is disabled", ErrorText(await CallAsync(s, "gated_read")));

        var (_, resources, _) = await s.ModernCallAsync("resources/list");
        Assert.DoesNotContain(resources["result"]!["resources"]!.AsArray(), r => r!["uri"]!.GetValue<string>() == "test://gated");
        var (_, read, _) = await s.ModernCallAsync("resources/read", new JsonObject { ["uri"] = "test://gated" });
        Assert.Equal(-32602, read["error"]!["code"]!.GetValue<int>());

        var tool = (await s.ModernCallAsync("tools/list")).Response;
        Assert.Contains(s.Server.ListRegisteredTools(), t => t.Name == "say_hello" && t.Permission == ToolPermission.Chat && t.Category == "gated");
        Assert.NotNull(tool);
    }

    [Fact]
    public async Task ArgumentErrorsAreToolErrors()
    {
        await using var s = await TestServer.StartAsync();
        var missing = ErrorText(await CallAsync(s, "add", new JsonObject { ["a"] = 1 }));
        Assert.Contains("missing required argument 'b'", missing);
        Assert.Contains("Expected arguments: a: integer, required; b: integer, required", missing);

        var wrongType = ErrorText(await CallAsync(s, "add", new JsonObject { ["a"] = "one", ["b"] = true }));
        Assert.Contains("argument 'a' must be an integer (got string \"one\")", wrongType);
        Assert.Contains("argument 'b' must be an integer (got boolean true)", wrongType);

        var fraction = ErrorText(await CallAsync(s, "add", new JsonObject { ["a"] = 1.5, ["b"] = 1 }));
        Assert.Contains("must be an integer (got 1.5)", fraction);

        var overflow = ErrorText(await CallAsync(s, "add", new JsonObject { ["a"] = 3_000_000_000L, ["b"] = 1 }));
        Assert.Contains("out of range for Int32", overflow);

        var unknown = ErrorText(await CallAsync(s, "add", new JsonObject { ["a"] = 1, ["b"] = 2, ["c"] = 3 }));
        Assert.Contains("unknown argument 'c' (valid: a, b)", unknown);

        var badEnum = ErrorText(await CallAsync(s, "pick", new JsonObject { ["colour"] = "purple" }));
        Assert.Contains("unknown value 'purple'", badEnum);
        Assert.Contains("red, darkBlue, lightGreen", badEnum);

        var badAllowed = ErrorText(await CallAsync(s, "pick", new JsonObject { ["colour"] = "red", ["mode"] = "medium" }));
        Assert.Contains("allowed: fast, slow", badAllowed);

        var range = ErrorText(await CallAsync(s, "numbers", new JsonObject { ["small"] = 256, ["big"] = -1, ["real"] = "abc", ["percent"] = 101 }));
        Assert.Contains("'small' is out of range for Byte", range);
        Assert.Contains("'big' is out of range for UInt32", range);
        Assert.Contains("'real' must be a number", range);
        Assert.Contains("'percent' must be between 0 and 100 (got 101)", range);

        var nested = ErrorText(await CallAsync(s, "make_waymark", new JsonObject { ["name"] = "w", ["position"] = new JsonObject { ["x"] = "far" } }));
        Assert.Contains("argument 'position.x' is invalid", nested);

        var notObject = await s.ModernCallAsync("tools/call", new JsonObject { ["name"] = "add", ["arguments"] = new JsonArray() });
        Assert.Equal(-32602, notObject.Response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task LenientConversions()
    {
        await using var s = await TestServer.StartAsync();
        Assert.Equal(5, (await CallAsync(s, "add", new JsonObject { ["a"] = "2", ["b"] = 3.0 }))["structuredContent"]!["result"]!.GetValue<int>());
        Assert.Equal("DarkBlue/slow", (await CallAsync(s, "pick", new JsonObject { ["colour"] = "DARK_BLUE", ["mode"] = "SLOW" }))["structuredContent"]!["result"]!.GetValue<string>());
        Assert.Equal("LightGreen/fast", (await CallAsync(s, "pick", new JsonObject { ["Colour"] = "light-green" }))["structuredContent"]!["result"]!.GetValue<string>());
        Assert.Equal("255/4000000000/1.5/50", (await CallAsync(s, "numbers", new JsonObject { ["small"] = 255, ["big"] = 4_000_000_000L, ["real"] = "1.5" }))["structuredContent"]!["result"]!.GetValue<string>());
        Assert.Equal("7", (await CallAsync(s, "echo", new JsonObject { ["text"] = 7 }))["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResultShapes()
    {
        await using var s = await TestServer.StartAsync();

        var waymark = await CallAsync(s, "make_waymark", new JsonObject
        {
            ["name"] = "A",
            ["position"] = new JsonObject { ["x"] = 1.5, ["y"] = 0, ["z"] = -2 },
            ["tags"] = new JsonArray("A", "c"),
        });
        var structured = waymark["structuredContent"]!;
        Assert.Equal("darkBlue", structured["colour"]!.GetValue<string>());
        Assert.Null(structured["order"]);
        Assert.Null(structured["note"]);
        Assert.Equal(["a", "c"], structured["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(structured.ToJsonString(), waymark["content"]![0]!["text"]!.GetValue<string>());

        var numbers = await CallAsync(s, "list_numbers");
        Assert.Equal([1, 2, 3], numbers["structuredContent"]!["result"]!.AsArray().Select(n => n!.GetValue<int>()));

        var nothing = await CallAsync(s, "nothing");
        Assert.Null(nothing["structuredContent"]);
        Assert.Equal("OK", nothing["content"]![0]!["text"]!.GetValue<string>());

        var explicitOk = await CallAsync(s, "explicit_result", new JsonObject { ["error"] = false });
        Assert.Equal("image", explicitOk["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("AQID", explicitOk["content"]![1]!["data"]!.GetValue<string>());
        Assert.True(explicitOk["structuredContent"]!["ok"]!.GetValue<bool>());
        Assert.Equal("explicit failure", ErrorText(await CallAsync(s, "explicit_result", new JsonObject { ["error"] = true })));

        var none = await CallAsync(s, "maybe_waymark", new JsonObject { ["give"] = false });
        Assert.NotNull(none["structuredContent"]);
        Assert.Null(none["structuredContent"]!["result"]);
        var some = await CallAsync(s, "maybe_waymark", new JsonObject { ["give"] = true });
        Assert.Equal("w", some["structuredContent"]!["result"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailuresAreToolErrors()
    {
        await using var s = await TestServer.StartAsync();
        Assert.Equal("Item 12345 not found", ErrorText(await CallAsync(s, "user_error")));
        var crash = ErrorText(await CallAsync(s, "crash"));
        Assert.Contains("internal error", crash);
        Assert.Contains("boom", crash);
        Assert.Contains(s.Logs, l => l.Contains("tool 'crash' threw"));
        var failed = s.Server.GetStatus().FailedRequests;
        Assert.True(failed >= 2);
        Assert.Contains("crash", s.Server.GetStatus().LastError);
    }

    [Fact]
    public async Task CallTimeout()
    {
        await using var s = await TestServer.StartAsync(o => o.CallTimeout = TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        var hang = ErrorText(await CallAsync(s, "hang"));
        Assert.Contains("timed out after 0.3 s", hang);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));

        // A stalled framework thread is also bounded by the timeout, and the queued work is dropped.
        s.Game.Gate = new ManualResetEventSlim(false);
        var stalled = ErrorText(await CallAsync(s, "on_game_thread"));
        Assert.Contains("timed out", stalled);
        s.Game.Gate.Set();
    }

    [Fact]
    public async Task ConcurrentCallsMixingThreads()
    {
        await using var s = await TestServer.StartAsync();
        var tasks = Enumerable.Range(0, 60).Select(async i =>
        {
            var name = (i % 3) switch { 0 => "on_game_thread", 1 => "sleep", _ => "add" };
            JsonObject? args = name switch
            {
                "sleep" => new JsonObject { ["ms"] = 20 },
                "add" => new JsonObject { ["a"] = i, ["b"] = i },
                _ => null,
            };
            var result = await CallAsync(s, name, args);
            Assert.Null(result["isError"]);
            return result;
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(60, results.Length);
        Assert.True(s.Game.InvocationCount >= 20);
    }

    [Fact]
    public async Task ActivityFeedAndEvent()
    {
        await using var s = await TestServer.StartAsync(o => o.ActivityCapacity = 5);
        var seen = new List<ActivityEntry>();
        var gotOne = new TaskCompletionSource();
        s.Server.ActivityRecorded += e =>
        {
            lock (seen)
                seen.Add(e);
            if (e.Target == "echo")
                gotOne.TrySetResult();
        };
        s.Server.ActivityRecorded += _ => throw new InvalidOperationException("handler bug must not break the server");

        for (var i = 0; i < 8; i++)
            await CallAsync(s, "echo", new JsonObject { ["text"] = "a" });
        await gotOne.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var activity = s.Server.GetActivity(100);
        Assert.Equal(5, activity.Count);
        Assert.True(activity[0].Timestamp >= activity[^1].Timestamp);
        Assert.All(activity, a =>
        {
            Assert.Equal("tools/call", a.Method);
            Assert.Equal("modern-test 2.0", a.ClientName);
            Assert.True(a.Success);
        });
        Assert.Equal(2, s.Server.GetActivity(2).Count);
        Assert.True(s.Server.GetStatus().TotalRequests >= 8);
    }
}

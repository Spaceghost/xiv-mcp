using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

[McpProvider("approval")]
public sealed class ApprovalProvider
{
    public int Runs;

    [McpTool("approve_action", Description = "Action tier.", Permission = ToolPermission.Action, GameThread = false, RequiresLogin = false)]
    public string Act(string text, int count = 1)
    {
        Interlocked.Increment(ref Runs);
        return $"{text}x{count}";
    }

    [McpTool("approve_chat", Description = "Chat tier.", Permission = ToolPermission.Chat, GameThread = false, RequiresLogin = false, OpenWorld = true)]
    public string Chat()
    {
        Interlocked.Increment(ref Runs);
        return "said";
    }

    [McpTool("approve_login_action", Description = "Action tier on the game thread, needs login.", Permission = ToolPermission.Action)]
    public string LoginAct()
    {
        Interlocked.Increment(ref Runs);
        return "acted";
    }

    [McpTool("approve_ui", Description = "Ui tier.", Permission = ToolPermission.Ui, GameThread = false, RequiresLogin = false)]
    public string Ui()
    {
        Interlocked.Increment(ref Runs);
        return "ui";
    }

    [McpTool("approve_slow_action", Description = "Action tier that takes 400 ms.", Permission = ToolPermission.Action, GameThread = false, RequiresLogin = false)]
    public async Task<string> SlowAct(CancellationToken cancellationToken)
    {
        await Task.Delay(400, cancellationToken);
        Interlocked.Increment(ref Runs);
        return "slow";
    }
}

public sealed class FakeApprover : IToolCallApprover
{
    public readonly ConcurrentQueue<(string Tool, ToolPermission Permission, string? Client, string? Arguments, bool OnGameThread)> Calls = new();

    public Func<CancellationToken, Task<bool>> Decide { get; set; } = static _ => Task.FromResult(true);

    public FakeGameThread? Game { get; set; }

    public CancellationToken LastToken { get; private set; }

    public Task<bool> ApproveToolCallAsync(string toolName, ToolPermission permission, string? clientName, string? argumentsJson, CancellationToken cancellationToken)
    {
        LastToken = cancellationToken;
        Calls.Enqueue((toolName, permission, clientName, argumentsJson, Game?.IsOnGameThread ?? false));
        return Decide(cancellationToken);
    }
}

public class ApprovalTests
{
    private static async Task<(TestServer Server, ApprovalProvider Provider, FakeApprover Approver)> StartAsync(Action<McpServerOptions>? configure = null)
    {
        var s = await TestServer.StartAsync(configure);
        var provider = new ApprovalProvider();
        s.Server.RegisterProvider(provider);
        s.Host.Permissions[ToolPermission.Action] = true;
        s.Host.Permissions[ToolPermission.Chat] = true;
        var approver = new FakeApprover { Game = s.Game };
        s.Server.Approver = approver;
        return (s, provider, approver);
    }

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

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsError(JsonObject result) => result["isError"]?.GetValue<bool>() == true;

    [Fact]
    public async Task ApprovedCallRunsAndApproverSeesToolClientAndArguments()
    {
        var (s, provider, approver) = await StartAsync();
        await using var _ = s;

        var result = await CallAsync(s, "approve_action", new JsonObject { ["text"] = "héllo <b>", ["count"] = 2 });
        Assert.False(IsError(result), result.ToJsonString());
        Assert.Equal("héllo <b>x2", Text(result));
        Assert.Equal(1, provider.Runs);

        var call = Assert.Single(approver.Calls);
        Assert.Equal("approve_action", call.Tool);
        Assert.Equal(ToolPermission.Action, call.Permission);
        Assert.Equal("modern-test 2.0", call.Client);
        Assert.Equal("""{"text":"héllo <b>","count":2}""", call.Arguments);
        Assert.False(call.OnGameThread);

        await CallAsync(s, "approve_chat");
        Assert.Contains(approver.Calls, c => c.Tool == "approve_chat" && c.Permission == ToolPermission.Chat && c.Arguments is null);
    }

    [Fact]
    public async Task DeniedCallDoesNotRun()
    {
        var (s, provider, approver) = await StartAsync();
        await using var _ = s;
        approver.Decide = static _ => Task.FromResult(false);

        var result = await CallAsync(s, "approve_action", new JsonObject { ["text"] = "x" });
        Assert.True(IsError(result));
        Assert.Contains("denied in game by the player", Text(result));
        Assert.Equal(0, provider.Runs);
        Assert.Contains(s.Server.GetActivity(5), a => a.Target == "approve_action" && !a.Success && a.Error!.Contains("denied in game"));
    }

    [Fact]
    public async Task UnansweredApprovalTimesOutAndCancelsTheApprover()
    {
        var (s, provider, approver) = await StartAsync(o => o.ApprovalTimeout = TimeSpan.FromMilliseconds(300));
        await using var _ = s;
        approver.Decide = static async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return true;
        };

        var result = await CallAsync(s, "approve_action", new JsonObject { ["text"] = "x" });
        Assert.True(IsError(result));
        Assert.Contains("not confirmed in game within 0.3 s", Text(result));
        Assert.True(approver.LastToken.IsCancellationRequested);
        Assert.Equal(0, provider.Runs);
    }

    [Fact]
    public async Task ApproverTimeoutExceptionAndFaultsFailClosed()
    {
        var (s, provider, approver) = await StartAsync();
        await using var _ = s;

        approver.Decide = static _ => Task.FromException<bool>(new TimeoutException());
        var timedOut = await CallAsync(s, "approve_action", new JsonObject { ["text"] = "x" });
        Assert.Contains("not confirmed in game", Text(timedOut));

        approver.Decide = static _ => throw new InvalidOperationException("window gone");
        var faulted = await CallAsync(s, "approve_action", new JsonObject { ["text"] = "x" });
        Assert.True(IsError(faulted));
        Assert.Contains("Denied for safety", Text(faulted));
        Assert.Equal(0, provider.Runs);
    }

    [Fact]
    public async Task ApproverIsOnlyAskedForPermittedValidActionAndChatCalls()
    {
        var (s, provider, approver) = await StartAsync();
        await using var _ = s;

        Assert.Equal("ui", Text(await CallAsync(s, "approve_ui")));
        Assert.Equal("x", Text(await CallAsync(s, "echo", new JsonObject { ["text"] = "x" })));
        Assert.Empty(approver.Calls);

        // Invalid arguments are reported without asking the player.
        var invalid = await CallAsync(s, "approve_action", new JsonObject { ["count"] = "many" });
        Assert.Contains("Invalid arguments", Text(invalid));
        Assert.Empty(approver.Calls);

        // A disabled tier is reported without asking the player.
        s.Host.Permissions[ToolPermission.Chat] = false;
        Assert.Contains("'Chat' permission tier", Text(await CallAsync(s, "approve_chat")));
        Assert.Empty(approver.Calls);

        // No logged-in character: no prompt, the login error comes first.
        s.Host.LoggedIn = false;
        Assert.Contains("requires a logged-in character", Text(await CallAsync(s, "approve_login_action")));
        Assert.Empty(approver.Calls);
        Assert.Equal(0, s.Host.LoginChecksOnWrongThread);

        s.Host.LoggedIn = true;
        Assert.Equal("acted", Text(await CallAsync(s, "approve_login_action")));
        Assert.Single(approver.Calls);
        Assert.Equal(2, provider.Runs);
    }

    [Fact]
    public async Task ApprovalWaitDoesNotConsumeTheCallTimeout()
    {
        var (s, provider, approver) = await StartAsync(o =>
        {
            o.CallTimeout = TimeSpan.FromMilliseconds(600);
            o.ApprovalTimeout = TimeSpan.FromSeconds(10);
        });
        await using var _ = s;
        approver.Decide = static async token =>
        {
            await Task.Delay(900, token);
            return true;
        };

        var result = await CallAsync(s, "approve_slow_action");
        Assert.False(IsError(result), result.ToJsonString());
        Assert.Equal("slow", Text(result));
        Assert.Equal(1, provider.Runs);
    }

    [Fact]
    public async Task LegacySessionsAndBatchesAreGatedToo()
    {
        var (s, provider, approver) = await StartAsync();
        await using var _ = s;
        approver.Decide = static _ => Task.FromResult(false);

        var session = await s.InitializeAsync("2025-06-18", "legacy-client");
        var legacy = await s.LegacyCallAsync(session, "tools/call", new JsonObject { ["name"] = "approve_chat" }, "2025-06-18");
        Assert.True(legacy["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains(approver.Calls, c => c.Client == "legacy-client 1.0");

        var batchSession = await s.InitializeAsync("2025-03-26");
        var batch = new JsonArray(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "approve_action", ["arguments"] = new JsonObject { ["text"] = "a" } } },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "approve_chat" } });
        using var response = await s.Http.SendAsync(s.Post(batch, batchSession, "2025-03-26"));
        var replies = (await TestServer.ReadJsonAsync(response)).AsArray();
        Assert.Equal(2, replies.Count);
        Assert.All(replies, r => Assert.True(r!["result"]!["isError"]!.GetValue<bool>()));
        Assert.Equal(0, provider.Runs);
        Assert.Equal(3, approver.Calls.Count);
    }

    [Fact]
    public async Task ClearingTheApproverRunsCallsDirectly()
    {
        var (s, provider, approver) = await StartAsync();
        await using var _ = s;
        approver.Decide = static _ => Task.FromResult(false);
        Assert.True(IsError(await CallAsync(s, "approve_chat")));

        s.Server.Approver = null;
        Assert.Equal("said", Text(await CallAsync(s, "approve_chat")));
        Assert.Equal(1, provider.Runs);
    }
}

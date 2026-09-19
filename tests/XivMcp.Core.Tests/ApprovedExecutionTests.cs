using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

public sealed class SessionAwareApprover : ISessionAwareToolCallApprover
{
    public readonly ConcurrentQueue<ToolCallApprovalRequest> Requests = new();

    public int LegacyCalls;

    public Task<bool> ApproveToolCallAsync(ToolCallApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(true);
    }

    public Task<bool> ApproveToolCallAsync(string toolName, ToolPermission permission, string? clientName, string? argumentsJson, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref LegacyCalls);
        return Task.FromResult(true);
    }
}

public class ApprovedExecutionTests
{
    private static async Task<(TestServer Server, ApprovalProvider Provider, FakeApprover Approver)> StartAsync()
    {
        var s = await TestServer.StartAsync();
        var provider = new ApprovalProvider();
        s.Server.RegisterProvider(provider);
        s.Host.Permissions[ToolPermission.Action] = true;
        s.Host.Permissions[ToolPermission.Chat] = true;
        var approver = new FakeApprover { Decide = static _ => Task.FromResult(false) };
        s.Server.Approver = approver;
        return (s, provider, approver);
    }

    [Fact]
    public async Task ApprovedExecutionRunsWithoutAskingTheApproverAndIsNotCountedAsARequest()
    {
        var (s, provider, approver) = await StartAsync();
        await using var server = s;
        var before = s.Server.GetStatus().TotalRequests;

        var run = await s.Server.ExecuteApprovedToolAsync("approve_action", new JsonObject { ["text"] = "go", ["count"] = 3 }, "agent 1.0", "sess-1");
        Assert.False(run.IsError, run.Result.ToJsonString());
        Assert.Null(run.Error);
        Assert.Equal("gox3", run.Result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("gox3", run.Result["structuredContent"]!["result"]!.GetValue<string>());
        Assert.Equal(1, provider.Runs);
        Assert.Empty(approver.Calls);
        Assert.Equal(before, s.Server.GetStatus().TotalRequests);
    }

    [Fact]
    public async Task ApprovedExecutionStillAppliesTierArgumentAndLoginChecks()
    {
        var (s, provider, _) = await StartAsync();
        await using var server = s;

        var unknown = await s.Server.ExecuteApprovedToolAsync("no_such_tool", null, "c", null);
        Assert.True(unknown.IsError);
        Assert.Contains("Unknown tool", unknown.Error);

        var invalid = await s.Server.ExecuteApprovedToolAsync("approve_action", new JsonObject { ["count"] = "many" }, "c", null);
        Assert.True(invalid.IsError);
        Assert.Contains("Invalid arguments", invalid.Error);

        s.Host.LoggedIn = false;
        var loggedOut = await s.Server.ExecuteApprovedToolAsync("approve_login_action", null, "c", null);
        Assert.True(loggedOut.IsError);
        Assert.Contains("logged-in character", loggedOut.Error);

        s.Host.Permissions[ToolPermission.Action] = false;
        var disabled = await s.Server.ExecuteApprovedToolAsync("approve_action", new JsonObject { ["text"] = "x" }, "c", null);
        Assert.True(disabled.IsError);
        Assert.Contains("'Action' permission tier", disabled.Error);
        Assert.Equal(0, provider.Runs);
    }

    [Fact]
    public async Task CheckToolCallReportsTheSameProblemsWithoutRunning()
    {
        var (s, provider, _) = await StartAsync();
        await using var server = s;

        Assert.Null(s.Server.CheckToolCall("approve_chat", null, out var chat));
        Assert.Equal(ToolPermission.Chat, chat);
        Assert.Null(s.Server.CheckToolCall("approve_action", new JsonObject { ["text"] = "x" }, out var action));
        Assert.Equal(ToolPermission.Action, action);
        Assert.Contains("Invalid arguments", s.Server.CheckToolCall("approve_action", new JsonObject(), out _));
        Assert.Contains("Unknown tool", s.Server.CheckToolCall("nope", null, out var unknown));
        Assert.Equal(ToolPermission.Read, unknown);
        s.Host.DisabledCategories["approval"] = true;
        Assert.Contains("category is disabled", s.Server.CheckToolCall("approve_action", new JsonObject { ["text"] = "x" }, out _));
        Assert.Equal(0, provider.Runs);
    }

    [Fact]
    public async Task HostActivityGoesToTheFeedWithoutCountingAsARequest()
    {
        var (s, _, _) = await StartAsync();
        await using var server = s;
        var recorded = new TaskCompletionSource<ActivityEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Server.ActivityRecorded += e => recorded.TrySetResult(e);

        s.Server.RecordHostActivity("sess", "agent 1.0", "tickets/execute", "approve_action #t1", false, new string('e', 900));
        var entry = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("tickets/execute", entry.Method);
        Assert.Equal(501, entry.Error!.Length);
        Assert.Equal(0, s.Server.GetStatus().FailedRequests);
        Assert.Contains(s.Server.GetActivity(5), a => a.Target == "approve_action #t1");
    }

    [Fact]
    public async Task SessionAwareApproverReceivesTheMcpSessionId()
    {
        var s = await TestServer.StartAsync();
        await using var server = s;
        s.Server.RegisterProvider(new ApprovalProvider());
        s.Host.Permissions[ToolPermission.Action] = true;
        var approver = new SessionAwareApprover();
        s.Server.Approver = approver;

        var sessionId = await s.InitializeAsync(clientName: "legacy-agent");
        var response = await s.LegacyCallAsync(sessionId, "tools/call", new JsonObject { ["name"] = "approve_action", ["arguments"] = new JsonObject { ["text"] = "a" } });
        Assert.Null(response["result"]!["isError"]);

        var (status, modern, _) = await s.ModernCallAsync("tools/call", new JsonObject { ["name"] = "approve_action", ["arguments"] = new JsonObject { ["text"] = "b" } });
        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.Null(modern["result"]!["isError"]);

        Assert.Equal(0, approver.LegacyCalls);
        var requests = approver.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(sessionId, requests[0].SessionId);
        Assert.StartsWith("legacy-agent", requests[0].ClientName);
        Assert.Equal("""{"text":"a"}""", requests[0].ArgumentsJson);
        Assert.Null(requests[1].SessionId);
    }
}

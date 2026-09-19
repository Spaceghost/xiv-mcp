using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Meta;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>Stands in for the MCP server: a few Action/Chat/Read tools, recorded executions, optional gate.</summary>
internal sealed class FakeToolRunner : IToolRunner
{
    public readonly Dictionary<string, ToolPermission> Tools = new()
    {
        ["teleport"] = ToolPermission.Action,
        ["set_target"] = ToolPermission.Action,
        ["send_chat"] = ToolPermission.Chat,
        [ConfirmationService.ExecuteCommandTool] = ToolPermission.Action,
        ["get_player"] = ToolPermission.Read,
    };

    public readonly ConcurrentQueue<(string Tool, string? Arguments, string? Client, string? Session)> Runs = new();

    public Func<string, ToolExecutionResult>? Result { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public string? CheckToolCall(string toolName, JsonObject? arguments, out ToolPermission permission)
    {
        if (!Tools.TryGetValue(toolName, out permission))
            return $"Unknown tool: '{toolName}'.";
        return arguments?["invalid"] is not null ? "Invalid arguments for tool." : null;
    }

    public async Task<ToolExecutionResult> ExecuteApprovedToolAsync(string toolName, JsonObject? arguments, string? clientName, string? sessionId, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
            await gate.Task.WaitAsync(cancellationToken);
        Runs.Enqueue((toolName, arguments?.ToJsonString(), clientName, sessionId));
        return Result?.Invoke(toolName)
               ?? new ToolExecutionResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"ran {toolName}" }) }, false, null);
    }
}

internal sealed class ApprovalFixture : IDisposable
{
    public ApprovalFixture(string? directory = null, Action<Configuration>? configure = null)
    {
        Directory = directory ?? Path.Combine(Path.GetTempPath(), "xivmcp-tickets-" + Guid.NewGuid().ToString("N"));
        Config = new Configuration { AllowAction = true, AllowChat = true, ConfirmActions = true };
        configure?.Invoke(Config);
        Sessions = new ApprovalSessionService(Config, Time);
        Confirmations = new ConfirmationService(Config, Time) { Sessions = Sessions };
        Store = new TicketStore(Path.Combine(Directory, TicketStore.FileName));
        Queue = new ApprovalQueue(Config, Store, Runner, Confirmations, Time);
        Queue.ActivityRecorded += a => Activity.Enqueue(a);
    }

    public string Directory { get; }

    public ManualTimeProvider Time { get; } = new();

    public Configuration Config { get; }

    public FakeToolRunner Runner { get; } = new();

    public ApprovalSessionService Sessions { get; }

    public ConfirmationService Confirmations { get; }

    public TicketStore Store { get; }

    public ApprovalQueue Queue { get; private set; }

    public ConcurrentQueue<TicketActivity> Activity { get; } = new();

    public Ticket Submit(string tool = "teleport", JsonObject? arguments = null, string client = "agent 1.0", string? session = "s1", string? resume = null, int? expires = null, string reason = "Go to Limsa for the vendor") =>
        Queue.Submit(new TicketRequest(tool, arguments ?? new JsonObject { ["destination"] = "Limsa Lominsa" }, reason, resume, expires, client, session));

    public async Task<Ticket> WaitForAsync(string id, TicketState state)
    {
        for (var i = 0; i < 400; i++)
        {
            if (Queue.Get(id) is { } t && t.State == state)
                return t;
            await Task.Delay(10);
        }

        throw new TimeoutException($"ticket {id} never reached {state}; now {Queue.Get(id)?.State}");
    }

    /// <summary>Disposes the queue (flushing it to disk) and opens a new one on the same file, like a plugin reload.</summary>
    public void Reload()
    {
        Queue.Dispose();
        Queue = new ApprovalQueue(Config, Store, Runner, Confirmations, Time);
        Queue.ActivityRecorded += a => Activity.Enqueue(a);
    }

    public void Dispose()
    {
        Queue.Dispose();
        Confirmations.Dispose();
        Sessions.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // temp cleanup only
        }
    }
}

[Collection(ChatCommandsCollection.Name)]
public class ApprovalQueueTests
{
    [Fact]
    public async Task PendingTicketIsApprovedThenExecutedWithItsResult()
    {
        using var f = new ApprovalFixture();
        var ticket = f.Submit(resume: "step-3");
        Assert.Equal(TicketState.Pending, ticket.State);
        Assert.StartsWith("t_", ticket.Id);
        Assert.Equal(ToolPermission.Action, ticket.Tier);
        Assert.Equal("step-3", ticket.ResumeToken);
        Assert.Equal(1, f.Queue.PendingPosition(ticket.Id));
        Assert.Equal(1, f.Queue.PendingCount);

        f.Time.Advance(TimeSpan.FromDays(30)); // no expiry set: waiting never turns into a denial
        f.Queue.Tick();
        Assert.Equal(TicketState.Pending, f.Queue.Get(ticket.Id)!.State);
        Assert.Empty(f.Runner.Runs);

        Assert.True(f.Queue.Approve(ticket.Id));
        var done = await f.WaitForAsync(ticket.Id, TicketState.Executed);
        Assert.Equal("player", done.DecidedBy);
        Assert.NotNull(done.CompletedAt);
        Assert.Contains("ran teleport", done.ResultJson);
        var run = Assert.Single(f.Runner.Runs);
        Assert.Equal("""{"destination":"Limsa Lominsa"}""", run.Arguments);
        Assert.Equal(("agent 1.0", "s1"), (run.Client, run.Session));
        Assert.False(f.Queue.Approve(ticket.Id)); // final tickets cannot be decided again
        Assert.Equal(["tickets/request", "tickets/approve", "tickets/execute"], f.Activity.Select(a => a.Method));
    }

    [Fact]
    public async Task DeniedCancelledFailedAndExpiredTicketsNeverRunTwice()
    {
        using var f = new ApprovalFixture();
        var denied = f.Submit();
        Assert.True(f.Queue.Deny(denied.Id));
        Assert.Equal(TicketState.Denied, f.Queue.Get(denied.Id)!.State);

        var cancelled = f.Submit();
        Assert.Throws<McpToolException>(() => f.Queue.Cancel(cancelled.Id, "someone-else 1.0"));
        Assert.Equal(TicketState.Cancelled, f.Queue.Cancel(cancelled.Id, "agent 1.0").State);
        Assert.Contains("not pending", Assert.Throws<McpToolException>(() => f.Queue.Cancel(cancelled.Id, "agent 1.0")).Message);

        var expiring = f.Submit(expires: 60);
        f.Time.Advance(TimeSpan.FromSeconds(59));
        f.Queue.Tick();
        Assert.Equal(TicketState.Pending, f.Queue.Get(expiring.Id)!.State);
        f.Time.Advance(TimeSpan.FromSeconds(2));
        f.Queue.Tick();
        Assert.Equal(TicketState.Expired, f.Queue.Get(expiring.Id)!.State);
        Assert.False(f.Queue.Approve(expiring.Id));

        f.Runner.Result = _ => new ToolExecutionResult(new JsonObject { ["isError"] = true }, true, "Tool 'teleport' requires a logged-in character");
        var failing = f.Submit();
        f.Queue.Approve(failing.Id);
        var failed = await f.WaitForAsync(failing.Id, TicketState.Failed);
        Assert.Contains("logged-in character", failed.Error);
        Assert.Single(f.Runner.Runs);
        Assert.Contains(f.Activity, a => a.Method == "tickets/execute" && !a.Success);
    }

    [Fact]
    public async Task ApprovedTicketsRunOneAtATimeInApprovalOrder()
    {
        using var f = new ApprovalFixture();
        f.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = f.Submit("teleport");
        var b = f.Submit("set_target", new JsonObject { ["name"] = "Dummy" });
        var c = f.Submit("teleport");
        f.Queue.Approve(c.Id);
        Assert.Equal(2, f.Queue.ApproveAllFrom("agent 1.0"));
        f.Runner.Gate.SetResult();
        await f.WaitForAsync(b.Id, TicketState.Executed);
        await f.WaitForAsync(a.Id, TicketState.Executed);
        await f.WaitForAsync(c.Id, TicketState.Executed);
        Assert.Equal(["teleport", "teleport", "set_target"], f.Runner.Runs.Select(r => r.Tool));
        Assert.Equal(0, f.Queue.PendingCount);
    }

    [Fact]
    public void TicketsSurviveAReloadAndInterruptedOnesAreNotReRun()
    {
        using var f = new ApprovalFixture();
        var pending = f.Submit(resume: "r1", expires: 3600);
        var denied = f.Submit(tool: "send_chat", arguments: new JsonObject { ["message"] = "hi" });
        f.Queue.Deny(denied.Id);

        f.Reload();
        var reloaded = f.Queue.Get(pending.Id)!;
        Assert.Equal(pending with { }, reloaded);
        Assert.Equal(TicketState.Denied, f.Queue.Get(denied.Id)!.State);
        Assert.Equal(ToolPermission.Chat, f.Queue.Get(denied.Id)!.Tier);

        // A ticket that was running when the game closed: saved as Approved, loaded as Failed, never executed again.
        var running = pending with { Id = "t_interrupted", State = TicketState.Approved, DecidedBy = "player", DecidedAt = f.Time.GetUtcNow() };
        f.Queue.Dispose(); // the game closes: final save, then the file is edited as if the ticket had been running
        f.Store.Save([.. f.Queue.Snapshot(), running]);
        f.Reload();
        var interrupted = f.Queue.Get("t_interrupted")!;
        Assert.Equal(TicketState.Failed, interrupted.State);
        Assert.Contains("Interrupted", interrupted.Error);
        Thread.Sleep(50);
        Assert.Empty(f.Runner.Runs);
    }

    [Fact]
    public void UnreadableStoreStartsEmptyAndKeepsTheFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xivmcp-tickets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, TicketStore.FileName), "{ not json");
        using var f = new ApprovalFixture(dir);
        Assert.Empty(f.Queue.Snapshot());
        Assert.Single(Directory.GetFiles(dir, TicketStore.FileName + ".unreadable-*"));
    }

    [Fact]
    public void QueueLimitsArePerClientWithClearErrors()
    {
        using var f = new ApprovalFixture();
        for (var i = 0; i < ApprovalQueue.MaxPendingPerClient; i++)
            f.Submit();
        var full = Assert.Throws<McpToolException>(() => f.Submit());
        Assert.Contains("Approval queue full for client 'agent 1.0'", full.Message);
        Assert.Contains("cancel_ticket", full.Message);

        Assert.Equal(TicketState.Pending, f.Submit(client: "other 2.0").State);
        f.Queue.Cancel(f.Queue.Snapshot()[0].Id, "agent 1.0");
        Assert.Equal(TicketState.Pending, f.Submit().State);
    }

    [Fact]
    public void RequestsAreValidatedBeforeTheyReachThePlayer()
    {
        using var f = new ApprovalFixture();
        Assert.Contains("needs no approval", Assert.Throws<McpToolException>(() => f.Submit("get_player")).Message);
        Assert.Contains("Unknown tool", Assert.Throws<McpToolException>(() => f.Submit("nope")).Message);
        Assert.Contains("Invalid arguments", Assert.Throws<McpToolException>(() => f.Submit(arguments: new JsonObject { ["invalid"] = 1 })).Message);
        Assert.Contains("reason", Assert.Throws<McpToolException>(() => f.Submit(reason: "  ")).Message);
        Assert.Contains("reason is too long", Assert.Throws<McpToolException>(() => f.Submit(reason: new string('r', 301))).Message);
        Assert.Contains("resumeToken", Assert.Throws<McpToolException>(() => f.Submit(resume: new string('x', 513))).Message);
        Assert.Contains("expiresInSeconds", Assert.Throws<McpToolException>(() => f.Submit(expires: 5)).Message);
        Assert.Contains("too large", Assert.Throws<McpToolException>(() => f.Submit(arguments: new JsonObject { ["message"] = new string('m', 17000) })).Message);
        Assert.Contains("cannot be queued", Assert.Throws<McpToolException>(() => f.Submit(ConfirmationService.ExecuteCommandTool, new JsonObject { ["command"] = "/logout" })).Message);

        var chat = f.Submit(ConfirmationService.ExecuteCommandTool, new JsonObject { ["command"] = "/p pulling in 5" });
        Assert.Equal(ToolPermission.Chat, chat.Tier);
        Assert.Equal(ToolPermission.Action, chat.Permission);

        f.Config.AllowAction = false;
        Assert.Contains("queue is closed", Assert.Throws<McpToolException>(() => f.Submit()).Message);
        Assert.Empty(f.Runner.Runs);
    }

    [Fact]
    public void DisablingTheActionTierDeniesEverythingWaitingAndChatOnlyChat()
    {
        using var f = new ApprovalFixture();
        var action = f.Submit();
        var chat = f.Submit("send_chat", new JsonObject { ["message"] = "hello" });

        f.Config.AllowChat = false;
        Assert.Equal(1, f.Queue.EnforcePermissions());
        Assert.Equal(TicketState.Denied, f.Queue.Get(chat.Id)!.State);
        Assert.Equal("system", f.Queue.Get(chat.Id)!.DecidedBy);
        Assert.Equal(TicketState.Pending, f.Queue.Get(action.Id)!.State);

        f.Config.AllowAction = false;
        Assert.Equal(1, f.Queue.EnforcePermissions());
        Assert.Equal(TicketState.Denied, f.Queue.Get(action.Id)!.State);
        Assert.Contains("Action permission tier was disabled", f.Queue.Get(action.Id)!.Error);
        Assert.Equal(0, f.Queue.PendingCount);
    }

    [Fact]
    public async Task SessionsGrantsAndDisabledConfirmationApproveQueuedCallsAtOnce()
    {
        using var f = new ApprovalFixture();
        f.Sessions.Start("agent 1.0", "s1", includeChat: false);

        var covered = f.Submit();
        Assert.Equal(TicketState.Approved, covered.State);
        Assert.Equal("session", covered.DecidedBy);
        await f.WaitForAsync(covered.Id, TicketState.Executed);

        Assert.Equal(TicketState.Pending, f.Submit("send_chat", new JsonObject { ["message"] = "hi" }).State); // Chat needs its own checkbox
        Assert.Equal(TicketState.Pending, f.Submit(session: "s2").State); // another MCP session of the same name
        Assert.Equal(TicketState.Pending, f.Submit(client: "other 1.0").State);

        f.Sessions.RevokeAll();
        Assert.Equal(TicketState.Pending, f.Submit().State);

        f.Config.ConfirmActions = false;
        var unconfirmed = f.Submit(client: "anyone");
        Assert.Equal("confirmation off", unconfirmed.DecidedBy);
        await f.WaitForAsync(unconfirmed.Id, TicketState.Executed);
    }

    [Fact]
    public async Task LargeResultsAreCappedButKeepTheirShape()
    {
        using var f = new ApprovalFixture();
        f.Runner.Result = _ => new ToolExecutionResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = new string('z', 100_000) }) }, false, null);
        var t = f.Submit();
        f.Queue.Approve(t.Id);
        var done = await f.WaitForAsync(t.Id, TicketState.Executed);
        Assert.True(done.ResultJson!.Length <= ApprovalQueue.MaxResultLength);
        var result = JsonNode.Parse(done.ResultJson)!;
        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.StartsWith("zzz", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ActivityNeverCarriesArguments()
    {
        using var f = new ApprovalFixture();
        const string secret = "SECRET-TOKEN-4f9a";
        f.Runner.Result = _ => new ToolExecutionResult(new JsonObject { ["isError"] = true }, true, "Unknown gearset.");
        var t = f.Submit("set_target", new JsonObject { ["name"] = secret });
        f.Queue.Approve(t.Id);
        await f.WaitForAsync(t.Id, TicketState.Failed);
        f.Queue.Cancel(f.Submit(arguments: new JsonObject { ["x"] = secret }).Id, "agent 1.0");

        Assert.NotEmpty(f.Activity);
        foreach (var a in f.Activity)
        {
            Assert.DoesNotContain(secret, a.Ticket.ActivityTarget);
            Assert.DoesNotContain(secret, a.Error ?? "");
            Assert.DoesNotContain(secret, a.Method);
        }

        Assert.Equal($"set_target #{t.Id}", t.ActivityTarget);
        Assert.Equal("left" + '\\' + "u202Eright", ConfirmationService.ShowInvisible(U.S("left<202E>right")));
    }

    [Fact]
    public async Task ProviderShowsOnlyTheCallersTicketsAndNotifiesSubscribers()
    {
        using var f = new ApprovalFixture();
        var notifier = new RecordingNotifier();
        using var provider = new TicketProvider(f.Queue, f.Sessions, notifier);
        var mine = new ToolContext { Game = null!, Notifier = notifier, CancellationToken = default, ClientName = "agent 1.0", SessionId = "s1" };
        var theirs = new ToolContext { Game = null!, Notifier = notifier, CancellationToken = default, ClientName = "other 1.0", SessionId = "s9" };

        var dto = provider.RequestAction("teleport", "Go to Limsa", new JsonObject { ["destination"] = "Limsa" }, "resume-7", null, mine);
        Assert.Equal("pending", dto.State);
        Assert.Equal("action", dto.Tier);
        Assert.Equal(1, dto.QueuePosition);
        Assert.Equal($"ffxiv://tickets/{dto.Id}", dto.Uri);
        Assert.Contains(TicketProvider.TicketsUri, notifier.Updated);
        Assert.Contains(dto.Uri, notifier.Updated);

        Assert.Throws<McpToolException>(() => provider.GetTicket(dto.Id, theirs));
        Assert.Null(provider.ReadTicket(dto.Id, theirs));
        Assert.Equal(0, provider.ListTickets("all", null, theirs).Count);
        Assert.Single(provider.ListTickets("open", "resume-7", mine).Tickets);
        Assert.Empty(provider.ListTickets("final", null, mine).Tickets);

        f.Queue.Approve(dto.Id);
        await f.WaitForAsync(dto.Id, TicketState.Executed);
        var done = provider.GetTicket(dto.Id, mine);
        Assert.Equal("executed", done.State);
        Assert.Equal("ran teleport", done.Result!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(provider.ListTickets("open", null, mine).SessionActiveUntil);

        f.Sessions.Start("agent 1.0", "s1", includeChat: false);
        Assert.NotNull(provider.ListTickets("open", null, mine).SessionActiveUntil);
    }

    private sealed class RecordingNotifier : IMcpNotifier
    {
        public readonly ConcurrentBag<string> Updated = [];

        public void ResourceUpdated(string uri) => Updated.Add(uri);

        public void ResourceListChanged()
        {
        }

        public void ToolListChanged()
        {
        }

        public void PromptListChanged()
        {
        }

        public void Log(McpLogLevel level, string logger, object? data)
        {
        }
    }
}

using XivMcp.Core;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

[Collection(ChatCommandsCollection.Name)]
public class ApprovalSessionTests
{
    private static (ApprovalSessionService Sessions, Configuration Config, ManualTimeProvider Time) Create(int minutes = 5)
    {
        var config = new Configuration { AllowAction = true, AllowChat = true, ConfirmActions = true, ApprovalSessionMinutes = minutes };
        var time = new ManualTimeProvider();
        return (new ApprovalSessionService(config, time), config, time);
    }

    [Fact]
    public void SessionCoversActionButChatOnlyWhenAllowedSeparately()
    {
        var (sessions, _, _) = Create();
        sessions.Start("agent 1.0", "s1", includeChat: false);
        Assert.True(sessions.Covers("agent 1.0", "s1", ToolPermission.Action));
        Assert.False(sessions.Covers("agent 1.0", "s1", ToolPermission.Chat));
        Assert.False(sessions.Covers("agent 1.0", "s1", ToolPermission.Ui)); // below Action: not the session's business
        Assert.False(sessions.Covers("agent 1.1", "s1", ToolPermission.Action));
        Assert.False(sessions.Covers("agent 1.0", "s2", ToolPermission.Action));
        Assert.False(sessions.Covers(null, "s1", ToolPermission.Action));

        sessions.Start("agent 1.0", "s1", includeChat: true); // replaces the first
        Assert.Single(sessions.Active());
        Assert.True(sessions.Covers("agent 1.0", "s1", ToolPermission.Chat));

        sessions.Start("stateless 2.0", null, includeChat: false); // no MCP session: name only
        Assert.True(sessions.Covers("stateless 2.0", "anything", ToolPermission.Action));
    }

    [Fact]
    public void SessionsAreTimeBoundAndTheLengthIsConfigurable()
    {
        var (sessions, config, time) = Create(minutes: 5);
        var ended = new List<(ApprovalSession, ApprovalSessionEnd)>();
        sessions.Ended += (s, why) => ended.Add((s, why));
        var session = sessions.Start("agent 1.0", "s1", includeChat: false);
        Assert.Equal(TimeSpan.FromMinutes(5), session.Expires - session.StartedAt);

        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.True(sessions.Covers("agent 1.0", "s1", ToolPermission.Action));
        Assert.Equal(TimeSpan.FromSeconds(1), session.Remaining(time.GetUtcNow()));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(sessions.Covers("agent 1.0", "s1", ToolPermission.Action)); // expired even before the tick removes it
        sessions.Tick();
        Assert.Equal((session, ApprovalSessionEnd.Expired), Assert.Single(ended));
        Assert.Empty(sessions.Active());

        config.ApprovalSessionMinutes = 60;
        Assert.Equal(TimeSpan.FromMinutes(60), sessions.Duration);
        config.ApprovalSessionMinutes = 999;
        Assert.Equal(TimeSpan.FromMinutes(60), sessions.Duration);
        config.ApprovalSessionMinutes = 0;
        Assert.Equal(TimeSpan.FromMinutes(1), sessions.Duration);

        var normalized = new Configuration { ApprovalSessionMinutes = 500 };
        Assert.True(normalized.Normalize());
        Assert.Equal(60, normalized.ApprovalSessionMinutes);
        Assert.Equal(5, new Configuration().ApprovalSessionMinutes);
    }

    [Fact]
    public void RevokeEndsOneOrAllAndSessionsAreNotPersisted()
    {
        var (sessions, config, time) = Create();
        var started = new List<ApprovalSession>();
        var ended = new List<ApprovalSessionEnd>();
        sessions.Started += started.Add;
        sessions.Ended += (_, why) => ended.Add(why);
        var a = sessions.Start("a", "s1", includeChat: false);
        sessions.Start("b", "s2", includeChat: true);
        Assert.Equal(2, started.Count);

        Assert.True(sessions.Revoke(a.Id));
        Assert.False(sessions.Revoke(a.Id));
        Assert.False(sessions.Covers("a", "s1", ToolPermission.Action));
        sessions.RevokeAll(ApprovalSessionEnd.PermissionsChanged);
        Assert.Equal([ApprovalSessionEnd.Revoked, ApprovalSessionEnd.PermissionsChanged], ended);
        Assert.Empty(sessions.Active());

        // Nothing about sessions lives in the saved configuration: a new service (plugin reload, game restart) starts empty.
        sessions.Start("c", "s3", includeChat: true);
        var saved = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        Assert.DoesNotContain("\"c\"", saved);
        using var reloaded = new ApprovalSessionService(config, time);
        Assert.Empty(reloaded.Active());
        sessions.Dispose();
        Assert.Equal(ApprovalSessionEnd.Unloaded, ended[^1]);
    }

    [Fact]
    public async Task SynchronousCallsFromACoveredClientSkipThePrompt()
    {
        var (sessions, config, time) = Create();
        using var confirmations = new ConfirmationService(config, time) { Sessions = sessions };
        sessions.Start("agent 1.0", "s1", includeChat: false);

        Assert.True(await confirmations.ApproveToolCallAsync(new ToolCallApprovalRequest("teleport", ToolPermission.Action, "agent 1.0", "s1", null), default));
        Assert.False(confirmations.HasPending);

        // Chat (here an execute_command line that posts chat) still asks, as does the same name from another MCP session.
        var chat = confirmations.ApproveToolCallAsync(new ToolCallApprovalRequest(ConfirmationService.ExecuteCommandTool, ToolPermission.Action, "agent 1.0", "s1", """{"command":"/say hi"}"""), default);
        var otherSession = confirmations.ApproveToolCallAsync(new ToolCallApprovalRequest("teleport", ToolPermission.Action, "agent 1.0", "s2", null), default);
        for (var i = 0; i < 200 && confirmations.Snapshot().Count < 2; i++)
            await Task.Delay(5);
        var waiting = confirmations.Snapshot();
        Assert.Equal(2, waiting.Count);
        Assert.Contains(waiting, p => p.Tier == ToolPermission.Chat && p.SessionId == "s1");
        confirmations.DenyAll();
        Assert.False(await chat);
        Assert.False(await otherSession);

        Assert.True(confirmations.PassesWithoutPrompt("teleport", ToolPermission.Action, "agent 1.0", "s1", out var how));
        Assert.Equal("session", how);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(confirmations.PassesWithoutPrompt("teleport", ToolPermission.Action, "agent 1.0", "s1", out _));
    }
}

using XivMcp.Core;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

[Collection(ChatCommandsCollection.Name)]
public class ConfirmationServiceTests
{
    private static (ConfirmationService Service, Configuration Config, ManualTimeProvider Time) Create(bool allowChat = true)
    {
        var config = new Configuration { ConfirmActions = true, ConfirmTimeoutSeconds = 20, AllowAction = true, AllowChat = allowChat };
        var time = new ManualTimeProvider();
        return (new ConfirmationService(config, time), config, time);
    }

    private static async Task<PendingConfirmation> WaitForPendingAsync(ConfirmationService service, int count = 1)
    {
        for (var i = 0; i < 500; i++)
        {
            var snapshot = service.Snapshot();
            if (snapshot.Count >= count)
                return snapshot[^1];
            await Task.Delay(5);
        }

        throw new TimeoutException("no pending confirmation");
    }

    [Fact]
    public async Task ReadAndDisabledConfirmationPassWithoutPrompt()
    {
        var (service, config, _) = Create();
        Assert.True(await service.ApproveToolCallAsync("get_player", ToolPermission.Read, "c", null, default));
        config.ConfirmActions = false;
        Assert.True(await service.ApproveToolCallAsync("teleport", ToolPermission.Action, "c", null, default));
        Assert.True(await service.ApproveToolCallAsync("set_map_flag", ToolPermission.Ui, "c", null, default));
        Assert.True(await service.ApproveToolCallAsync("send_chat", ToolPermission.Chat, "c", null, default));
        Assert.False(service.HasPending);
    }

    /// <summary>The server only sends a Ui tool here when it asked for approval (the map flag, opening a window): it is prompted like an Action.</summary>
    [Fact]
    public async Task AUiToolThatReachesTheGateIsPromptedAndCarriesItsSummary()
    {
        var (service, _, _) = Create();
        var call = service.ApproveToolCallAsync(
            new ToolCallApprovalRequest("set_map_flag", ToolPermission.Ui, "c", null, """{"x":11.2}""") { Summary = "Place the map flag at 11.2, 9" }, default);
        var pending = await WaitForPendingAsync(service);
        Assert.Equal(ToolPermission.Action, pending.Tier);
        Assert.Equal("Place the map flag at 11.2, 9", pending.Summary);
        service.Resolve(pending.Id, ConfirmationDecision.Allow);
        Assert.True(await call);
    }

    [Fact]
    public async Task AllowAndDenyResolveThePendingCall()
    {
        var (service, _, _) = Create();
        var allowed = service.ApproveToolCallAsync("set_target", ToolPermission.Action, "claude-code 1.0", """{"name":"Striking Dummy"}""", default);
        var request = await WaitForPendingAsync(service);
        Assert.Equal("set_target", request.ToolName);
        Assert.Equal(ToolPermission.Action, request.Tier);
        Assert.Equal("claude-code 1.0", request.ClientName);
        Assert.Contains("\"name\": \"Striking Dummy\"", request.Arguments);
        Assert.Equal(request.CreatedAt + TimeSpan.FromSeconds(20), request.Deadline);
        service.Resolve(request.Id, ConfirmationDecision.Allow);
        Assert.True(await allowed);
        Assert.False(service.HasPending);

        var denied = service.ApproveToolCallAsync("set_target", ToolPermission.Action, "claude-code 1.0", null, default);
        service.Resolve((await WaitForPendingAsync(service)).Id, ConfirmationDecision.Deny);
        Assert.False(await denied);
        Assert.Empty(service.Grants());
    }

    [Fact]
    public async Task CancellationAndFallbackTimeoutNeverApprove()
    {
        var (service, _, time) = Create();
        using var cts = new CancellationTokenSource();
        var cancelled = service.ApproveToolCallAsync("teleport", ToolPermission.Action, "c", null, cts.Token);
        await WaitForPendingAsync(service);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(service.HasPending);

        var unanswered = service.ApproveToolCallAsync("teleport", ToolPermission.Action, "c", null, default);
        await WaitForPendingAsync(service);
        time.Advance(TimeSpan.FromSeconds(21));
        Assert.False(unanswered.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => unanswered);
        Assert.False(service.HasPending);
    }

    [Fact]
    public async Task AllowForAWhileGrantsSameToolTierAndClientForTenMinutes()
    {
        var (service, _, time) = Create();
        var first = service.ApproveToolCallAsync("equip_gearset", ToolPermission.Action, "client-a", null, default);
        var second = service.ApproveToolCallAsync("equip_gearset", ToolPermission.Action, "client-a", null, default);
        var request = await WaitForPendingAsync(service, 2);
        service.Resolve(request.Id, ConfirmationDecision.AllowForAWhile);
        Assert.True(await first);
        Assert.True(await second); // the other pending call with the same key is covered too
        Assert.Single(service.Grants());

        Assert.True(await service.ApproveToolCallAsync("equip_gearset", ToolPermission.Action, "client-a", null, default));

        var otherClient = service.ApproveToolCallAsync("equip_gearset", ToolPermission.Action, "client-b", null, default);
        service.Resolve((await WaitForPendingAsync(service)).Id, ConfirmationDecision.Deny);
        Assert.False(await otherClient);

        var otherTool = service.ApproveToolCallAsync("teleport", ToolPermission.Action, "client-a", null, default);
        service.Resolve((await WaitForPendingAsync(service)).Id, ConfirmationDecision.Deny);
        Assert.False(await otherTool);

        time.Advance(ConfirmationService.GrantDuration);
        Assert.Empty(service.Grants());
        var expired = service.ApproveToolCallAsync("equip_gearset", ToolPermission.Action, "client-a", null, default);
        service.Resolve((await WaitForPendingAsync(service)).Id, ConfirmationDecision.AllowForAWhile);
        Assert.True(await expired);

        service.RevokeGrants();
        var revoked = service.ApproveToolCallAsync("equip_gearset", ToolPermission.Action, "client-a", null, default);
        Assert.True(service.HasPending || !revoked.IsCompleted);
        service.DenyAll();
        Assert.False(await revoked);
    }

    [Fact]
    public async Task ExecuteCommandChatCommandsAreConfirmedAsChat()
    {
        var (service, config, _) = Create();
        var chat = service.ApproveToolCallAsync(ConfirmationService.ExecuteCommandTool, ToolPermission.Action, "c", """{"command":"/p pull in 5"}""", default);
        var request = await WaitForPendingAsync(service);
        Assert.Equal(ToolPermission.Chat, request.Tier);
        Assert.Equal(ToolPermission.Action, request.Permission);
        service.Resolve(request.Id, ConfirmationDecision.AllowForAWhile);
        Assert.True(await chat);

        // The grant covers chat commands only: a non-chat execute_command still asks.
        var gearset = service.ApproveToolCallAsync(ConfirmationService.ExecuteCommandTool, ToolPermission.Action, "c", """{"command":"/gearset change 2"}""", default);
        var gearsetRequest = await WaitForPendingAsync(service);
        Assert.Equal(ToolPermission.Action, gearsetRequest.Tier);
        service.Resolve(gearsetRequest.Id, ConfirmationDecision.Deny);
        Assert.False(await gearset);

        // Calls the tool refuses by itself are not put in front of the player.
        Assert.True(await service.ApproveToolCallAsync(ConfirmationService.ExecuteCommandTool, ToolPermission.Action, "c", """{"command":"/logout"}""", default));
        config.AllowChat = false;
        Assert.True(await service.ApproveToolCallAsync(ConfirmationService.ExecuteCommandTool, ToolPermission.Action, "x", """{"command":"/shout hi"}""", default));
        Assert.False(service.HasPending);
    }

    [Fact]
    public void ArgumentsAreReadableButInvisibleCharactersAreShown()
    {
        var json = U.S("""{"message":"<30CF><30A4> left<202E>right zero<200B>width","n":1}""");
        var (text, truncated) = ConfirmationService.FormatArguments(json);
        Assert.False(truncated);
        Assert.Contains(U.S("<30CF><30A4>"), text);
        Assert.Contains("\\u202E", text);
        Assert.Contains("\\u200B", text);
        Assert.DoesNotContain(U.S("<202E>"), text, StringComparison.Ordinal);
        Assert.Contains("\n", text); // indented

        Assert.Equal((null, false), ConfirmationService.FormatArguments(null));
        Assert.Equal((null, false), ConfirmationService.FormatArguments("{}"));
        var (longText, longTruncated) = ConfirmationService.FormatArguments($$"""{"message":"{{new string('x', 5000)}}"}""");
        Assert.True(longTruncated);
        Assert.Equal(ConfirmationService.MaxArgumentsLength, longText!.Length);
    }

    [Fact]
    public async Task DisposeDeniesPendingAndLaterCalls()
    {
        var (service, _, _) = Create();
        var pending = service.ApproveToolCallAsync("teleport", ToolPermission.Action, "c", null, default);
        await WaitForPendingAsync(service);
        service.Dispose();
        Assert.False(await pending);
        Assert.False(await service.ApproveToolCallAsync("teleport", ToolPermission.Action, "c", null, default));
    }
}

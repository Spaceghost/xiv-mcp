using System.Text.Json.Nodes;
using XivMcp.Core;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

[Collection(ChatCommandsCollection.Name)]
public class AutoApprovePolicyTests
{
    private const string Exec = ConfirmationService.ExecuteCommandTool;

    private static readonly AutoApproveRule CiRule = new()
    {
        Client = "ghostty-ci",
        Tool = Exec,
        Prefixes = ["/term selftest", "/xivmcp quests"],
    };

    private static string Command(string command) => new JsonObject { ["command"] = command }.ToJsonString();

    private static AutoApproveMatch? Match(string command, string? token = "ghostty-ci", ToolPermission tier = ToolPermission.Action, params AutoApproveRule[] rules) =>
        AutoApprovePolicy.Match(rules.Length == 0 ? [CiRule] : rules, token, Exec, tier, Command(command));

    [Theory]
    [InlineData("/term selftest")]
    [InlineData("/term selftest --fast")]
    [InlineData("/term selftest suite=smoke,retry=2")]
    [InlineData("/xivmcp quests")]
    [InlineData("/xivmcp quests list \"Main Scenario\"")]
    public void AllowedCommandsMatch(string command)
    {
        var match = Match(command);
        Assert.NotNull(match);
        Assert.Same(CiRule, match.Rule);
        Assert.StartsWith(match.Prefix!, command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/term selftestx")]              // no word boundary
    [InlineData("/term selftest2")]
    [InlineData("/term selftest; /say hi")]      // separators and shell-like tricks
    [InlineData("/term selftest;/say hi")]
    [InlineData("/term selftest | /say hi")]
    [InlineData("/term selftest && /say hi")]
    [InlineData("/term selftest $(say hi)")]
    [InlineData("/term selftest `say`")]
    [InlineData("/term selftest > out")]
    [InlineData("/term selftest \\ /say")]
    [InlineData("/term selftest\n/say hi")]      // line breaks and control characters
    [InlineData("/term selftest\r\n/say hi")]
    [InlineData("/term selftest\t--fast")]
    [InlineData("/term selftest\0")]
    [InlineData(" /term selftest")]              // no trimming or case folding
    [InlineData("/TERM selftest")]
    [InlineData("/term  selftest")]
    [InlineData("/term")]
    [InlineData("/say /term selftest")]
    [InlineData("")]
    public void EverythingElseDoesNotMatch(string command) => Assert.Null(Match(command));

    [Fact]
    public void InvisibleAndNonAsciiTailsDoNotMatch()
    {
        Assert.Null(Match(U.S("/term selftest <2028>/say hi")));  // line separator
        Assert.Null(Match(U.S("/term selftest <202E>tset")));     // bidi override
        Assert.Null(Match(U.S("/term selftest<200B> x")));        // zero-width space right after the prefix
        Assert.Null(Match(U.S("/term selftest <FF1B>/say hi")));  // fullwidth semicolon
        Assert.Null(Match(U.S("<FF0F>term selftest")));           // fullwidth slash never equals the prefix
    }

    [Fact]
    public void RulesMatchOnlyTheTokenIdentityNotTheSelfReportedName()
    {
        Assert.Null(Match("/term selftest", token: null));          // main token, even if clientInfo says "ghostty-ci"
        Assert.Null(Match("/term selftest", token: "other-ci"));
        Assert.Null(Match("/term selftest", token: "Ghostty-CI"));  // ordinal
        Assert.NotNull(Match("/term selftest", token: "ghostty-ci"));
    }

    [Fact]
    public void ChatDisabledRulesAndOtherToolsDoNotMatch()
    {
        var chatRule = new AutoApproveRule { Client = "ghostty-ci", Tool = Exec, Prefixes = ["/p ready"] };
        Assert.Null(Match("/p ready", tier: ToolPermission.Chat, rules: chatRule));
        chatRule.IncludeChat = true;
        Assert.NotNull(Match("/p ready", tier: ToolPermission.Chat, rules: chatRule));

        var disabled = CiRule.Clone();
        disabled.Enabled = false;
        Assert.Null(Match("/term selftest", rules: disabled));

        Assert.Null(AutoApprovePolicy.Match([CiRule], "ghostty-ci", "teleport", ToolPermission.Action, Command("/term selftest")));
        Assert.Null(AutoApprovePolicy.Match([CiRule], "ghostty-ci", Exec, ToolPermission.Ui, Command("/term selftest"))); // nothing to approve
        Assert.Null(AutoApprovePolicy.Match([CiRule], "ghostty-ci", Exec, ToolPermission.Action, """{"command":42}"""));
        Assert.Null(AutoApprovePolicy.Match([CiRule], "ghostty-ci", Exec, ToolPermission.Action, "not json"));
    }

    [Fact]
    public void EmptyPrefixesMeanAnyArgumentsExceptForExecuteCommand()
    {
        var anyCommand = new AutoApproveRule { Client = "ghostty-ci", Tool = Exec };
        Assert.Null(Match("/term selftest", rules: anyCommand));

        var anyTarget = new AutoApproveRule { Client = "ghostty-ci", Tool = "set_target" };
        var match = AutoApprovePolicy.Match([anyTarget], "ghostty-ci", "set_target", ToolPermission.Action, """{"name":"Striking Dummy"}""");
        Assert.NotNull(match);
        Assert.Null(match.Prefix);
        Assert.Equal("rule ghostty-ci/set_target: any arguments", match.Describe());

        var namedArgument = new AutoApproveRule { Client = "ghostty-ci", Tool = "teleport", Argument = "destination", Prefixes = ["Limsa Lominsa"] };
        Assert.NotNull(AutoApprovePolicy.Match([namedArgument], "ghostty-ci", "teleport", ToolPermission.Action, """{"destination":"Limsa Lominsa"}"""));
        Assert.Null(AutoApprovePolicy.Match([namedArgument], "ghostty-ci", "teleport", ToolPermission.Action, """{"destination":"Gridania"}"""));
    }

    [Fact]
    public void ClientTokensAreStoredHashedAndRevocable()
    {
        var config = new Configuration();
        var token = config.AddClientToken(" ghostty-ci ", DateTimeOffset.UnixEpoch);
        Assert.Equal(43, token.Length);
        var entry = Assert.Single(config.ClientTokens);
        Assert.Equal("ghostty-ci", entry.Name);
        Assert.Equal(Configuration.HashToken(token), entry.TokenSha256);
        Assert.DoesNotContain(token, Newtonsoft.Json.JsonConvert.SerializeObject(config));
        Assert.Equal(new ClientToken("ghostty-ci", entry.TokenSha256), Assert.Single(config.ClientTokenHashes()));

        Assert.Throws<ArgumentException>(() => config.AddClientToken("GHOSTTY-CI", DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => config.AddClientToken("bad name", DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => config.AddClientToken("", DateTimeOffset.UnixEpoch));

        Assert.True(config.RevokeClientToken("ghostty-ci"));
        Assert.False(config.RevokeClientToken("ghostty-ci"));
        Assert.Empty(config.ClientTokenHashes());
    }

    [Fact]
    public void NormalizeDropsUnusableTokensRulesAndPrefixes()
    {
        var config = new Configuration
        {
            ClientTokens =
            [
                new ClientTokenEntry { Name = "ok", TokenSha256 = new string('A', 64) },
                new ClientTokenEntry { Name = "short", TokenSha256 = "ABC" },
                new ClientTokenEntry { Name = "bad name", TokenSha256 = new string('B', 64) },
            ],
            AutoApproveRules =
            [
                new AutoApproveRule { Client = "ok", Tool = Exec, Prefixes = ["/term selftest", "", " leading", U.S("/x<200B>")] },
                new AutoApproveRule { Client = "", Tool = Exec },
            ],
        };
        Assert.True(config.Normalize());
        Assert.Equal("ok", Assert.Single(config.ClientTokens).Name);
        Assert.Equal(["/term selftest"], Assert.Single(config.AutoApproveRules).Prefixes);
    }

    [Fact]
    public async Task ConfirmationAndQueueUseTheRuleAndReportEveryAutoApproval()
    {
        using var f = new ApprovalFixture(configure: c => c.AutoApproveRules = [CiRule.Clone()]);
        var reported = new List<(ToolCallApprovalRequest Call, AutoApproveMatch Match)>();
        f.Confirmations.AutoApproved += (call, _, match) => reported.Add((call, match));

        // Synchronous path: a matching call from the token client runs without a prompt.
        var ci = new ToolCallApprovalRequest(Exec, ToolPermission.Action, "ghostty-ci 1.0", null, Command("/term selftest"), "ghostty-ci");
        Assert.True(await f.Confirmations.ApproveToolCallAsync(ci, default));
        Assert.False(f.Confirmations.HasPending);

        // The same name without the token, or a command outside the allowlist, is prompted as usual.
        var spoofed = f.Confirmations.ApproveToolCallAsync(ci with { AuthenticatedClient = null }, default);
        var outside = f.Confirmations.ApproveToolCallAsync(ci with { ArgumentsJson = Command("/term selftest; /say hi") }, default);
        for (var i = 0; i < 200 && f.Confirmations.Snapshot().Count < 2; i++)
            await Task.Delay(5);
        Assert.Equal(2, f.Confirmations.Snapshot().Count);
        Assert.Contains(f.Confirmations.Snapshot(), p => p.AuthenticatedClient == "ghostty-ci");
        f.Confirmations.DenyAll();
        Assert.False(await spoofed);
        Assert.False(await outside);

        // Queue path: request_action from the token client is approved by policy and runs.
        var ticket = f.Submit(Exec, new JsonObject { ["command"] = "/xivmcp quests" }, client: "ghostty-ci 1.0", token: "ghostty-ci");
        Assert.Equal(TicketState.Approved, ticket.State);
        Assert.Equal("policy", ticket.DecidedBy);
        await f.WaitForAsync(ticket.Id, TicketState.Executed);
        Assert.Equal(TicketState.Pending, f.Submit(Exec, new JsonObject { ["command"] = "/gearset change 1" }, client: "ghostty-ci 1.0", token: "ghostty-ci").State);

        Assert.Equal(2, reported.Count);
        Assert.All(reported, r => Assert.Equal("ghostty-ci", r.Call.AuthenticatedClient));
        Assert.Equal(["/term selftest", "/xivmcp quests"], reported.Select(r => r.Match.Prefix));

        // Revoking the token (or disabling the rule) ends it: the server no longer authenticates the token, and a disabled
        // rule never matches.
        f.Config.AutoApproveRules[0].Enabled = false;
        Assert.False(f.Confirmations.PassesWithoutPrompt(ci, ToolPermission.Action, out _));
    }
}

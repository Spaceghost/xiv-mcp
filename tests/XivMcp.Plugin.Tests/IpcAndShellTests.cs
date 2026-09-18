using System.Text.RegularExpressions;
using Umbra.XivMcp;
using XivMcp.Core;
using XivMcp.Plugin.Ipc;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Tests;

public class IpcPayloadTests
{
    private sealed class Tiers(bool read, bool ui, bool action, bool chat) : IHostState
    {
        public bool IsLoggedIn => true;

        public bool IsPermitted(ToolPermission permission) => permission switch
        {
            ToolPermission.Read => read,
            ToolPermission.Ui => ui,
            ToolPermission.Action => action,
            _ => chat,
        };

        public bool IsCategoryEnabled(string category) => true;
    }

    [Fact]
    public void StatusRoundTripsThroughTheUmbraParser()
    {
        var status = new ServerStatus(true, "http://127.0.0.1:41800/mcp", 2, 128, 3, "tools/call x: boom", ["claude-code 2.1", "codex"]);
        var json = IpcJson.Status(true, "http://127.0.0.1:41800/mcp", status, null, new Tiers(true, true, false, true), 4, true);

        var parsed = IpcPayloadParser.ParseStatus(json)!;
        Assert.True(parsed.Running);
        Assert.Equal("http://127.0.0.1:41800/mcp", parsed.Endpoint);
        Assert.Equal(2, parsed.ActiveSessions);
        Assert.Equal(128, parsed.TotalRequests);
        Assert.Equal(3, parsed.FailedRequests);
        Assert.Equal("tools/call x: boom", parsed.LastError);
        Assert.Equal(["claude-code 2.1", "codex"], parsed.ConnectedClients);
        Assert.Equal(new McpPermissions(true, true, false, true), parsed.Permissions);

        // A host error (bind failure) wins over the last request error.
        Assert.Equal("port in use", IpcPayloadParser.ParseStatus(IpcJson.Status(false, "e", status, "port in use", new Tiers(true, true, true, true), 0, false))!.LastError);
    }

    [Fact]
    public void ActivityAndBoardRoundTrip()
    {
        var at = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var activity = IpcPayloadParser.ParseActivity(IpcJson.Activity(
        [
            new ActivityEntry(at, "abc", "claude-code 2.1", "tools/call", "get_player", true, null, 12.5),
            new ActivityEntry(at, null, null, "http POST", "401", false, "Missing bearer token", 0.2),
        ]));
        Assert.Equal(2, activity.Count);
        Assert.Equal(at, activity[0].Timestamp);
        Assert.Equal("get_player", activity[0].Target);
        Assert.Equal(12.5, activity[0].DurationMs);
        Assert.False(activity[1].Success);
        Assert.Equal("Missing bearer token", activity[1].Error);
        Assert.Equal("tools/call get_player", McpFormat.ActivityLabel(activity[0]));

        var board = IpcPayloadParser.ParseAgentBoard(IpcJson.Board(
        [
            new AgentPost("builder", "compiling", AgentState.Running, 0.5, "step 2/4", "claude-code", at, at),
            new AgentPost("docs", "done", AgentState.Done, null, null, null, at, at),
        ]));
        Assert.Equal(2, board.Count);
        Assert.Equal("running", board[0].State);
        Assert.Equal(0.5, board[0].Fraction);
        Assert.Equal("claude-code", board[0].ClientName);
        Assert.Equal("done", board[1].State);
        Assert.Null(board[1].Fraction);

        var snapshot = new McpSnapshot(McpLinkState.Running, 1, IpcPayloadParser.ParseStatus(IpcJson.Status(true, "e", new ServerStatus(true, "e", 2, 5, 0, null, []), null, new Tiers(true, true, false, false), 2, true)), activity, board, null, at);
        Assert.Equal("MCP ● 2 ▶1", McpFormat.WidgetText(snapshot, new WidgetTextOptions(true, true, false)));
    }

    [Fact]
    public void ParserToleratesMalformedPayloads()
    {
        Assert.Null(IpcPayloadParser.ParseStatus(null));
        Assert.Null(IpcPayloadParser.ParseStatus("[]"));
        Assert.Null(IpcPayloadParser.ParseStatus("{not json"));
        Assert.Empty(IpcPayloadParser.ParseActivity("{}"));
        Assert.Single(IpcPayloadParser.ParseAgentBoard("""[{"agent":"a","status":"s","state":"RUNNING","progress":86},{"status":"no name"},42]"""));
    }
}

public class ShellTests
{
    [Fact]
    public void ConfigurationDefaultsAndNormalize()
    {
        var token = Configuration.GenerateToken();
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), token);
        Assert.NotEqual(token, Configuration.GenerateToken());

        var config = new Configuration { BearerToken = "", Port = 70000, CallTimeoutSeconds = 1, ConfirmTimeoutSeconds = 1000 };
        Assert.True(config.Normalize());
        Assert.Equal(43, config.BearerToken.Length);
        Assert.Equal(65535, config.Port);
        Assert.Equal(5, config.CallTimeoutSeconds);
        Assert.Equal(300, config.ConfirmTimeoutSeconds);

        var defaults = new Configuration();
        Assert.True(defaults.AllowRead && defaults.AllowUi && !defaults.AllowAction && !defaults.AllowChat);
        Assert.True(defaults.RequireToken && defaults.ConfirmActions);
        Assert.Equal("127.0.0.1", defaults.Host);

        foreach (var loopback in new[] { "127.0.0.1", "::1", "[::1]", "localhost", "127.5.5.5" })
            Assert.True(Configuration.IsLoopbackHost(loopback), loopback);
        foreach (var remote in new[] { "0.0.0.0", "192.168.1.2", "", "example.com", "::" })
            Assert.False(Configuration.IsLoopbackHost(remote), remote);
    }

    [Fact]
    public void SnippetsNeverShowTheTokenUnlessAskedTo()
    {
        var config = new Configuration { BearerToken = "SECRET_TOKEN_VALUE" };
        Assert.DoesNotContain("SECRET", ClientSnippets.GenericJson(config, TokenDisplay.Masked));
        Assert.DoesNotContain("SECRET", ClientSnippets.ClaudeCodeStatic(config, TokenDisplay.Placeholder));
        Assert.DoesNotContain("SECRET", ClientSnippets.ClaudeCodeHelper(config));
        Assert.Contains("Bearer SECRET_TOKEN_VALUE", ClientSnippets.GenericJson(config, TokenDisplay.Real));
        Assert.DoesNotContain("Authorization", ClientSnippets.GenericJson(new Configuration { BearerToken = "S", RequireToken = false }, TokenDisplay.Real));
    }

    [Fact]
    public void AgentBoardBoundsAndCompletion()
    {
        var board = new AgentBoard(new Configuration { AgentBoardExpiryMinutes = 0 });
        var completed = 0;
        board.Completed += _ => completed++;
        board.Upsert("Claude", "starting", AgentState.Running, 1.7, null, "cc");
        board.Upsert("claude", "half", AgentState.Running, 0.5, "d", "cc");
        Assert.Equal(1, board.Count);
        Assert.Equal(0.5, board.Snapshot()[0].Progress);

        board.Upsert("claude", "ok", AgentState.Done, double.NaN, null, "cc");
        board.Upsert("claude", "ok again", AgentState.Done, null, null, "cc");
        Assert.Equal(1, completed);
        Assert.Null(board.Snapshot()[0].Progress);

        var clamped = board.Upsert("x", new string('a', 500), AgentState.Info, -3, null, null);
        Assert.Equal(0, clamped.Progress);
        Assert.Equal(AgentBoard.MaxStatusLength, clamped.Status.Length);

        for (var i = 0; i < 100; i++)
            board.Upsert($"a{i}", "s", AgentState.Running, null, null, null);
        Assert.Equal(AgentBoard.MaxAgents, board.Count);
        Assert.Equal(1, board.Clear("a99"));
        Assert.Equal(AgentBoard.MaxAgents - 1, board.Clear(null));
        Assert.Throws<ArgumentException>(() => AgentBoard.ParseState("bogus"));
    }
}

using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Tests;

public sealed class ActionLogTests
{
    private static GatedToolExecution Run(string tool = "send_chat", ToolPermission tier = ToolPermission.Chat, bool ok = true, bool preApproved = false, string? args = null) =>
        new(tool, tier, "agent 1.0", "laptop", "s1", "Send to Party: \"hello\"", args, ok, ok ? null : "boom", preApproved);

    [Fact]
    public void RecordsWhoWhatWhenAndWhetherThePlayerWasAsked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xivmcp-actionlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new ActionLog(dir);
            log.Record(Run(), approvalOn: false);
            log.Record(Run(preApproved: true), approvalOn: true);
            log.Record(Run(ok: false), approvalOn: true);

            var recent = log.Recent();
            Assert.Equal(["asked", "ticket", "off"], recent.Select(e => e.Approval));
            Assert.Equal("laptop", recent[0].Token);
            Assert.Equal("boom", recent[0].Error);

            var lines = File.ReadAllLines(Path.Combine(dir, "actions.log"));
            Assert.Equal(3, lines.Length);
            Assert.Contains("\"tool\":\"send_chat\"", lines[0]);
            Assert.Contains("\"approval\":\"off\"", lines[0]);
            Assert.Contains("\"token\":\"laptop\"", lines[0]);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnUnwritableLogNeverFailsTheCall()
    {
        var errors = 0;
        var file = Path.GetTempFileName();
        try
        {
            // A directory path below a regular file cannot be created.
            var log = new ActionLog(Path.Combine(file, "nested"), onError: (_, _) => errors++);
            log.Record(Run(), approvalOn: true);
            log.Record(Run(), approvalOn: true);
            Assert.Equal(2, log.Recent().Count);
            Assert.Equal(1, errors);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void MemoryTailIsBounded()
    {
        var log = new ActionLog(null);
        for (var i = 0; i < ActionLog.MemoryCapacity + 20; i++)
            log.Record(Run(), approvalOn: true);
        Assert.Equal(ActionLog.MemoryCapacity, log.Recent().Count);
    }

    [Theory]
    [InlineData("send_chat", ToolPermission.Chat, null, true)]
    [InlineData("equip_gearset", ToolPermission.Action, null, true)]
    [InlineData("execute_command", ToolPermission.Action, """{"command":"/gearset change 3"}""", true)]
    [InlineData("execute_command", ToolPermission.Action, """{"command":"/gs change 3"}""", true)]
    [InlineData("execute_command", ToolPermission.Action, """{"command":"/hudlayout 2"}""", false)]
    [InlineData("teleport", ToolPermission.Action, null, false)]
    public void ToastsOnlyForChatAndGear(string tool, ToolPermission tier, string? args, bool expected) =>
        Assert.Equal(expected, ActionLog.DeservesToast(tool, tier, args));
}

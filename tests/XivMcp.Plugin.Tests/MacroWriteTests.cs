using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Actions;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>write_macro / clear_macro: everything that is decided before game memory is touched.</summary>
[Collection(ChatCommandsCollection.Name)]
public class MacroWriteTests
{
    private static McpToolException Rejects(Action call) => Assert.Throws<McpToolException>(call);

    private static MacroWriteProvider Create(bool allowChat = false)
    {
        var commands = FakeProxy.Create<ICommandManager>(new()
        {
            ["get_Commands"] = _ => new ReadOnlyDictionary<string, IReadOnlyCommandInfo>(new Dictionary<string, IReadOnlyCommandInfo>()),
        });
        return new MacroWriteProvider(commands, FakeProxy.Create<IDataManager>(), FakeProxy.Create<ICondition>(), new Configuration { AllowAction = true, AllowChat = allowChat })
        {
            IconExists = icon => icon == 66001,
        };
    }

    [Fact]
    public void KeepsInnerBlankLinesAndDropsTrailingOnes()
    {
        var lines = MacroText.CleanLines(["/micon \"Sprint\"", "", "  /echo done\t<se.1> ", " ", null], chatPermitted: false);
        Assert.Equal(["/micon \"Sprint\"", "", "/echo done <se.1>"], lines);
    }

    [Theory]
    [InlineData("/ac Sprint <me>")]
    [InlineData("/ACTION \"Cure\" <t>")]
    [InlineData("/<FF41><FF43> Cure")]
    [InlineData("/a<200B>c Cure")]
    [InlineData("/automove on")]
    [InlineData("/follow")]
    [InlineData("/logout")]
    [InlineData("/xlkill")]
    public void BlockedAndAutomationLinesAreRefused(string line)
    {
        var ex = Rejects(() => MacroText.CleanLines(["/echo start", U.S(line)], chatPermitted: true));
        Assert.Equal(McpErrorCodes.Refused, ex.Code);
        Assert.Contains("lines[1]", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/p pulling")]
    [InlineData("/sh LFG")]
    [InlineData("hello everyone")]
    public void LinesOthersWouldSeeNeedTheChatTier(string line)
    {
        var ex = Rejects(() => MacroText.CleanLines([line], chatPermitted: false));
        Assert.Equal(McpErrorCodes.Refused, ex.Code);
        Assert.Contains("Chat permission tier", ex.Message, StringComparison.Ordinal);
        Assert.Equal([line], MacroText.CleanLines([line], chatPermitted: true));
    }

    [Fact]
    public void OwnCommandsAreRefused()
    {
        var ex = Rejects(() => MacroText.CleanLines(["/xivmcp allow all"], chatPermitted: true, token => token == "/xivmcp"));
        Assert.Equal(McpErrorCodes.Refused, ex.Code);
    }

    [Fact]
    public void LineLimitsAreTheGames()
    {
        Assert.Equal(15, MacroText.MaxLines);
        var sixteen = Enumerable.Repeat("/wait 1", 16).ToArray();
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => MacroText.CleanLines(sixteen, false)).Code);
        Assert.Equal(15, MacroText.CleanLines(sixteen[..15], false).Count);

        var ascii = "/echo " + new string('a', 174);
        Assert.Equal(180, System.Text.Encoding.UTF8.GetByteCount(ascii));
        Assert.Single(MacroText.CleanLines([ascii], false));
        Assert.Contains("181 UTF-8 bytes", Rejects(() => MacroText.CleanLines([ascii + "a"], false)).Message, StringComparison.Ordinal);

        // 60 three-byte characters: 66 chars, 186 bytes.
        var wide = "/echo " + new string('あ', 60);
        Assert.Contains("186 UTF-8 bytes", Rejects(() => MacroText.CleanLines([wide], false)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/echo a<000A>/ac Cure", "single line")]
    [InlineData("/echo a<000D>", "single line")]
    public void ALineCannotSmuggleASecondLine(string line, string expected)
    {
        Assert.Contains(expected, Rejects(() => MacroText.CleanLines([U.S(line)], true)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyInputIsRejected()
    {
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => MacroText.CleanLines(null, false)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => MacroText.CleanLines([], false)).Code);
        Assert.Contains("clear_macro", Rejects(() => MacroText.CleanLines(["", "  "], false)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMacroThatDoesNotFitTheApprovalPromptIsRejected()
    {
        var lines = Enumerable.Repeat("/echo " + new string('x', 60), 10).ToArray();
        var ex = Rejects(() => MacroText.CleanLines(lines, false));
        Assert.Equal(McpErrorCodes.InvalidArguments, ex.Code);
        Assert.Contains("approval prompt", ex.Message, StringComparison.Ordinal);

        // What passes is rendered in full by the server's approval sentence.
        var ok = new[] { "/micon \"Sprint\"", "/echo あ <se.1>" };
        MacroText.CleanLines(ok, false);
        var args = new JsonObject { ["set"] = "shared", ["index"] = 7, ["title"] = "Hi", ["lines"] = new JsonArray(ok[0], ok[1]) };
        var sentence = McpServer.RenderApprovalSummary("slot {index} of {set}, \"{title}\": {lines}", "write_macro", args);
        Assert.Equal("slot 7 of shared, \"Hi\": [\"/micon \\\"Sprint\\\"\",\"/echo あ <se.1>\"]", sentence);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("123456789012345678901", "21 characters")]
    [InlineData("a<000A>b", "single line")]
    public void TitleRules(string title, string expected)
    {
        Assert.Contains(expected, Rejects(() => MacroText.CleanTitle(U.S(title))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleOfTwentyCharactersIsFine() => Assert.Equal("12345678901234567890", MacroText.CleanTitle(" 12345678901234567890 "));

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void SlotRange(int index)
    {
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => MacroText.EnsureSlot(index)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => Create().ClearMacro(MacroProvider.MacroSet.Individual, index)).Code);
    }

    [Fact]
    public void RestorableSaysWhetherTheOldLinesCouldBeWrittenBack()
    {
        Assert.True(MacroText.CanBeWritten([], false));
        Assert.True(MacroText.CanBeWritten(["/echo hi"], false));
        Assert.False(MacroText.CanBeWritten(["/ac Cure <t>"], true));
        Assert.False(MacroText.CanBeWritten(["/p hi"], false));
        Assert.True(MacroText.CanBeWritten(["/p hi"], true));
    }

    [Fact]
    public void WriteMacroRejectsBeforeTouchingTheGame()
    {
        var provider = Create();
        var set = MacroProvider.MacroSet.Shared;
        Assert.Equal(McpErrorCodes.Refused, Rejects(() => provider.WriteMacro(set, 3, "Burst", ["/ac \"Fight or Flight\""])).Code);
        Assert.Equal(McpErrorCodes.Refused, Rejects(() => provider.WriteMacro(set, 3, "Hello", ["/s hello"])).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => provider.WriteMacro(set, 100, "Hello", ["/echo hi"])).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => provider.WriteMacro(set, 3, new string('t', 21), ["/echo hi"])).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => provider.WriteMacro(set, 3, "Icon", ["/echo hi"], iconId: 12345)).Code);
    }
}

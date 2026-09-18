using XivMcp.Core;
using XivMcp.Plugin.Providers.Chat;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

public class ChatTextTests
{
    [Theory]
    [InlineData("  hi<0009>there<0007> ", "hi there")]
    [InlineData("<0002>H<0003>auto-translate", "Hauto-translate")] // SeString payload markers are control characters
    [InlineData("left<202E>right", "leftright")] // bidi override
    [InlineData("zero<200B>width<FEFF>", "zerowidth")]
    [InlineData("soft<00AD>hyphen", "softhyphen")]
    [InlineData("lone<D800>surrogate", "lonesurrogate")]
    [InlineData("emoji <D83D><DE00> ok", "emoji <D83D><DE00> ok")]
    [InlineData("<30CF><30A4><30FB><30A8><30FC><30C6><30EB>", "<30CF><30A4><30FB><30A8><30FC><30C6><30EB>")]
    [InlineData("<E03C> private use glyph", "<E03C> private use glyph")]
    public void CleanSingleLineRemovesInvisibleAndControlCharacters(string input, string expected) =>
        Assert.Equal(U.S(expected), ChatText.CleanSingleLine(U.S(input), "message"));

    [Theory]
    [InlineData("a<000A>b")]
    [InlineData("a<000D><000A>b")]
    [InlineData("a<2028>b")]
    [InlineData("a<2029>b")]
    [InlineData("a<0085>b")]
    public void CleanSingleLineRejectsLineBreaks(string input)
    {
        var ex = Assert.Throws<McpToolException>(() => ChatText.CleanSingleLine(U.S(input), "message"));
        Assert.Contains("single line", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<0001><0002><200B>")]
    public void CleanSingleLineRejectsEmpty(string input) =>
        Assert.Throws<McpToolException>(() => ChatText.CleanSingleLine(U.S(input), "message"));

    [Fact]
    public void ByteLimitCountsUtf8BytesIncludingPrefix()
    {
        ChatText.EnsureByteLimit(new string('a', 500));
        var ex = Assert.Throws<McpToolException>(() => ChatText.EnsureByteLimit(new string('a', 499) + U.S("<00E9>")));
        Assert.Contains("501", ex.Message);

        // 166 three-byte characters = 498 bytes; with "/p " that is 501.
        var japanese = U.Repeat("<3042>", 166);
        ChatText.EnsureByteLimit(japanese);
        Assert.Throws<McpToolException>(() => ChatText.EnsureByteLimit("/p " + japanese));
    }

    [Fact]
    public void RateLimiterAllowsOneMessagePerTwoSecondsAndTenPerMinute()
    {
        long now = 1_000_000;
        var limiter = new ChatRateLimiter(() => now);
        limiter.EnsureAllowed();
        limiter.Record();

        now += 1999;
        Assert.Contains("wait", Assert.Throws<McpToolException>(limiter.EnsureAllowed).Message);

        for (var i = 1; i < 10; i++)
        {
            now += 2000;
            limiter.EnsureAllowed();
            limiter.Record();
        }

        now += 2000;
        Assert.Contains("10 messages", Assert.Throws<McpToolException>(limiter.EnsureAllowed).Message);

        // The first message leaves the one-minute window.
        now = 1_000_000 + 60_000;
        limiter.EnsureAllowed();
    }
}

[Collection(ChatCommandsCollection.Name)]
public class ChatCommandsTests
{
    [Theory]
    [InlineData("/say hi")]
    [InlineData("/SAY hi")]
    [InlineData("   /s hi")]
    [InlineData("/<FF53><FF41><FF59> hi")] // full-width letters
    [InlineData("/say<3000>hi")] // ideographic space
    [InlineData("/s<200B>ay hi")] // zero-width space inside the command
    [InlineData("/p")]
    [InlineData("/party hello")]
    [InlineData("/a hi")]
    [InlineData("/fc hi")]
    [InlineData("/freecompany hi")]
    [InlineData("/l hi")]
    [InlineData("/linkshell1 hi")]
    [InlineData("/l8 hi")]
    [InlineData("/cwl hi")]
    [InlineData("/cwlinkshell1 hi")]
    [InlineData("/CWL8 hi")]
    [InlineData("/tell Warrior Light@Balmung hi")]
    [InlineData("/t Warrior Light hi")]
    [InlineData("/r hi")]
    [InlineData("/reply hi")]
    [InlineData("/y hi")]
    [InlineData("/yell hi")]
    [InlineData("/sh hi")]
    [InlineData("/shout hi")]
    [InlineData("/em waves wildly")]
    [InlineData("/emote waves")]
    [InlineData("/beginner hi")]
    [InlineData("/novice hi")]
    [InlineData("/pvpteam hi")]
    [InlineData("/random")]
    [InlineData("/dice 100")]
    [InlineData("<FF0F>say hi")] // full-width slash folds too (execute_command separately requires an ASCII '/')
    public void ChatCommandsAreDetected(string line) => Assert.Equal(CommandKind.Chat, ChatCommands.Classify(U.S(line)));

    [Theory]
    [InlineData("/e note to self")] // echo, not emote
    [InlineData("/echo note")]
    [InlineData("/gearset change 3")]
    [InlineData("/hudlayout 2")]
    [InlineData("/xlplugins")]
    [InlineData("/saybye")] // not a known command
    [InlineData("hello")]
    public void OtherCommandsAreNotChat(string line) => Assert.Equal(CommandKind.Other, ChatCommands.Classify(line));

    [Theory]
    [InlineData("/logout", CommandKind.Blocked)]
    [InlineData("/<FF2C><FF2F><FF27><FF2F><FF35><FF34>", CommandKind.Blocked)]
    [InlineData("/Shutdown", CommandKind.Blocked)]
    [InlineData("/xlkill", CommandKind.Blocked)]
    [InlineData("/ac Sprint", CommandKind.Automation)]
    [InlineData("/action Holy", CommandKind.Automation)]
    [InlineData("/automove on", CommandKind.Automation)]
    [InlineData("/lockon", CommandKind.Automation)]
    public void BlockedAndAutomationCommands(string line, CommandKind expected) => Assert.Equal(expected, ChatCommands.Classify(U.S(line)));

    [Fact]
    public void CommandTokenIsNormalized()
    {
        Assert.Equal("/say", ChatCommands.CommandToken(U.S("  /<FF33><FF21><FF39><3000>hi")));
        Assert.Equal("", ChatCommands.CommandToken("say hi"));
        Assert.Equal("", ChatCommands.CommandToken("/ hi"));
    }

    [Fact]
    public void LocalizedSpellingsJoinTheirEnglishCommand()
    {
        try
        {
            ChatCommands.AddAliases(
            [
                ["/say", "/s", "/sagen", "/sa"],
                ["/logout", "/logout", "/abmelden", ""],
                ["/gearset", "/gs", "/ausruestungsset", ""],
            ]);

            Assert.Equal(CommandKind.Chat, ChatCommands.Classify("/sagen hallo"));
            Assert.Equal(CommandKind.Chat, ChatCommands.Classify("/SA hallo"));
            Assert.Equal(CommandKind.Blocked, ChatCommands.Classify("/abmelden"));
            Assert.Equal(CommandKind.Other, ChatCommands.Classify("/ausruestungsset change 1"));
            Assert.Equal(CommandKind.Chat, ChatCommands.Classify("/party hi")); // English list kept
        }
        finally
        {
            ChatCommands.AddAliases([]);
        }

        Assert.Equal(CommandKind.Other, ChatCommands.Classify("/sagen hallo"));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChatCommandsCollection
{
    public const string Name = "ChatCommands static state";
}

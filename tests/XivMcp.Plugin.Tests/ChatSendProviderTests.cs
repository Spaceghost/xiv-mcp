using System.Collections.ObjectModel;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Chat;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>send_chat / execute_command validation paths that reject before anything reaches the game.</summary>
[Collection(ChatCommandsCollection.Name)]
public class ChatSendProviderTests
{
    private static ChatSendProvider Create(Configuration config, Dictionary<string, IReadOnlyCommandInfo>? pluginCommands = null)
    {
        var commands = FakeProxy.Create<ICommandManager>(new()
        {
            ["get_Commands"] = _ => new ReadOnlyDictionary<string, IReadOnlyCommandInfo>(pluginCommands ?? []),
            ["ProcessCommand"] = _ => throw new InvalidOperationException("must not dispatch in these tests"),
        });

        // IDataManager returns no sheets here: localized spellings are skipped (and logged), English stays.
        return new ChatSendProvider(commands, FakeProxy.Create<IChatGui>(), config, FakeProxy.Create<IDataManager>(), FakeProxy.Create<IPluginLog>());
    }

    private static string Rejects(Action call)
    {
        var ex = Assert.Throws<McpToolException>(call);
        return ex.Message;
    }

    [Theory]
    [InlineData("/logout", "blocked")]
    [InlineData("  /LOGOUT", "blocked")]
    [InlineData("/<FF4C><FF4F><FF47><FF4F><FF55><FF54>", "blocked")]
    [InlineData("/xlkill", "blocked")]
    [InlineData("/ac Sprint", "does not automate")]
    [InlineData("/follow <t>", "does not automate")]
    [InlineData("gearset change 1", "start with '/'")]
    [InlineData("<FF0F>gearset change 1", "start with '/'")]
    [InlineData("/", "start with '/'")]
    [InlineData("/gearset change 1<000A>/shout hi", "single line")]
    public void ExecuteCommandRejects(string command, string expected)
    {
        var provider = Create(new Configuration { AllowAction = true, AllowChat = true });
        Assert.Contains(expected, Rejects(() => provider.ExecuteCommand(U.S(command))));
    }

    [Theory]
    [InlineData("/p pull in 5")]
    [InlineData("/P pull")]
    [InlineData("/<FF50> pull")]
    [InlineData("/party<3000>pull")]
    [InlineData("/s<200B>ay hi")]
    [InlineData("/cwl3 hello")]
    [InlineData("/em dances")]
    [InlineData("/tell Warrior Light@Balmung hi")]
    public void ExecuteCommandChatNeedsTheChatTier(string command)
    {
        var provider = Create(new Configuration { AllowAction = true, AllowChat = false });
        Assert.Contains("Chat permission tier", Rejects(() => provider.ExecuteCommand(U.S(command))));
    }

    [Fact]
    public void ExecuteCommandLengthIsCheckedInBytes()
    {
        var provider = Create(new Configuration { AllowAction = true, AllowChat = true });
        Assert.Contains("500", Rejects(() => provider.ExecuteCommand("/echo " + U.Repeat("<3042>", 165))));
    }

    [Fact]
    public void SendChatValidation()
    {
        var provider = Create(new Configuration { AllowChat = true });
        Assert.Contains("cannot start with '/'", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Say, "/shout hi")));
        Assert.Contains("cannot start with '/'", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Say, U.S("<200B>/shout hi"))));
        Assert.Contains("cannot start with '/'", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Say, U.S("<FF0F>shout hi"))));
        Assert.Contains("single line", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Party, U.S("hi<000A>/logout"))));
        Assert.Contains("requires tellTarget", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Tell, "hi")));
        Assert.Contains("only valid", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Say, "hi", "Warrior Light@Balmung")));
        Assert.Contains("not a valid character name", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Tell, "hi", "Warrior Light@Balmung hi /shout")));
        Assert.Contains("not a valid character name", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Tell, "hi", "Warrior")));
        Assert.Contains("500", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Say, new string('x', 496))));
        Assert.Contains("empty", Rejects(() => provider.SendChat(ChatSendProvider.ChatChannel.Say, U.S("<0002><0003>"))));
    }
}

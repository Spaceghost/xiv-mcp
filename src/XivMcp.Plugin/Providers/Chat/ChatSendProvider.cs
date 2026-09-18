using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Chat;

/// <summary>send_chat, print_echo and execute_command.</summary>
[McpProvider("chat")]
public sealed partial class ChatSendProvider
{
    private const int MaxEchoLength = 1000;

    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly Configuration configuration;
    private readonly ChatRateLimiter rateLimiter = new();

    public ChatSendProvider(ICommandManager commandManager, IChatGui chatGui, Configuration configuration, IDataManager dataManager, IPluginLog log)
    {
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.configuration = configuration;
        LoadLocalizedCommandSpellings(dataManager, log);
    }

    /// <summary>
    /// The game accepts each text command under its English and localized spellings (TextCommand: Command,
    /// ShortCommand, Alias, ShortAlias). Teach the classifier every spelling of the chat, blocked and automation
    /// commands in all four client languages. Lumina only; no game memory.
    /// </summary>
    private static void LoadLocalizedCommandSpellings(IDataManager dataManager, IPluginLog log)
    {
        try
        {
            var rows = new Dictionary<uint, List<string>>();
            foreach (var language in Enum.GetValues<ClientLanguage>())
            {
                ExcelSheet<TextCommand> sheet;
                try
                {
                    sheet = dataManager.GetExcelSheet<TextCommand>(language);
                }
                catch (Exception)
                {
                    continue; // language data not installed
                }

                foreach (var row in sheet)
                {
                    if (!rows.TryGetValue(row.RowId, out var spellings))
                        rows[row.RowId] = spellings = [];
                    spellings.Add(row.Command.ExtractText());
                    spellings.Add(row.ShortCommand.ExtractText());
                    spellings.Add(row.Alias.ExtractText());
                    spellings.Add(row.ShortAlias.ExtractText());
                }
            }

            ChatCommands.AddAliases(rows.Values);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "xiv-mcp: localized text command spellings unavailable; using the English command list only");
        }
    }

    /// <summary>Chat channels send_chat can post to.</summary>
    public enum ChatChannel
    {
        Say,
        Party,
        Alliance,
        FreeCompany,
        Linkshell1,
        Linkshell2,
        Linkshell3,
        Linkshell4,
        Linkshell5,
        Linkshell6,
        Linkshell7,
        Linkshell8,
        CrossLinkshell1,
        CrossLinkshell2,
        CrossLinkshell3,
        CrossLinkshell4,
        CrossLinkshell5,
        CrossLinkshell6,
        CrossLinkshell7,
        CrossLinkshell8,
        Yell,
        Shout,
        Novice,
        PvpTeam,
        Tell,
    }

    [McpTool("send_chat",
        Title = "Send chat message",
        Description =
            "Sends one line of text that OTHER PLAYERS WILL SEE, on the chosen channel, exactly as if the user typed it into the chat box. " +
            "Only use when the user explicitly asked you to say something, and send exactly the words they approved. " +
            "channel: say (nearby), yell (wider area), shout (whole zone), party, alliance, freeCompany, linkshell1-8, crossLinkshell1-8, novice (Novice Network), pvpTeam, or tell (private message; requires tellTarget \"Firstname Lastname@World\", or without @World for the user's own world). " +
            "The message must be a single line, must not start with '/', and the full line including the channel command must fit in 500 UTF-8 bytes; control characters are removed. " +
            "Rate limited to 1 message per 2 seconds and 10 per minute. Returns the exact line submitted. The game does not confirm delivery: if the channel is unavailable (not in a party/FC, linkshell slot empty, recipient offline) an error line appears in chat — check with read_chat channels [\"system\"].",
        Permission = ToolPermission.Chat, OpenWorld = true, Idempotent = false)]
    public SendChatResult SendChat(
        [McpParam("Channel to post on.")] ChatChannel channel,
        [McpParam("The message text (single line, no leading '/').")] string message,
        [McpParam("Only for channel=tell: recipient as \"Firstname Lastname@World\" (or \"Firstname Lastname\" for the same world).")] string? tellTarget = null)
    {
        var text = ChatText.CleanSingleLine(message, "message");
        if (text.StartsWith('/') || ChatCommands.Normalize(text).StartsWith('/'))
            throw new McpToolException("The message cannot start with '/'. send_chat adds the channel command itself; use execute_command for slash commands.");

        string prefix;
        if (channel == ChatChannel.Tell)
        {
            if (string.IsNullOrWhiteSpace(tellTarget))
                throw new McpToolException("channel=tell requires tellTarget, e.g. \"Firstname Lastname@World\".");
            var target = tellTarget.Trim();
            if (!TellTargetRegex().IsMatch(target))
                throw new McpToolException($"tellTarget '{target}' is not a valid character name. Use \"Firstname Lastname@World\" (letters, apostrophes and hyphens; world name letters only).");
            prefix = $"/tell {target}";
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(tellTarget))
                throw new McpToolException("tellTarget is only valid with channel=tell.");
            prefix = ChannelPrefix(channel);
        }

        var line = $"{prefix} {text}";
        ChatText.EnsureByteLimit(line);
        rateLimiter.EnsureAllowed();
        ChatInput.Submit(line);
        rateLimiter.Record();

        return new SendChatResult(ChannelName(channel), line, Encoding.UTF8.GetByteCount(line));
    }

    [McpTool("print_echo",
        Title = "Print to own chat log",
        Description =
            "Prints a line into the user's OWN chat log only (tagged [MCP]); nobody else can see it and nothing is sent to the server. " +
            "Use it to leave the user a visible note, summary or reminder inside the game. Line breaks are turned into spaces; at most 1000 characters. Returns the printed text.",
        Permission = ToolPermission.Ui, Idempotent = false)]
    public EchoResult PrintEcho([McpParam("Text to print.")] string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new McpToolException("message is empty.");

        var sb = new StringBuilder(message.Length);
        foreach (var ch in message)
        {
            if (ch is '\r' or '\n' or '\t' or '\u2028' or '\u2029')
                sb.Append(' ');
            else if (!char.IsControl(ch))
                sb.Append(ch);
        }

        var text = sb.ToString().Trim();
        if (text.Length == 0)
            throw new McpToolException("message is empty after removing control characters.");
        var truncated = text.Length > MaxEchoLength;
        if (truncated)
            text = text[..MaxEchoLength];

        chatGui.Print(text, "MCP");
        return new EchoResult(text, truncated ? true : null);
    }

    [McpTool("execute_command",
        Title = "Execute slash command",
        Description =
            "Runs one slash command as if the user typed it into the chat box, e.g. \"/gearset change 3\", \"/hudlayout 2\", \"/xlplugins\" or another installed plugin's command. " +
            "Registered Dalamud plugin commands are dispatched through Dalamud (handledBy=\"plugin\"); everything else goes to the game's command parser (handledBy=\"game\"). " +
            "The game does not return a result: unknown commands or failures show up as error lines in chat, so follow up with read_chat channels [\"system\"] when it matters. " +
            "Commands that post text others can see (/say /s /party /p /alliance /fc /linkshell1-8 /l1-8 /cwlinkshell1-8 /cwl1-8 /yell /shout /tell /reply /emote /em /beginner /pvpteam /random /dice) additionally require the Chat permission tier and share send_chat's rate limit — prefer send_chat for messages. Detection ignores case, full-width characters and invisible characters and knows the localized spellings of those commands; commands registered by other plugins are not classified. " +
            "Blocked outright: /logout /shutdown /quit /exit, Dalamud kill/restart/branch/language commands, xiv-mcp's own commands, and action/movement commands (/ac /action /blueaction /pvpaction /generalaction /petaction /automove /follow /lockon /facetarget) — combat and movement automation is out of scope. Must be a single line starting with '/', at most 500 UTF-8 bytes.",
        Permission = ToolPermission.Action, Destructive = true, Idempotent = false)]
    public ExecuteCommandResult ExecuteCommand([McpParam("The full command line, starting with '/'.")] string command)
    {
        var line = ChatText.CleanSingleLine(command, "command");
        var token = ChatCommands.CommandToken(line);
        if (!line.StartsWith('/') || token.Length < 2)
            throw new McpToolException("command must start with '/' followed by the command name, e.g. \"/gearset change 1\". To send plain text use send_chat.");
        ChatText.EnsureByteLimit(line);

        switch (ChatCommands.Classify(line))
        {
            case CommandKind.Blocked:
                throw new McpToolException($"{token} is blocked for MCP clients (it would log out, close or restart the game or change Dalamud itself). Ask the user to run it manually.");
            case CommandKind.Automation:
                throw new McpToolException($"{token} triggers combat actions or movement; xiv-mcp does not automate those. Ask the user to do it themselves.");
        }

        var pluginCommand = FindPluginCommand(token);
        if (pluginCommand is { } info && IsOwnCommand(info))
            throw new McpToolException($"{token} belongs to xiv-mcp itself and cannot be invoked by an MCP client (it could change the server's own permissions). Ask the user to use it directly.");

        var isChat = ChatCommands.Classify(line) == CommandKind.Chat;
        if (isChat)
        {
            if (!configuration.IsPermitted(ToolPermission.Chat))
                throw new McpToolException($"{token} posts text other players can see and needs the Chat permission tier, which is disabled. Ask the user to enable it in the xiv-mcp settings, or don't send it.");
            rateLimiter.EnsureAllowed();
        }

        string handledBy;
        if (pluginCommand is not null && commandManager.ProcessCommand(line))
        {
            handledBy = "plugin";
        }
        else
        {
            ChatInput.Submit(line);
            handledBy = "game";
        }

        if (isChat)
            rateLimiter.Record();

        return new ExecuteCommandResult(
            line,
            handledBy,
            isChat ? true : null,
            handledBy == "game"
                ? "Submitted to the game's command parser; the game reports failures only as chat error lines (read_chat channels [\"system\"])."
                : null);
    }

    private IReadOnlyCommandInfo? FindPluginCommand(string token)
    {
        var commands = commandManager.Commands;
        if (commands.TryGetValue(token, out var exact))
            return exact;
        foreach (var (name, info) in commands)
        {
            if (string.Equals(ChatCommands.Normalize(name), token, StringComparison.Ordinal))
                return info;
        }

        return null;
    }

    private static bool IsOwnCommand(IReadOnlyCommandInfo info)
    {
        try
        {
            return info.Handler.Method.Module.Assembly == typeof(ChatSendProvider).Assembly;
        }
        catch
        {
            return false;
        }
    }

    private static string ChannelPrefix(ChatChannel channel) => channel switch
    {
        ChatChannel.Say => "/say",
        ChatChannel.Party => "/party",
        ChatChannel.Alliance => "/alliance",
        ChatChannel.FreeCompany => "/freecompany",
        >= ChatChannel.Linkshell1 and <= ChatChannel.Linkshell8 => $"/linkshell{channel - ChatChannel.Linkshell1 + 1}",
        >= ChatChannel.CrossLinkshell1 and <= ChatChannel.CrossLinkshell8 => $"/cwlinkshell{channel - ChatChannel.CrossLinkshell1 + 1}",
        ChatChannel.Yell => "/yell",
        ChatChannel.Shout => "/shout",
        ChatChannel.Novice => "/beginner",
        ChatChannel.PvpTeam => "/pvpteam",
        _ => throw new McpToolException($"Unsupported channel '{channel}'."),
    };

    private static string ChannelName(ChatChannel channel)
    {
        var name = channel.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z'\-]{0,14} [A-Za-z][A-Za-z'\-]{0,14}(@[A-Za-z]{2,20})?$", RegexOptions.CultureInvariant)]
    private static partial Regex TellTargetRegex();

    public sealed record SendChatResult(string Channel, string SentText, int Bytes);

    public sealed record EchoResult(string Printed, bool? Truncated);

    public sealed record ExecuteCommandResult(string Command, string HandledBy, bool? UsedChatTier, string? Note);
}

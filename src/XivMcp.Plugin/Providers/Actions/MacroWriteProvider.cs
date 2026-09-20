using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Chat;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>write_macro and clear_macro: one User Macro slot per approved call.</summary>
[McpProvider("actions")]
public sealed class MacroWriteProvider
{
    private readonly ICommandManager commandManager;
    private readonly IDataManager dataManager;
    private readonly ICondition condition;
    private readonly Configuration configuration;

    /// <summary>Whether an icon id is one of the game's macro icons; replaced in host tests, which have no game data.</summary>
    internal Func<uint, bool>? IconExists { get; set; }

    public MacroWriteProvider(ICommandManager commandManager, IDataManager dataManager, ICondition condition, Configuration configuration)
    {
        this.commandManager = commandManager;
        this.dataManager = dataManager;
        this.condition = condition;
        this.configuration = configuration;
    }

    [McpTool("write_macro",
        Sources = ["client:RaptureMacroModule", "client:RaptureHotbarModule", "lumina:MacroIcon"],
        ApprovalSummary = "Overwrite macro slot {index} of your {set} macros with the title \"{title}\", icon {iconId}, and exactly these lines: {lines}",
        Title = "Write a user macro",
        Description =
            "Writes one slot of the User Macros window, replacing whatever is in it: set individual (this character) or shared (all characters), index 0-99 as in list_macros, title (at most 20 characters), optional iconId (an icon from the macro icon picker; omitted keeps the slot's icon, or the default icon for an empty slot) and 1-15 lines of at most 180 UTF-8 bytes each. " +
            "The player runs the macro; this tool never does. Every line follows execute_command's rules: lines that log out, close the game or change Dalamud, action and movement commands (/ac /action /automove /follow ...) and xiv-mcp's own commands are refused (error code refused), and lines other players would see (/say /party ..., or plain text without a command) need the Chat permission tier. " +
            "The whole lines array must fit the approval prompt (500 characters as JSON), so very long macros are rejected rather than approved unseen. " +
            "Returns the slot as written and previous (title, iconId, lines, and restorable: whether write_macro would accept those lines again) so the old content can be put back. Hotbar slots that point at the macro are refreshed; the macro file is saved by the game as after an edit in the window.",
        Permission = ToolPermission.Action, Destructive = true, Idempotent = true)]
    public WriteMacroResult WriteMacro(
        [McpParam("Which macro page to write: individual or shared.")] MacroProvider.MacroSet set,
        [McpParam("Slot number 0-99 as shown in list_macros.", Minimum = 0, Maximum = 99)] int index,
        [McpParam("Macro name, at most 20 characters.")] string title,
        [McpParam("Macro text, 1-15 lines, each a single line of at most 180 UTF-8 bytes.")] string[] lines,
        [McpParam("Icon id from the macro icon picker (MacroIcon sheet). Omit to keep the slot's icon.")] uint? iconId = null)
    {
        MacroText.EnsureSlot(index);
        var cleanTitle = MacroText.CleanTitle(title);
        var chatPermitted = configuration.IsPermitted(ToolPermission.Chat);
        var cleanLines = MacroText.CleanLines(lines, chatPermitted, IsOwnCommand);
        if (iconId is { } icon && !(IconExists ?? MacroIconExists)(icon))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"iconId {icon} is not one of the game's macro icons (MacroIcon sheet). Omit iconId to keep the current icon.");

        PlayerGuards.EnsureNotBusy(condition, "edit macros");
        var previous = MacroMemory.Replace(set, index, cleanTitle, cleanLines, iconId);
        var written = MacroMemory.Read(set, index);
        return new WriteMacroResult(SetName(set), index, written, Previous(previous, chatPermitted));
    }

    [McpTool("clear_macro",
        Sources = ["client:RaptureMacroModule", "client:RaptureHotbarModule"],
        ApprovalSummary = "Delete the contents of macro slot {index} of your {set} macros (title, icon and all lines).",
        Title = "Clear a user macro",
        Description =
            "Empties one User Macros slot (set individual or shared, index 0-99): title, icon and lines are removed, as with Delete in the User Macros window. " +
            "Returns previous (title, iconId, lines, restorable) so the content can be written back with write_macro; wasEmpty is true when there was nothing to clear. Hotbar slots that pointed at the macro are refreshed.",
        Permission = ToolPermission.Action, Destructive = true, Idempotent = true)]
    public ClearMacroResult ClearMacro(
        [McpParam("Which macro page: individual or shared.")] MacroProvider.MacroSet set,
        [McpParam("Slot number 0-99 as shown in list_macros.", Minimum = 0, Maximum = 99)] int index)
    {
        MacroText.EnsureSlot(index);
        PlayerGuards.EnsureNotBusy(condition, "edit macros");
        var previous = MacroMemory.Clear(set, index);
        var wasEmpty = previous.Title.Length == 0 && previous.Lines.Count == 0;
        return new ClearMacroResult(SetName(set), index, wasEmpty, wasEmpty ? null : Previous(previous, configuration.IsPermitted(ToolPermission.Chat)));
    }

    private PreviousMacro Previous(MacroContent content, bool chatPermitted) =>
        new(content.Title, content.IconId, content.Lines, MacroText.CanBeWritten(content.Lines, chatPermitted, IsOwnCommand));

    private static string SetName(MacroProvider.MacroSet set) => set == MacroProvider.MacroSet.Shared ? "shared" : "individual";

    private bool MacroIconExists(uint iconId)
    {
        var sheet = dataManager.GetExcelSheet<MacroIcon>();
        return sheet is not null && sheet.Any(row => row.Icon > 0 && (uint)row.Icon == iconId);
    }

    private bool IsOwnCommand(string token)
    {
        foreach (var (name, info) in commandManager.Commands)
        {
            if (string.Equals(ChatCommands.Normalize(name), token, StringComparison.Ordinal) && IsOwn(info))
                return true;
        }

        return false;
    }

    private static bool IsOwn(IReadOnlyCommandInfo info)
    {
        try
        {
            return info.Handler.Method.Module.Assembly == typeof(MacroWriteProvider).Assembly;
        }
        catch
        {
            return false;
        }
    }

    public sealed record MacroContent(string Title, uint IconId, IReadOnlyList<string> Lines);

    public sealed record PreviousMacro(string Title, uint IconId, IReadOnlyList<string> Lines, bool Restorable);

    public sealed record WriteMacroResult(string Set, int Index, MacroContent Written, PreviousMacro Previous);

    public sealed record ClearMacroResult(string Set, int Index, bool WasEmpty, PreviousMacro? Previous);
}

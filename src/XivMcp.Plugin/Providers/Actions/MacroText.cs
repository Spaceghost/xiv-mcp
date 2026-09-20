using System.Globalization;
using System.Text;
using System.Text.Json;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Chat;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>
/// Pure rules for text written into a User Macro slot (no game or Dalamud types; unit tested on the host). A macro is
/// typed text the player later runs with one key press, so every line is held to the same rules as execute_command:
/// nothing the command classifier calls Blocked or Automation, nothing that belongs to xiv-mcp itself, and lines other
/// players would see only when the Chat tier is on.
/// </summary>
public static class MacroText
{
    /// <summary>Slots per macro set (individual / shared).</summary>
    public const int SlotCount = 100;

    /// <summary>Lines per macro (RaptureMacroModule.Macro holds 15 Utf8String lines).</summary>
    public const int MaxLines = 15;

    /// <summary>The User Macros window accepts at most 180 characters per line; held here as UTF-8 bytes, the stricter reading.</summary>
    public const int MaxLineBytes = 180;

    /// <summary>The User Macros window accepts at most 20 characters for the macro name.</summary>
    public const int MaxTitleLength = 20;

    /// <summary>The game's default macro icon.</summary>
    public const uint DefaultIconId = 66001;

    /// <summary>
    /// The server shows each argument in the approval sentence up to this many characters. A macro whose lines do not fit
    /// would be approved without the player having seen all of it, so it is rejected instead.
    /// </summary>
    public const int MaxApprovalArgumentLength = 500;

    public static void EnsureSlot(int index)
    {
        if (index is < 0 or >= SlotCount)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"index {index} is out of range; macro slots are 0-{SlotCount - 1} (the number shown in the User Macros window).");
    }

    public static string CleanTitle(string? title)
    {
        var cleaned = ChatText.CleanSingleLine(title, "title");
        var length = new StringInfo(cleaned).LengthInTextElements;
        if (length > MaxTitleLength)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"title is {length} characters; the game allows at most {MaxTitleLength}.");
        return cleaned;
    }

    /// <summary>
    /// Validates and cleans macro lines. Blank lines inside the macro are kept, trailing blank lines are dropped.
    /// </summary>
    /// <param name="lines">The lines as given by the client.</param>
    /// <param name="chatPermitted">Whether the Chat permission tier is on.</param>
    /// <param name="isOwnCommand">True for a normalized command token that belongs to this plugin.</param>
    public static IReadOnlyList<string> CleanLines(IReadOnlyList<string?>? lines, bool chatPermitted, Func<string, bool>? isOwnCommand = null)
    {
        if (lines is null || lines.Count == 0)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "lines is empty. Pass 1-15 macro lines, or use clear_macro to empty a slot.");
        if (lines.Count > MaxLines)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"lines has {lines.Count} entries; a macro holds at most {MaxLines} lines.");

        var approvalLength = JsonSerializer.Serialize(lines, McpJson.Options).Length;
        if (approvalLength > MaxApprovalArgumentLength)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"lines is {approvalLength} characters as shown in the approval prompt, which displays at most {MaxApprovalArgumentLength}; the player could not see the whole macro before approving it. Shorten the macro or split it over two slots.");

        var cleaned = new List<string>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                cleaned.Add("");
                continue;
            }

            var what = $"lines[{i}]";
            var line = ChatText.CleanSingleLine(raw, what);
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (bytes > MaxLineBytes)
                throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"{what} is {bytes} UTF-8 bytes; a macro line holds at most {MaxLineBytes}.");
            EnsureAllowed(line, what, chatPermitted, isOwnCommand);
            cleaned.Add(line);
        }

        while (cleaned.Count > 0 && cleaned[^1].Length == 0)
            cleaned.RemoveAt(cleaned.Count - 1);
        if (cleaned.Count == 0)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "lines has no text. Use clear_macro to empty a slot.");
        return cleaned;
    }

    /// <summary>Whether <see cref="CleanLines"/> would accept these (already stored) lines, i.e. whether write_macro can put them back.</summary>
    public static bool CanBeWritten(IReadOnlyList<string> lines, bool chatPermitted, Func<string, bool>? isOwnCommand = null)
    {
        if (lines.Count == 0)
            return true; // clear_macro restores an empty slot
        try
        {
            CleanLines(lines, chatPermitted, isOwnCommand);
            return true;
        }
        catch (McpToolException)
        {
            return false;
        }
    }

    private static void EnsureAllowed(string line, string what, bool chatPermitted, Func<string, bool>? isOwnCommand)
    {
        var token = ChatCommands.CommandToken(line);
        if (token.Length == 0)
        {
            // Text without a command is said on the player's current chat channel when the macro runs.
            if (!chatPermitted)
                throw McpToolException.WithCode(McpErrorCodes.Refused, $"{what} is plain text, which a macro says on the current chat channel where other players see it. That needs the Chat permission tier, which is disabled.");
            return;
        }

        switch (ChatCommands.Classify(line))
        {
            case CommandKind.Blocked:
                throw McpToolException.WithCode(McpErrorCodes.Refused, $"{what}: {token} is blocked for MCP clients (it would log out, close or restart the game or change Dalamud itself), in a macro as much as in execute_command.");
            case CommandKind.Automation:
                throw McpToolException.WithCode(McpErrorCodes.Refused, $"{what}: {token} triggers combat actions or movement; xiv-mcp does not write those into macros any more than it runs them. The player can add the line in the User Macros window.");
            case CommandKind.Chat when !chatPermitted:
                throw McpToolException.WithCode(McpErrorCodes.Refused, $"{what}: {token} posts text other players can see and needs the Chat permission tier, which is disabled.");
        }

        if (isOwnCommand is not null && isOwnCommand(token))
            throw McpToolException.WithCode(McpErrorCodes.Refused, $"{what}: {token} belongs to xiv-mcp itself and cannot be written into a macro by an MCP client.");
    }
}

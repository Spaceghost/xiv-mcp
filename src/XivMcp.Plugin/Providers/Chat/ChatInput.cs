using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Chat;

/// <summary>Validation and submission of text through the game's chat box (framework thread only).</summary>
internal static class ChatInput
{
    // Upper|Lower|Numbers|SpecialCharacters|CharacterList|OtherCharacters|Payloads|Unknown9 — what the
    // chat input itself allows. Anything the game would strip makes the text differ after sanitising.
    private const AllowedEntities ChatAllowed = (AllowedEntities)0x27F;

    /// <summary>
    /// Submits <paramref name="line"/> exactly as if typed into the chat box and confirmed with Enter.
    /// Must run on the framework thread.
    /// </summary>
    public static unsafe void Submit(string line)
    {
        ChatText.EnsureByteLimit(line);

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            throw new McpToolException("The game UI is not ready; try again once the character is fully loaded.");

        var str = Utf8String.FromString(line);
        if (str == null)
            throw new McpToolException("Could not allocate a game string for the chat input.");

        try
        {
            str->SanitizeString(ChatAllowed, null);
            if (!string.Equals(str->ToString(), line, StringComparison.Ordinal))
                throw new McpToolException("The text contains characters the game's chat box does not accept (the game would strip or alter them). Remove special symbols and try again.");

            uiModule->ProcessChatBoxEntry(str, 0, false);
        }
        finally
        {
            str->Dtor(true);
        }
    }
}

using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Chat;

/// <summary>Validation and submission of text through the game's chat box (framework thread only).</summary>
internal static class ChatInput
{
    /// <summary>The chat input box accepts at most 500 UTF-8 bytes.</summary>
    public const int MaxBytes = 500;

    // Upper|Lower|Numbers|SpecialCharacters|CharacterList|OtherCharacters|Payloads|Unknown9 — what the
    // chat input itself allows. Anything the game would strip makes the text differ after sanitising.
    private const AllowedEntities ChatAllowed = (AllowedEntities)0x27F;

    /// <summary>
    /// Rejects multi-line input, removes other control characters (tabs become spaces) and trims.
    /// </summary>
    public static string CleanSingleLine(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpToolException($"{what} is empty.");
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new McpToolException($"{what} must be a single line (no line breaks). Send separate calls for separate lines.");

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\t')
                sb.Append(' ');
            else if (!char.IsControl(ch) && ch != '\u2028' && ch != '\u2029')
                sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0)
            throw new McpToolException($"{what} is empty after removing control characters.");
        return cleaned;
    }

    public static void EnsureByteLimit(string line)
    {
        var bytes = Encoding.UTF8.GetByteCount(line);
        if (bytes > MaxBytes)
            throw new McpToolException($"Text is {bytes} UTF-8 bytes including the command prefix; the game's chat box accepts at most {MaxBytes}. Shorten it or split it into several messages.");
    }

    /// <summary>
    /// Submits <paramref name="line"/> exactly as if typed into the chat box and confirmed with Enter.
    /// Must run on the framework thread.
    /// </summary>
    public static unsafe void Submit(string line)
    {
        EnsureByteLimit(line);

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

/// <summary>Client-side flood guard for anything other players can see.</summary>
internal sealed class ChatRateLimiter
{
    public const int MinIntervalMs = 2000;

    public const int MaxPerMinute = 10;

    private readonly Queue<long> sent = new();
    private readonly object gate = new();

    /// <summary>Throws an actionable <see cref="McpToolException"/> when sending now would exceed the limit.</summary>
    public void EnsureAllowed()
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            while (sent.Count > 0 && now - sent.Peek() >= 60_000)
                sent.Dequeue();

            if (sent.Count > 0)
            {
                var last = sent.Last();
                var sinceLast = now - last;
                if (sinceLast < MinIntervalMs)
                    throw new McpToolException($"Chat rate limit: wait {(MinIntervalMs - sinceLast) / 1000.0:0.0} s before sending again (max 1 message every 2 s and {MaxPerMinute} per minute).");
            }

            if (sent.Count >= MaxPerMinute)
            {
                var wait = 60_000 - (now - sent.Peek());
                throw new McpToolException($"Chat rate limit: {MaxPerMinute} messages already sent in the last minute; wait {wait / 1000.0:0.0} s.");
            }
        }
    }

    public void Record()
    {
        lock (gate)
            sent.Enqueue(Environment.TickCount64);
    }
}

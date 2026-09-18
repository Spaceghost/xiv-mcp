using System.Globalization;
using System.Text;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Chat;

/// <summary>
/// Pure text rules for anything typed into the game's chat box (no game or Dalamud types, so it is unit tested on the
/// host). The native submission lives in <see cref="ChatInput"/>.
/// </summary>
public static class ChatText
{
    /// <summary>The chat input box accepts at most 500 UTF-8 bytes.</summary>
    public const int MaxBytes = 500;

    /// <summary>
    /// Rejects multi-line input; removes control characters (including the 0x02 SeString payload marker that
    /// auto-translate and link payloads start with), invisible format characters (zero-width, bidi overrides, soft
    /// hyphen) and unpaired surrogates; turns tabs into spaces; trims.
    /// </summary>
    public static string CleanSingleLine(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpToolException($"{what} is empty.");
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0 || text.Contains('\u2028') || text.Contains('\u2029') || text.Contains('\u0085'))
            throw new McpToolException($"{what} must be a single line (no line breaks). Send separate calls for separate lines.");

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(ch).Append(text[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(ch))
                continue;
            if (ch == '\t')
            {
                sb.Append(' ');
                continue;
            }

            if (char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                continue;
            sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0)
            throw new McpToolException($"{what} is empty after removing control characters.");
        return cleaned;
    }

    public static int ByteCount(string line) => Encoding.UTF8.GetByteCount(line);

    public static void EnsureByteLimit(string line)
    {
        var bytes = ByteCount(line);
        if (bytes > MaxBytes)
            throw new McpToolException($"Text is {bytes} UTF-8 bytes including the command prefix; the game's chat box accepts at most {MaxBytes}. Shorten it or split it into several messages.");
    }
}

/// <summary>Client-side flood guard for anything other players can see. Thread-safe.</summary>
public sealed class ChatRateLimiter
{
    public const int MinIntervalMs = 2000;

    public const int MaxPerMinute = 10;

    private readonly Queue<long> sent = new();
    private readonly object gate = new();
    private readonly Func<long> clock;

    /// <param name="clock">Milliseconds from a monotonic clock; defaults to <see cref="Environment.TickCount64"/>.</param>
    public ChatRateLimiter(Func<long>? clock = null) => this.clock = clock ?? (static () => Environment.TickCount64);

    /// <summary>Throws an actionable <see cref="McpToolException"/> when sending now would exceed the limit.</summary>
    public void EnsureAllowed()
    {
        lock (gate)
        {
            var now = clock();
            while (sent.Count > 0 && now - sent.Peek() >= 60_000)
                sent.Dequeue();

            if (sent.Count > 0)
            {
                var sinceLast = now - sent.Last();
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
            sent.Enqueue(clock());
    }
}

/// <summary>How execute_command treats a slash command.</summary>
public enum CommandKind
{
    /// <summary>Anything else: runs with the Action tier.</summary>
    Other,

    /// <summary>Posts text other players can read: additionally needs the Chat tier and the chat rate limit.</summary>
    Chat,

    /// <summary>Logs out, closes or restarts the game, or changes Dalamud itself: never allowed.</summary>
    Blocked,

    /// <summary>Combat actions or movement: out of scope, never allowed.</summary>
    Automation,
}

/// <summary>
/// Classifies slash commands for execute_command and the confirmation prompt. Pure; thread-safe. The command token is
/// compared after Unicode compatibility normalization (full-width letters and slashes fold to ASCII), removal of
/// invisible format characters and lower-casing, and is split at any Unicode whitespace (including the ideographic
/// space), so "/SAY", "/ｓａｙ", "/s&#x200B;ay" and "/say　hi" all classify as chat.
/// </summary>
public static class ChatCommands
{
    /// <summary>English command spellings (Command and ShortCommand columns of the TextCommand sheet) that post text others see.</summary>
    public static readonly IReadOnlyList<string> EnglishChat = BuildEnglishChat();

    public static readonly IReadOnlyList<string> EnglishBlocked =
    [
        "/logout", "/shutdown", "/quit", "/exit",
        "/xlkill", "/xlrestart", "/xlbranch", "/xllanguage", "/xltogglemultimonitor", "/xlbgmset",
    ];

    public static readonly IReadOnlyList<string> EnglishAutomation =
    [
        "/ac", "/action", "/blueaction", "/blac", "/pvpaction", "/pvpac", "/generalaction", "/gaction",
        "/petaction", "/pac", "/buddyaction", "/bac", "/craftaction", "/mountaction",
        "/automove", "/follow", "/lockon", "/facetarget", "/facecamera",
    ];

    private static volatile Snapshot current = new(ToSet(EnglishChat), ToSet(EnglishBlocked), ToSet(EnglishAutomation));

    /// <summary>
    /// Adds localized spellings (e.g. from the TextCommand sheet in every client language) of commands already
    /// classified by their English name. <paramref name="rows"/> holds, per sheet row, every spelling of that one
    /// command; a row joins a class when any of its spellings is already in it.
    /// </summary>
    public static void AddAliases(IEnumerable<IReadOnlyCollection<string>> rows)
    {
        var baseline = new Snapshot(ToSet(EnglishChat), ToSet(EnglishBlocked), ToSet(EnglishAutomation));
        foreach (var row in rows)
        {
            var spellings = row.Select(Normalize).Where(s => s.Length > 1 && s[0] == '/').ToArray();
            foreach (var set in new[] { baseline.Chat, baseline.Blocked, baseline.Automation })
            {
                if (spellings.Any(set.Contains))
                    set.UnionWith(spellings);
            }
        }

        current = baseline;
    }

    /// <summary>Number of known chat spellings (for diagnostics).</summary>
    public static int ChatSpellingCount => current.Chat.Count;

    /// <summary>The normalized command token of a line ("" when it does not start with a slash after normalization).</summary>
    public static string CommandToken(string line)
    {
        var normalized = Normalize(line).TrimStart();
        var end = 0;
        while (end < normalized.Length && !char.IsWhiteSpace(normalized[end]))
            end++;
        var token = normalized[..end];
        return token.Length > 1 && token[0] == '/' ? token : "";
    }

    public static CommandKind Classify(string line)
    {
        var token = CommandToken(line);
        if (token.Length == 0)
            return CommandKind.Other;
        var snapshot = current;
        if (snapshot.Blocked.Contains(token))
            return CommandKind.Blocked;
        if (snapshot.Automation.Contains(token))
            return CommandKind.Automation;
        return snapshot.Chat.Contains(token) ? CommandKind.Chat : CommandKind.Other;
    }

    /// <summary>NFKC, format characters removed, lower-cased (invariant).</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        string folded;
        try
        {
            folded = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            folded = text; // invalid UTF-16; ChatText.CleanSingleLine removes unpaired surrogates before submission
        }

        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format)
                sb.Append(ch);
        }

        return sb.ToString().ToLowerInvariant();
    }

    private static HashSet<string> ToSet(IEnumerable<string> items) => new(items.Select(Normalize), StringComparer.Ordinal);

    private static List<string> BuildEnglishChat()
    {
        var list = new List<string>
        {
            "/s", "/say", "/p", "/party", "/a", "/alliance", "/fc", "/freecompany",
            "/l", "/linkshell", "/cwl", "/cwlinkshell",
            "/y", "/yell", "/sh", "/shout", "/t", "/tell", "/r", "/reply",
            "/em", "/emote", "/beginner", "/novice", "/n", "/nn", "/pvpteam", "/pt",
            "/random", "/dice",
        };
        for (var i = 1; i <= 8; i++)
        {
            list.Add($"/l{i}");
            list.Add($"/linkshell{i}");
            list.Add($"/cwl{i}");
            list.Add($"/cwlinkshell{i}");
        }

        return list;
    }

    private sealed record Snapshot(HashSet<string> Chat, HashSet<string> Blocked, HashSet<string> Automation);
}

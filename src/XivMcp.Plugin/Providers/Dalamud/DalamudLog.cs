using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>Serilog levels in the order Dalamud writes them ("{Level:u3}").</summary>
public enum DalamudLogLevel
{
    Verbose = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5,
}

/// <summary>One log event: the header line plus any continuation lines (exception text) that followed it.</summary>
public sealed record DalamudLogEntry(DateTimeOffset Time, DalamudLogLevel Level, string? Plugin, string Message, string? Continuation);

/// <summary>
/// Parses the format Dalamud's file sink writes (EntryPoint.InitLogging):
/// <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}</c>.
/// Plugin and module loggers prefix the message with <c>[InternalName] </c>.
/// </summary>
public static partial class DalamudLogParser
{
    /// <summary>Continuation text kept per entry; a stack trace longer than this is cut.</summary>
    public const int MaxContinuationChars = 4000;

    public static bool TryParseLevel(string? text, out DalamudLogLevel level)
    {
        level = DalamudLogLevel.Information;
        switch (text?.Trim().ToUpperInvariant())
        {
            case "VERBOSE" or "VRB" or "TRACE":
                level = DalamudLogLevel.Verbose;
                return true;
            case "DEBUG" or "DBG":
                level = DalamudLogLevel.Debug;
                return true;
            case "INFORMATION" or "INFO" or "INF":
                level = DalamudLogLevel.Information;
                return true;
            case "WARNING" or "WARN" or "WRN":
                level = DalamudLogLevel.Warning;
                return true;
            case "ERROR" or "ERR":
                level = DalamudLogLevel.Error;
                return true;
            case "FATAL" or "FTL":
                level = DalamudLogLevel.Fatal;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Parses a header line; false for continuation lines (stack traces, multi-line messages).</summary>
    public static bool TryParseHeader(string line, out DalamudLogEntry entry)
    {
        entry = null!;
        var m = Header().Match(line);
        if (!m.Success)
            return false;
        if (!DateTimeOffset.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return false;
        if (!TryParseLevel(m.Groups["lvl"].Value, out var level))
            return false;

        var message = m.Groups["msg"].Value;
        string? plugin = null;
        var tag = Tag().Match(message);
        if (tag.Success)
        {
            plugin = tag.Groups["tag"].Value;
            message = message[tag.Length..];
        }

        entry = new DalamudLogEntry(time, level, plugin, message, null);
        return true;
    }

    /// <summary>Groups lines into entries. Lines before the first header (the tail of an entry that started outside the window) are dropped.</summary>
    public static List<DalamudLogEntry> ParseEntries(IEnumerable<string> lines)
    {
        var entries = new List<DalamudLogEntry>();
        DalamudLogEntry? current = null;
        StringBuilder? continuation = null;

        void Flush()
        {
            if (current is null)
                return;
            entries.Add(continuation is { Length: > 0 } ? current with { Continuation = continuation.ToString() } : current);
        }

        foreach (var line in lines)
        {
            if (TryParseHeader(line, out var entry))
            {
                Flush();
                current = entry;
                continuation = null;
            }
            else if (current is not null && line.Length > 0)
            {
                continuation ??= new StringBuilder();
                if (continuation.Length >= MaxContinuationChars)
                    continue;
                if (continuation.Length > 0)
                    continuation.Append('\n');
                var room = MaxContinuationChars - continuation.Length;
                continuation.Append(line.Length > room ? line.AsSpan(0, room) : line);
            }
        }

        Flush();
        return entries;
    }

    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<lvl>VRB|DBG|INF|WRN|ERR|FTL)\] (?<msg>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Header();

    [GeneratedRegex(@"^\[(?<tag>[^\[\]\r\n]{1,80})\] ?", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();
}

/// <summary>What to keep from a parsed log. All text matching is case-insensitive.</summary>
public sealed record DalamudLogQuery(string? Plugin, DalamudLogLevel MinimumLevel, string? Contains, DateTimeOffset? Since, int Limit);

public sealed record DalamudLogSelection(IReadOnlyList<DalamudLogEntry> Entries, int Matched, bool Truncated);

public static class DalamudLogFilter
{
    /// <summary>Keeps the newest <see cref="DalamudLogQuery.Limit"/> matches, oldest first (newest last).</summary>
    public static DalamudLogSelection Select(IReadOnlyList<DalamudLogEntry> entries, DalamudLogQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);
        var matches = new List<DalamudLogEntry>();
        foreach (var e in entries)
        {
            if (e.Level < query.MinimumLevel)
                continue;
            if (query.Since is { } since && e.Time < since)
                continue;
            if (!string.IsNullOrEmpty(query.Plugin) && !string.Equals(e.Plugin, query.Plugin, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(query.Contains) &&
                !e.Message.Contains(query.Contains, StringComparison.OrdinalIgnoreCase) &&
                !(e.Continuation?.Contains(query.Contains, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            matches.Add(e);
        }

        var skip = Math.Max(0, matches.Count - limit);
        return new DalamudLogSelection(skip == 0 ? matches : matches.GetRange(skip, limit), matches.Count, skip > 0);
    }

    /// <summary>Error and fatal entries per plugin tag (untagged entries count as "Dalamud"), most first.</summary>
    public static IReadOnlyList<KeyValuePair<string, int>> ErrorCounts(IEnumerable<DalamudLogEntry> entries, DateTimeOffset since, int top)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.Level < DalamudLogLevel.Error || e.Time < since)
                continue;
            var key = e.Plugin ?? "Dalamud";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts.OrderByDescending(static p => p.Value).ThenBy(static p => p.Key, StringComparer.OrdinalIgnoreCase).Take(top).ToList();
    }
}

public sealed record DalamudLogTail(IReadOnlyList<string> Lines, long FileBytes, long ScannedBytes, bool StartedMidFile);

/// <summary>Reads the end of a log another process is writing. Never reads more than <see cref="MaxTailBytes"/>.</summary>
public static class DalamudLogTailReader
{
    public const int MaxTailBytes = 4 * 1024 * 1024;

    /// <summary>Lines longer than this are cut (the cut is marked) before anything else looks at them.</summary>
    public const int MaxLineChars = LogScrubber.MaxInput;

    /// <exception cref="IOException">The file is missing, locked exclusively or being replaced.</exception>
    public static DalamudLogTail ReadTail(string path, int maxBytes)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, MaxTailBytes);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var length = stream.Length;
        var start = Math.Max(0, length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);

        // The writer may truncate the file (Dalamud's log retention) between Length and Read: take what is there.
        var buffer = new byte[(int)(length - start)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
                break;
            read += n;
        }

        var offset = 0;
        if (start > 0)
        {
            // The window starts inside a line (perhaps inside a UTF-8 sequence): drop everything up to the first newline.
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            offset = newline < 0 ? read : newline + 1;
        }

        var text = Encoding.UTF8.GetString(buffer, offset, read - offset);
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            lines.Add(line.Length > MaxLineChars ? string.Concat(line.AsSpan(0, MaxLineChars), " ...[cut]") : line);
        }

        // A last line without a newline is still being written; it is returned as is (the next call completes it).
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return new DalamudLogTail(lines, length, read, start > 0);
    }
}

/// <summary>
/// Finds dalamud.log from paths the plugin interface hands out. XIVLauncher keeps the log in its roaming root
/// (<c>~/.xlcore</c>, <c>%AppData%\XIVLauncher</c>) next to <c>pluginConfigs/</c> and <c>dalamudAssets/</c>; nothing here
/// names a home directory.
/// </summary>
public static class DalamudLogLocator
{
    public const string FileName = "dalamud.log";

    /// <param name="pluginConfigDirectory"><c>&lt;root&gt;/pluginConfigs/&lt;InternalName&gt;</c></param>
    /// <param name="pluginConfigFile"><c>&lt;root&gt;/pluginConfigs/&lt;InternalName&gt;.json</c></param>
    /// <param name="assetDirectory"><c>&lt;root&gt;/dalamudAssets/&lt;version&gt;</c></param>
    public static IReadOnlyList<string> Candidates(string? pluginConfigDirectory, string? pluginConfigFile, string? assetDirectory)
    {
        var roots = new List<string?>
        {
            Up(pluginConfigDirectory, 2),
            Up(pluginConfigFile, 2),
            Up(assetDirectory, 2),
            Up(assetDirectory, 1),
        };

        var result = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var candidate = Path.Combine(root, FileName);
            if (!result.Contains(candidate, StringComparer.Ordinal))
                result.Add(candidate);
        }

        return result;
    }

    public static string? FindExisting(IEnumerable<string> candidates) => candidates.FirstOrDefault(File.Exists);

    private static string? Up(string? path, int levels)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            for (var i = 0; i < levels && current is not null; i++)
                current = Path.GetDirectoryName(current);
            return current;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

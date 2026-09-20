using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.DalamudInfo;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>dalamud.log handling: the line format, filtering, the bounded tail reader and where the file is looked for.</summary>
public sealed class DalamudLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xivmcp-log-" + Guid.NewGuid().ToString("N"));

    public DalamudLogTests() => Directory.CreateDirectory(Path.Combine(root, "pluginConfigs", "XivMcp"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string LogPath => Path.Combine(root, "dalamud.log");

    private static string Line(DateTimeOffset time, string level, string message) =>
        time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture) + " [" + level + "] " + message;

    [Fact]
    public void ParsesTheSerilogTemplateDalamudWrites()
    {
        Assert.True(DalamudLogParser.TryParseHeader("2026-09-19 12:34:56.789 +02:00 [ERR] [SomePlugin] Could not load: boom", out var entry));
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 12, 34, 56, 789, TimeSpan.FromHours(2)), entry.Time);
        Assert.Equal(DalamudLogLevel.Error, entry.Level);
        Assert.Equal("SomePlugin", entry.Plugin);
        Assert.Equal("Could not load: boom", entry.Message);

        Assert.True(DalamudLogParser.TryParseHeader("2026-01-02 03:04:05.006 -05:00 [INF] This is Dalamud", out var untagged));
        Assert.Null(untagged.Plugin);
        Assert.Equal("This is Dalamud", untagged.Message);
        Assert.Equal(TimeSpan.FromHours(-5), untagged.Time.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   at Some.Plugin.Method() in file.cs:line 12")]
    [InlineData("System.InvalidOperationException: boom")]
    [InlineData("2026-09-19 12:34:56.789 +02:00 [XXX] nope")]
    [InlineData("2026-09-19 12:34:56 +02:00 [INF] no milliseconds")]
    [InlineData("2026-13-45 99:99:99.999 +02:00 [INF] impossible date")]
    public void ContinuationAndMalformedLinesAreNotHeaders(string line) => Assert.False(DalamudLogParser.TryParseHeader(line, out _));

    [Theory]
    [InlineData("VRB", DalamudLogLevel.Verbose)]
    [InlineData("DBG", DalamudLogLevel.Debug)]
    [InlineData("INF", DalamudLogLevel.Information)]
    [InlineData("WRN", DalamudLogLevel.Warning)]
    [InlineData("ERR", DalamudLogLevel.Error)]
    [InlineData("FTL", DalamudLogLevel.Fatal)]
    [InlineData(" Warning ", DalamudLogLevel.Warning)]
    [InlineData("info", DalamudLogLevel.Information)]
    public void ParsesLevels(string text, DalamudLogLevel expected)
    {
        Assert.True(DalamudLogParser.TryParseLevel(text, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("loud")]
    [InlineData("3")]
    public void RejectsUnknownLevels(string? text) => Assert.False(DalamudLogParser.TryParseLevel(text, out _));

    [Fact]
    public void GroupsExceptionLinesUnderTheirEntryAndCapsThem()
    {
        var lines = new List<string>
        {
            "   at Orphan.Frame()",
            "2026-09-19 10:00:00.000 +00:00 [ERR] [A] failed",
            "System.Exception: boom",
            "   at A.Run()",
            "2026-09-19 10:00:01.000 +00:00 [INF] [B] fine",
        };
        lines.InsertRange(4, Enumerable.Repeat(new string('x', 500), 40));

        var entries = DalamudLogParser.ParseEntries(lines);
        Assert.Equal(2, entries.Count);
        Assert.StartsWith("System.Exception: boom\n   at A.Run()\n", entries[0].Continuation, StringComparison.Ordinal);
        Assert.True(entries[0].Continuation!.Length <= DalamudLogParser.MaxContinuationChars);
        Assert.Null(entries[1].Continuation);
    }

    [Fact]
    public void FiltersByPluginLevelTextAndTimeNewestLast()
    {
        var t = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<DalamudLogEntry>
        {
            new(t.AddMinutes(-90), DalamudLogLevel.Error, "A", "old error", null),
            new(t.AddMinutes(-30), DalamudLogLevel.Debug, "A", "debug noise", null),
            new(t.AddMinutes(-20), DalamudLogLevel.Warning, "a", "Texture missing", null),
            new(t.AddMinutes(-10), DalamudLogLevel.Error, "B", "other plugin", "stack with TEXTURE"),
            new(t.AddMinutes(-5), DalamudLogLevel.Error, "A", "texture failed", null),
            new(t.AddMinutes(-1), DalamudLogLevel.Information, null, "dalamud line", null),
        };

        var byPlugin = DalamudLogFilter.Select(entries, new DalamudLogQuery("A", DalamudLogLevel.Warning, null, t.AddMinutes(-60), 50));
        Assert.Equal("Texture missing|texture failed", string.Join("|", byPlugin.Entries.Select(e => e.Message)));
        Assert.False(byPlugin.Truncated);

        var byText = DalamudLogFilter.Select(entries, new DalamudLogQuery(null, DalamudLogLevel.Verbose, "texture", null, 50));
        Assert.Equal(3, byText.Matched);

        var limited = DalamudLogFilter.Select(entries, new DalamudLogQuery(null, DalamudLogLevel.Verbose, null, null, 2));
        Assert.True(limited.Truncated);
        Assert.Equal(6, limited.Matched);
        Assert.Equal("texture failed|dalamud line", string.Join("|", limited.Entries.Select(e => e.Message)));

        var counts = DalamudLogFilter.ErrorCounts(entries, t.AddMinutes(-60), 10);
        Assert.Equal(new[] { KeyValuePair.Create("A", 1), KeyValuePair.Create("B", 1) }, counts.ToArray());
    }

    [Fact]
    public void TailDropsThePartialFirstLineAndNeverReadsMoreThanAsked()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 2000; i++)
            sb.Append(Line(DateTimeOffset.UnixEpoch.AddSeconds(i), "INF", $"[P] line {i:0000} é")).Append("\r\n");
        File.WriteAllText(LogPath, sb.ToString(), new UTF8Encoding(false));

        var tail = DalamudLogTailReader.ReadTail(LogPath, 4096);
        Assert.True(tail.StartedMidFile);
        Assert.Equal(4096, tail.ScannedBytes);
        Assert.Equal(new FileInfo(LogPath).Length, tail.FileBytes);
        Assert.All(tail.Lines, l => Assert.True(DalamudLogParser.TryParseHeader(l, out _), l));
        Assert.EndsWith("line 1999 é", tail.Lines[^1], StringComparison.Ordinal);

        var whole = DalamudLogTailReader.ReadTail(LogPath, DalamudLogTailReader.MaxTailBytes);
        Assert.False(whole.StartedMidFile);
        Assert.Equal(2000, whole.Lines.Count);
    }

    [Fact]
    public void TailCapsTheWindowAtFourMiB()
    {
        using (var stream = new FileStream(LogPath, FileMode.Create))
        {
            var chunk = Encoding.ASCII.GetBytes(new string('z', 1023) + "\n");
            for (var i = 0; i < 5 * 1024; i++)
                stream.Write(chunk);
        }

        var tail = DalamudLogTailReader.ReadTail(LogPath, int.MaxValue);
        Assert.Equal(DalamudLogTailReader.MaxTailBytes, tail.ScannedBytes);
        Assert.True(tail.StartedMidFile);
    }

    [Fact]
    public void TailCutsHugeLinesAndSurvivesAWindowWithoutANewline()
    {
        File.WriteAllText(LogPath, Line(DateTimeOffset.UnixEpoch, "INF", "short") + "\n" + Line(DateTimeOffset.UnixEpoch, "WRN", new string('y', 100_000)) + "\n");
        var tail = DalamudLogTailReader.ReadTail(LogPath, DalamudLogTailReader.MaxTailBytes);
        Assert.Equal(2, tail.Lines.Count);
        Assert.True(tail.Lines[1].Length <= DalamudLogTailReader.MaxLineChars + 16);
        Assert.EndsWith("[cut]", tail.Lines[1], StringComparison.Ordinal);

        // The window falls entirely inside the huge line: nothing usable, and nothing thrown.
        var inside = DalamudLogTailReader.ReadTail(LogPath, 2048);
        Assert.True(inside.Lines.Count <= 1);
        Assert.Empty(DalamudLogParser.ParseEntries(inside.Lines));
    }

    [Fact]
    public void TailReadsWhileAnotherProcessHoldsTheFileOpenForWriting()
    {
        using var writer = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(Encoding.UTF8.GetBytes(Line(DateTimeOffset.UnixEpoch, "INF", "first") + "\n" + Line(DateTimeOffset.UnixEpoch, "INF", "half writ")));
        writer.Flush();

        var tail = DalamudLogTailReader.ReadTail(LogPath, 1024 * 1024);
        Assert.Equal(2, tail.Lines.Count);
        Assert.EndsWith("half writ", tail.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TailOfAnEmptyOrMissingFile()
    {
        File.WriteAllText(LogPath, "");
        Assert.Empty(DalamudLogTailReader.ReadTail(LogPath, 4096).Lines);

        // Rotation in progress: the file is gone for a moment.
        File.Delete(LogPath);
        Assert.ThrowsAny<IOException>(() => DalamudLogTailReader.ReadTail(LogPath, 4096));
    }

    [Fact]
    public void LocatesTheLogFromThePluginInterfacePathsOnly()
    {
        var candidates = DalamudLogLocator.Candidates(
            Path.Combine(root, "pluginConfigs", "XivMcp"),
            Path.Combine(root, "pluginConfigs", "XivMcp.json"),
            Path.Combine(root, "dalamudAssets", "dev"));
        Assert.Equal(Path.Combine(root, "dalamud.log"), candidates[0]);
        Assert.All(candidates, c => Assert.EndsWith("dalamud.log", c, StringComparison.Ordinal));
        Assert.Equal(candidates.Count, candidates.Distinct().Count());

        Assert.Null(DalamudLogLocator.FindExisting(candidates));
        File.WriteAllText(LogPath, "");
        Assert.Equal(LogPath, DalamudLogLocator.FindExisting(candidates));

        Assert.Empty(DalamudLogLocator.Candidates(null, "", "  "));
    }

    private DalamudLogProvider Provider() => new(
        FakeProxy.Create<IDalamudPluginInterface>(new()
        {
            ["get_ConfigDirectory"] = _ => new DirectoryInfo(Path.Combine(root, "pluginConfigs", "XivMcp")),
            ["get_ConfigFile"] = _ => new FileInfo(Path.Combine(root, "pluginConfigs", "XivMcp.json")),
            ["get_InstalledPlugins"] = _ => new[]
            {
                FakeProxy.Create<IExposedPlugin>(new() { ["get_InternalName"] = _ => "SomePlugin", ["get_Name"] = _ => "Some Plugin" }),
            },
        }),
        FakeProxy.Create<IClientState>(),
        FakeProxy.Create<IDataManager>());

    [Fact]
    public void ReadPluginLogReturnsScrubbedNewestLastEntries()
    {
        var now = DateTimeOffset.Now;
        File.WriteAllLines(LogPath,
        [
            Line(now.AddMinutes(-300), "ERR", "[SomePlugin] ancient"),
            Line(now.AddMinutes(-3), "DBG", "[SomePlugin] quiet"),
            Line(now.AddMinutes(-2), "ERR", "[SomePlugin] GET https://example.test/x?token=abc123 from 192.0.2.9 failed for /home/alice/cfg"),
            "System.Exception: mail alice@example.test",
            Line(now.AddMinutes(-1), "WRN", "[Other] unrelated"),
        ]);

        var result = Provider().ReadPluginLog(plugin: "some plugin", level: "warning", sinceMinutes: 60);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("error", entry.Level);
        Assert.Equal("SomePlugin", entry.Plugin);
        Assert.Equal("GET https://example.test/x?token=<redacted:secret> from <redacted:ipv4> failed for ~/cfg", entry.Message);
        Assert.Equal("System.Exception: mail <redacted:email>", entry.Details);
        Assert.False(result.Truncated);
        Assert.False(result.WindowStartsMidFile);

        var all = Provider().ReadPluginLog(level: "verbose", limit: 2);
        Assert.True(all.Truncated);
        Assert.Equal(4, all.Matched);
        Assert.Equal("Other", all.Entries[^1].Plugin);
    }

    [Fact]
    public void ReadPluginLogRejectsBadArgumentsAndReportsAMissingLog()
    {
        var provider = Provider();
        Assert.Equal(McpErrorCodes.InvalidArguments, Assert.Throws<McpToolException>(() => provider.ReadPluginLog(level: "loud")).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Assert.Throws<McpToolException>(() => provider.ReadPluginLog(sinceMinutes: 0)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Assert.Throws<McpToolException>(() => provider.ReadPluginLog(contains: new string('x', 201))).Code);
        Assert.Equal(McpErrorCodes.Unavailable, Assert.Throws<McpToolException>(() => provider.ReadPluginLog()).Code);
    }
}

using System.Text.RegularExpressions;

namespace XivMcp.Core.Tests;

/// <summary>
/// A source-level audit of the plugin projects, which cannot be loaded outside the game: every event
/// subscription (<c>X += OnSomething;</c>) and every Dalamud registration must be undone in the same
/// file, or the game keeps the old plugin alive on each reload. A line that is safe for a reason the
/// audit cannot see opts out with <c>// leak-audit: ok - reason</c>.
/// </summary>
public sealed partial class ReloadLeakAuditTests
{
    private static readonly (string Take, string Release)[] Pairs =
    [
        ("AddHandler(", "RemoveHandler("),
        ("AddWindow(", "Remove"), // RemoveWindow or RemoveAllWindows
        (".Subscribe(", ".Unsubscribe("),
        ("RegisterAction(", "UnregisterAction("),
        ("RegisterFunc(", "UnregisterFunc("),
        ("HookFromAddress", ".Dispose()"),
        ("HookFromSignature", ".Dispose()"),
        ("DtrBar.Get(", ".Remove()"),
        ("NewFontHandle(", ".Dispose()"),
    ];

    [GeneratedRegex(@"^\s*(?<event>[A-Za-z_][\w.]*)\s*\+=\s*(?<handler>(?:this\.)?[A-Z]\w*)\s*;", RegexOptions.Multiline)]
    private static partial Regex Subscription();

    private static string RepoRoot()
    {
        // the test project bakes it in: the build output may live outside the checkout
        var baked = typeof(ReloadLeakAuditTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
        if (baked is not null && Directory.Exists(Path.Combine(baked, "src")))
            return Path.GetFullPath(baked);

        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)))
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "tests")))
                return dir;
        }

        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    private static List<string> PluginSources()
    {
        var src = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
    }

    [Fact]
    public void TheAuditFindsTheSources() => Assert.True(PluginSources().Count > 10, "the audit found almost no sources: it is looking in the wrong place");

    [Fact]
    public void EverySubscriptionAndRegistrationIsUndoneInTheSameFile()
    {
        var problems = new List<string>();
        foreach (var file in PluginSources())
        {
            var lines = File.ReadAllLines(file);
            var text = string.Join('\n', lines.Where(l => !l.Contains("leak-audit: ok -", StringComparison.Ordinal)));
            var name = Path.GetRelativePath(RepoRoot(), file);
            foreach (Match m in Subscription().Matches(text))
            {
                var undo = new Regex(Regex.Escape(m.Groups["event"].Value) + @"\s*-=\s*" + Regex.Escape(m.Groups["handler"].Value) + @"\s*;");
                if (!undo.IsMatch(text))
                    problems.Add($"{name}: {m.Groups["event"].Value} += {m.Groups["handler"].Value} is never undone with -=");
            }

            foreach (var (take, release) in Pairs)
            {
                if (text.Contains(take, StringComparison.Ordinal) && !text.Contains(release, StringComparison.Ordinal))
                    problems.Add($"{name}: uses {take} but never {release}");
            }
        }

        Assert.True(problems.Count == 0, "reload leaks:\n" + string.Join('\n', problems));
    }
}

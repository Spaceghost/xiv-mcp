using System.Diagnostics;
using System.Reflection;
using XivMcp.Core;

namespace XivMcp.Core.Tests;

/// <summary>
/// changelog.json is the changelog: the plugin shows it in the What's new tab from the copy embedded
/// here, and CHANGELOG.md is rendered from the same file by tools/changelog.py. These tests keep both
/// ends honest — the embedded file parses and every entry carries a status the view can label, and
/// CHANGELOG.md still matches (the renderer is run with --check, never reimplemented here).
/// </summary>
public sealed class ChangelogTests
{
    private static readonly string[] Known = ["new", "fix", "beta", "next"];

    [Fact]
    public void BundledChangelogParses()
    {
        var log = Changelog.Bundled();
        Assert.NotEmpty(log.Releases);
        Assert.All(log.Releases, r =>
        {
            Assert.NotEmpty(r.Version);
            Assert.NotEmpty(r.Title);
            Assert.NotEmpty(r.Items);
            Assert.All(r.Items, i =>
            {
                Assert.Contains(i.Status, Known);
                Assert.NotEmpty(i.Text);
                Assert.NotEmpty(Changelog.Label(i.Status));
            });
        });
    }

    [Fact]
    public void UnreleasedSectionIsHeadedByItsTitleAlone()
    {
        var unreleased = new ChangelogRelease("next", "In the workshop", "", [new ChangelogItem("beta", "x")]);
        var released = new ChangelogRelease("0.1", "First light", "", [new ChangelogItem("new", "x")]);
        Assert.True(unreleased.Unreleased);
        Assert.Equal("In the workshop", unreleased.Heading);
        Assert.False(released.Unreleased);
        Assert.Equal("0.1 · First light", released.Heading);
    }

    [Fact]
    public void ChangelogMarkdownMatchesTheSource()
    {
        var root = RepositoryRoot();
        var script = Path.Combine(root, "tools", "changelog.py");
        Assert.True(File.Exists(script), $"no {script}");

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo("python3", [script, "--check"])
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
        }
        catch (Exception ex)
        {
            Assert.Fail($"python3 is needed to check CHANGELOG.md against changelog.json: {ex.Message}");
            return;
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"CHANGELOG.md is out of date; run tools/changelog.py\n{output}");
        }
    }

    /// <summary>The checkout, which the build output (Directory.Build.props) lives outside of.</summary>
    private static string RepositoryRoot() =>
        typeof(ChangelogTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "RepositoryRoot").Value!;
}

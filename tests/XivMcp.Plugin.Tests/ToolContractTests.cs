using System.Reflection;
using System.Text.RegularExpressions;
using XivMcp.Core;

namespace XivMcp.Plugin.Tests;

/// <summary>
/// The rules every tool has to follow, checked over the plugin assembly's attributes (no provider is constructed).
/// The approval rule is the important one: a tool that is not read-only must go through the server's single approval
/// gate — the one the player's "Ask me before anything changes" checkbox governs — unless it is on the short list
/// below of tools that only display something to the owner. Adding a mutating tool outside the gate fails here.
/// </summary>
public sealed partial class ToolContractTests
{
    /// <summary>
    /// Ui-tier tools that are not gated, and why that is fine: they show or file something for the owner alone and
    /// change nothing in the game client. Do not add a tool here to make the test pass; gate it instead.
    /// </summary>
    private static readonly Dictionary<string, string> DisplayOnly = new(StringComparer.Ordinal)
    {
        ["print_echo"] = "a line in the owner's own chat log",
        ["show_toast"] = "a transient on-screen message",
        ["show_notification"] = "a Dalamud notification card",
        ["post_status"] = "the agent board",
        ["clear_status"] = "the agent board",
        ["post_objective"] = "the plugin's own objectives overlay",
        ["update_objective"] = "the plugin's own objectives overlay",
        ["clear_objectives"] = "the plugin's own objectives overlay",
        ["load_objective_pack"] = "the plugin's own objectives overlay",
        ["request_action"] = "files an approval ticket: it IS the gate",
        ["cancel_ticket"] = "withdraws the caller's own ticket",
        ["take_screenshot"] = "captures what is on screen into the plugin's folder",
    };

    private static readonly string[] ReadVerbs = ["get_", "list_", "search_", "find_", "read_", "compare_", "convert_"];

    private static IEnumerable<(McpToolAttribute Tool, MethodInfo Method)> Tools() =>
        typeof(Plugin).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpProviderAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => (Tool: m.GetCustomAttribute<McpToolAttribute>()!, Method: m))
            .Where(x => x.Tool is not null);

    [Fact]
    public void EveryToolThatChangesSomethingGoesThroughTheApprovalGate()
    {
        var outside = Tools()
            .Where(x => x.Tool.Permission != ToolPermission.Read && !x.Tool.NeedsApproval && !DisplayOnly.ContainsKey(x.Tool.Name))
            .Select(x => x.Tool.Name).ToArray();
        Assert.True(outside.Length == 0, "not read-only and outside the approval gate (make it Action/Chat or RequiresApproval): " + string.Join(", ", outside));

        var stale = DisplayOnly.Keys.Where(n => Tools().All(x => x.Tool.Name != n || x.Tool.Permission != ToolPermission.Ui)).ToArray();
        Assert.True(stale.Length == 0, "DisplayOnly names that are no longer ungated Ui tools: " + string.Join(", ", stale));
    }

    [Fact]
    public void ReadToolsAreNamedAsReadsAndNothingElseIs()
    {
        foreach (var (tool, _) in Tools())
        {
            var readName = ReadVerbs.Any(v => tool.Name.StartsWith(v, StringComparison.Ordinal));
            if (tool.Permission == ToolPermission.Read)
                Assert.True(readName || tool.Name.EndsWith("_query", StringComparison.Ordinal), $"{tool.Name} is Read tier but is not named like a read");
            else if (tool.NeedsApproval)
                Assert.False(readName, $"{tool.Name} changes something but is named like a read");
        }
    }

    [Fact]
    public void GatedToolsTellThePlayerWhatWillHappenUsingRealArguments()
    {
        foreach (var (tool, method) in Tools().Where(x => x.Tool.NeedsApproval))
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.ApprovalSummary), $"{tool.Name} needs approval but has no ApprovalSummary");
            var parameters = method.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.Ordinal);
            foreach (Match m in Placeholder().Matches(tool.ApprovalSummary!))
                Assert.True(parameters.Contains(m.Groups[1].Value), $"{tool.Name}: ApprovalSummary names {{{m.Groups[1].Value}}}, which is not an argument");
        }
    }

    /// <summary>Text that leaves the client or is written somewhere is shown verbatim: every string argument of a Chat tool appears in its sentence.</summary>
    [Fact]
    public void ChatToolsShowTheirTextVerbatim()
    {
        foreach (var (tool, method) in Tools().Where(x => x.Tool.Permission == ToolPermission.Chat))
        {
            Assert.True(tool.OpenWorld, $"{tool.Name}: Chat tools are openWorld");
            foreach (var p in method.GetParameters().Where(p => p.ParameterType == typeof(string)))
                Assert.Contains("{" + p.Name + "}", tool.ApprovalSummary!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryToolDeclaresItsDataSourcesAndHonestHints()
    {
        foreach (var (tool, _) in Tools())
        {
            Assert.True(tool.Sources is { Length: > 0 }, $"{tool.Name} declares no Sources");
            Assert.All(tool.Sources!, s => Assert.Matches("^(lumina|client|dalamud|ipc|http|file|clock|xivmcp):.+", s));
            Assert.False(string.IsNullOrWhiteSpace(tool.Title), $"{tool.Name} has no Title");
            Assert.True(tool.Description.Length >= 40, $"{tool.Name}: the description is the product; write one");
            if (tool.Permission == ToolPermission.Read)
                Assert.False(tool.Destructive, $"{tool.Name}: a read cannot be destructive");
            if (tool.Availability == ToolAvailability.Static)
            {
                Assert.Equal(ToolPermission.Read, tool.Permission);
                Assert.False(tool.RequiresLogin, $"{tool.Name}: a static tool cannot require login");
                Assert.DoesNotContain(tool.Sources!, s => s.StartsWith("ipc:", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void ListsArePagedAndCapped()
    {
        foreach (var (tool, method) in Tools())
        {
            foreach (var p in method.GetParameters().Where(p => p.Name is "limit" or "count" or "max"))
            {
                var max = p.GetCustomAttribute<McpParamAttribute>()?.Maximum ?? double.NaN;
                Assert.True(max <= 500, $"{tool.Name}.{p.Name} needs a Maximum of at most 500 (has {max})");
            }
        }
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}

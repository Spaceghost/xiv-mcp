using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Objectives;
using XivMcp.Plugin.Providers.Objectives;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

public sealed class ObjectiveProviderTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "xivmcp-objective-provider-" + Guid.NewGuid().ToString("N"));
    private readonly ObjectiveStore store = new(null);
    private readonly ObjectiveTracker tracker;
    private readonly ObjectivesProvider provider;

    public ObjectiveProviderTests()
    {
        tracker = new ObjectiveTracker(
            store, new Configuration(), FakeProxy.Create<IFramework>(), FakeProxy.Create<IClientState>(), FakeProxy.Create<IObjectTable>(),
            FakeProxy.Create<IDataManager>(), FakeProxy.Create<IToastGui>(), FakeProxy.Create<IPluginLog>());
        provider = new ObjectivesProvider(tracker, FakeProxy.Create<IMcpNotifier>());
    }

    public void Dispose()
    {
        provider.Dispose();
        tracker.Dispose();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task PostAndUpdate()
    {
        var posted = await provider.PostObjective("Hero", "A Terminal Under the Stars", ["Go", "Capture"], 137, 31, 30, eorzeaTime: "21:00-03:00", weather: ["Clear Skies"]);
        Assert.Equal("hero", posted.Id);
        Assert.Equal(0, posted.CurrentStep);
        Assert.Equal("Go", posted.CurrentStepText);
        Assert.Equal("21:00-03:00", posted.EorzeaTime);
        Assert.Equal("mcp", posted.Source);

        var advanced = await provider.UpdateObjective("hero", advance: true, note: "almost there");
        Assert.Equal("Capture", advanced.CurrentStepText);
        Assert.Equal("almost there", advanced.Note);

        var done = await provider.UpdateObjective("hero", advance: true);
        Assert.True(done.Completed);

        var reopened = await provider.UpdateObjective("hero", step: 1, note: "");
        Assert.False(reopened.Completed);
        Assert.Null(reopened.Note);
    }

    [Theory]
    [InlineData("", "T", null, null, null, "id must contain")]
    [InlineData("a", " ", null, null, null, "title must not be empty")]
    [InlineData("a", "T", null, 10f, 10f, "need territoryId")]
    [InlineData("a", "T", 137u, 10f, null, "both x and y")]
    [InlineData("a", "T", null, null, null, "not HH:MM")]
    public async Task PostRejectsBadInput(string id, string title, uint? territory, float? x, float? y, string message)
    {
        var time = message == "not HH:MM" ? "dusk" : null;
        var ex = await Assert.ThrowsAsync<McpToolException>(() => provider.PostObjective(id, title, null, territory, x, y, eorzeaTime: time));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task UpdateRejectsBadInput()
    {
        await provider.PostObjective("a", "A", ["one"]);
        Assert.Contains("Nothing to update", (await Assert.ThrowsAsync<McpToolException>(() => provider.UpdateObjective("a"))).Message);
        Assert.Contains("not both", (await Assert.ThrowsAsync<McpToolException>(() => provider.UpdateObjective("a", advance: true, step: 0))).Message);
        Assert.Contains("No objective 'b'", (await Assert.ThrowsAsync<McpToolException>(() => provider.UpdateObjective("b", advance: true))).Message);
        Assert.Contains("step must be between 0 and 1", (await Assert.ThrowsAsync<McpToolException>(() => provider.UpdateObjective("a", step: 5))).Message);
    }

    [Fact]
    public async Task ListAtTheTitleScreenAndClear()
    {
        await provider.PostObjective("a", "A", ["one"], 137, 31, 30);
        await provider.PostObjective("b", "B");
        await provider.UpdateObjective("b", complete: true);

        var list = provider.ListObjectives();
        Assert.Equal(2, list.Count);
        Assert.Equal(1, list.Active);
        var a = list.Objectives[0];
        Assert.NotNull(a.Status);
        Assert.False(a.Status!.Ready);
        Assert.False(a.Status.InZone);
        Assert.Single(provider.ListObjectives(includeCompleted: false).Objectives);

        Assert.Equal(new ClearObjectivesResult(1, 1), provider.ClearObjectives(completedOnly: true));
        Assert.Equal(new ClearObjectivesResult(1, 0), provider.ClearObjectives());
    }

    [Fact]
    public void LoadPackFromJsonAndFile()
    {
        Assert.Contains("either json or path", Assert.Throws<McpToolException>(() => provider.LoadObjectivePack()).Message);

        var result = provider.LoadObjectivePack(json: """{"title":"P","quests":[{"id":"x","name":"X"},{"id":"y"}]}""");
        Assert.Equal("P", result.Title);
        Assert.Equal(["x"], result.Ids);
        Assert.Single(result.Errors);

        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pack.json");
        File.WriteAllText(file, """[{"id":"z","title":"Z","steps":["s"]}]""");
        var fromFile = provider.LoadObjectivePack(path: file);
        Assert.Equal(["z"], fromFile.Ids);
        Assert.Equal(2, fromFile.Total);
        Assert.Equal("pack:pack.json", store.Get("z")!.Source);

        Assert.Contains("must end in .json", Assert.Throws<McpToolException>(() => provider.LoadObjectivePack(path: Path.Combine(dir, "pack.txt"))).Message);
        Assert.Contains("not found", Assert.Throws<McpToolException>(() => provider.LoadObjectivePack(path: Path.Combine(dir, "missing.json"))).Message);
    }

    [Theory]
    [InlineData("", "list", null)]
    [InlineData("list", "list", null)]
    [InlineData(" done  hero-costa-night ", "done", "hero-costa-night")]
    [InlineData("FLAG hero", "flag", "hero")]
    [InlineData("load ~/packs/shot quests.json", "load", "~/packs/shot quests.json")]
    [InlineData("clear-done", "clear-done", null)]
    [InlineData("hide", "hide", null)]
    public void ParsesChatCommands(string text, string verb, string? argument)
    {
        var command = ObjectiveCommands.Parse(text, out var error);
        Assert.Null(error);
        Assert.Equal(new QuestCommand(verb, argument), command);
    }

    [Theory]
    [InlineData("frobnicate", "unknown quests command")]
    [InlineData("done", "needs an objective id")]
    [InlineData("load   ", "needs a file path")]
    public void RejectsBadChatCommands(string text, string message)
    {
        Assert.Null(ObjectiveCommands.Parse(text, out var error));
        Assert.Contains(message, error);
    }
}

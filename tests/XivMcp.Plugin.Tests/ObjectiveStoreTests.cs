using XivMcp.Plugin.Objectives;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

public sealed class ObjectiveStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    // The first real pack (ghostty-dalamud docs/media/shot-quests.json), trimmed to the shapes it uses; the full file
    // parsed with no errors when this was written.
    private const string ShotPack = """
        {
          "title": "Ghostty in Eorzea: media quests",
          "version": 1,
          "before_every_shot": ["Use /term showcase"],
          "quests": [
            {
              "id": "hero-costa-night",
              "name": "A Terminal Under the Stars",
              "kind": "video+screenshot",
              "zone": "Eastern La Noscea",
              "territoryId": 137,
              "spot": "Costa del Sol, the beach below the villa",
              "map": { "x": 31.0, "y": 30.0 },
              "eorzea_time": "21:00-03:00",
              "weather": ["Clear Skies", "Fair Skies"],
              "setup": ["/term showcase", "Two pets beside you"],
              "capture": "Slow 20 s orbit from the showcase camera.",
              "features": ["world screens"]
            },
            {
              "id": "summerford-walkup",
              "name": "Walk-up",
              "zone": "Middle La Noscea",
              "territoryId": 134,
              "spot": "Summerford Farms",
              "map": { "x": 20.0, "y": 21.0 },
              "eorzea_time": "17:00-18:30",
              "weather": ["Clear Skies", "Fair Skies"],
              "setup": [],
              "capture": "Pets trail you."
            },
            {
              "id": "settings-tour",
              "name": "Settings tour",
              "zone": "anywhere",
              "territoryId": null,
              "spot": "anywhere quiet",
              "map": null,
              "eorzea_time": "any",
              "weather": ["any"],
              "setup": ["Open settings"],
              "capture": "Scroll through every tab."
            },
            { "id": "broken", "name": "Bad time", "eorzea_time": "dusk" },
            { "id": "hero-costa-night", "name": "Duplicate" }
          ],
          "homepage_video": { "length": "75 s" }
        }
        """;

    private readonly string dir = Path.Combine(Path.GetTempPath(), "xivmcp-objectives-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ---- packs -----------------------------------------------------------------------------

    [Fact]
    public void ParsesTheShotQuestPack()
    {
        var result = ObjectivePack.Parse(ShotPack, "pack:shot-quests.json", Now);
        Assert.Equal("Ghostty in Eorzea: media quests", result.Title);
        Assert.Equal(["hero-costa-night", "summerford-walkup", "settings-tour"], result.Objectives.Select(o => o.Id));
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("Entry 4 (broken)", result.Errors[0]);
        Assert.Contains("'dusk'", result.Errors[0]);
        Assert.Contains("duplicate id 'hero-costa-night'", result.Errors[1]);

        var hero = result.Objectives[0];
        Assert.Equal("A Terminal Under the Stars", hero.Title);
        Assert.Equal("Eastern La Noscea", hero.Zone);
        Assert.Equal(137u, hero.TerritoryId);
        Assert.Equal(31f, hero.MapX);
        Assert.Equal(30f, hero.MapY);
        Assert.True(hero.HasSpot);
        Assert.Equal("21:00-03:00", hero.Conditions.EorzeaTime);
        Assert.Equal(["Clear Skies", "Fair Skies"], hero.Conditions.WeatherConstraint);
        Assert.Equal(
            ["Go to Costa del Sol, the beach below the villa", "/term showcase", "Two pets beside you", "Capture: Slow 20 s orbit from the showcase camera."],
            hero.Steps.Select(s => s.Text));
        Assert.Equal("pack:shot-quests.json", hero.Source);
        Assert.Equal(0, hero.CurrentStepIndex);

        Assert.Equal("17:00-18:30", result.Objectives[1].Conditions.EorzeaTime);

        var tour = result.Objectives[2];
        Assert.Null(tour.TerritoryId);
        Assert.False(tour.HasSpot);
        Assert.Null(tour.Conditions.EorzeaTime);
        Assert.Empty(tour.Conditions.WeatherConstraint);
    }

    [Theory]
    [InlineData("""[{"id":"a","title":"A","steps":["one","two"],"territoryId":128,"x":10.8,"y":"11.4","weather":"Rain","radius":12}]""")]
    [InlineData("""{"objectives":[{"id":"a","title":"A","steps":[{"text":"one"},{"text":"two"}],"territoryId":128,"map":{"x":10.8,"y":11.4},"weather":["Rain"],"radius":12,}]}""")]
    public void ParsesOtherShapes(string json)
    {
        var o = Assert.Single(ObjectivePack.Parse(json, null, Now).Objectives);
        Assert.Equal(["one", "two"], o.Steps.Select(s => s.Text));
        Assert.Equal(10.8f, o.MapX);
        Assert.Equal(11.4f, o.MapY);
        Assert.Equal(["Rain"], o.Conditions.Weather);
        Assert.Equal(12f, o.Conditions.Radius);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{"title":"x"}""", "\"quests\"")]
    [InlineData("42", "array of quests")]
    public void RejectsBadPacks(string json, string message) =>
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => ObjectivePack.Parse(json, null, Now)).Message);

    [Theory]
    [InlineData("""[{"id":"a","title":"A","map":{"x":10}}]""", "both x and y")]
    [InlineData("""[{"id":"a","title":"A","map":{"x":10,"y":10}}]""", "need territoryId")]
    [InlineData("""[{"id":"a","title":"A","territoryId":128,"map":{"x":99,"y":10}}]""", "outside the range")]
    [InlineData("""[{"id":"a","title":"A","radius":0}]""", "radius")]
    [InlineData("""[{"id":"!!!","title":"A"}]""", "id must contain")]
    [InlineData("""[{"id":"a"}]""", "title must not be empty")]
    [InlineData("""[{"id":"a","title":"A","territoryId":1.5}]""", "not a row id")]
    [InlineData("""[{"id":"a","title":"A","weather":{"no":1}}]""", "weather must be")]
    public void ReportsInvalidEntries(string json, string message) =>
        Assert.Contains(message, Assert.Single(ObjectivePack.Parse(json, null, Now).Errors));

    [Theory]
    [InlineData("/home/player/pack.json", true, "/home/player", @"Z:\home\player\pack.json")]
    [InlineData("~/pack.json", true, "/home/player", @"Z:\home\player\pack.json")]
    [InlineData(@"C:\packs\a.json", true, "/home/player", @"C:\packs\a.json")]
    [InlineData("\"/tmp/a b.json\"", true, null, @"Z:\tmp\a b.json")]
    [InlineData("~/pack.json", false, "/home/player/", "/home/player/pack.json")]
    public void ResolvesHostPathsUnderWine(string input, bool windows, string? home, string expected) =>
        Assert.Equal(expected, ObjectivePack.ResolvePath(input, windows, home));

    // ---- progress --------------------------------------------------------------------------

    private static Objective Three() => ObjectiveFactory.Create(new ObjectiveDraft { Id = "Three Steps!", Title = "T", Steps = ["a", "b", "c"] }, Now);

    [Fact]
    public void IdsAreNormalized() => Assert.Equal("three-steps", Three().Id);

    [Fact]
    public void AdvancingCompletesAfterTheLastStep()
    {
        var o = Three();
        o = ObjectiveFactory.Advance(o, Now);
        Assert.Equal(1, o.CurrentStepIndex);
        Assert.Equal("b", o.CurrentStepText);
        o = ObjectiveFactory.Advance(ObjectiveFactory.Advance(o, Now), Now);
        Assert.True(o.Completed);
        Assert.Equal(-1, o.CurrentStepIndex);
        Assert.Equal(Now, o.CompletedAt);
    }

    [Fact]
    public void SettingTheStepMovesBackAndForth()
    {
        var o = ObjectiveFactory.SetCurrentStep(Three(), 3, Now);
        Assert.True(o.Completed);
        o = ObjectiveFactory.SetCurrentStep(o, 1, Now);
        Assert.False(o.Completed);
        Assert.Null(o.CompletedAt);
        Assert.Equal([true, false, false], o.Steps.Select(s => s.Done));
        Assert.Throws<ArgumentException>(() => ObjectiveFactory.SetCurrentStep(o, 4, Now));
        Assert.Throws<ArgumentException>(() => ObjectiveFactory.SetCurrentStep(o, -1, Now));
    }

    [Fact]
    public void CompleteAndReopen()
    {
        var done = ObjectiveFactory.Complete(Three(), true, Now);
        Assert.True(done.Completed);
        Assert.All(done.Steps, s => Assert.True(s.Done));
        var reopened = ObjectiveFactory.Complete(done, false, Now);
        Assert.False(reopened.Completed);
        Assert.Null(reopened.CompletedAt);
        Assert.Same(reopened, ObjectiveFactory.Complete(reopened, false, Now));
    }

    [Fact]
    public void ObjectiveWithoutStepsCompletesOnAdvance()
    {
        var o = ObjectiveFactory.Create(new ObjectiveDraft { Id = "x", Title = "X" }, Now);
        Assert.Null(o.CurrentStepText);
        Assert.True(ObjectiveFactory.Advance(o, Now).Completed);
    }

    // ---- store and persistence -------------------------------------------------------------

    [Fact]
    public void PersistsAndReloads()
    {
        var file = Path.Combine(dir, "objectives.json");
        var store = new ObjectiveStore(file);
        store.Upsert(ObjectivePack.Parse(ShotPack, "pack", Now).Objectives);
        store.Update("hero-costa-night", o => ObjectiveFactory.Advance(o, Now) with { Note = "on my way" });
        store.Update("settings-tour", o => ObjectiveFactory.Complete(o, true, Now));
        Assert.Null(store.LastError);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".tmp"));

        var reloaded = new ObjectiveStore(file);
        reloaded.Load();
        Assert.Null(reloaded.LastError);
        Assert.Equal(store.Snapshot().Select(o => o.Id), reloaded.Snapshot().Select(o => o.Id));
        var hero = reloaded.Get("hero-costa-night")!;
        Assert.Equal(1, hero.CurrentStepIndex);
        Assert.Equal("on my way", hero.Note);
        Assert.Equal(137u, hero.TerritoryId);
        Assert.Equal(31f, hero.MapX);
        Assert.Equal("21:00-03:00", hero.Conditions.EorzeaTime);
        Assert.Equal(["Clear Skies", "Fair Skies"], hero.Conditions.Weather);
        Assert.Equal(30f, hero.Conditions.Radius);
        Assert.True(reloaded.Get("settings-tour")!.Completed);

        var text = File.ReadAllText(file);
        Assert.Contains("\"version\": 1", text);
        Assert.DoesNotContain("currentStepIndex", text);
        Assert.DoesNotContain("timeWindow", text);
    }

    [Fact]
    public void MissingFileIsEmptyAndDamagedFileIsKeptAside()
    {
        var file = Path.Combine(dir, "objectives.json");
        var store = new ObjectiveStore(file);
        store.Load();
        Assert.Equal(0, store.Count);
        Assert.Null(store.LastError);

        Directory.CreateDirectory(dir);
        File.WriteAllText(file, "{ broken");
        store.Load();
        Assert.Equal(0, store.Count);
        Assert.NotNull(store.LastError);
        Assert.True(File.Exists(file + ".bad"));
    }

    [Fact]
    public void ReloadingAPackKeepsProgressUnlessStepsChanged()
    {
        var store = new ObjectiveStore(null);
        store.Upsert(ObjectivePack.Parse(ShotPack, "pack", Now).Objectives);
        store.Update("hero-costa-night", o => ObjectiveFactory.Advance(o, Now));
        store.Upsert(ObjectivePack.Parse(ShotPack, "pack", Now.AddHours(1)).Objectives);
        var hero = store.Get("hero-costa-night")!;
        Assert.Equal(1, hero.CurrentStepIndex);
        Assert.Equal(Now, hero.CreatedAt);

        var changed = ObjectivePack.Parse(ShotPack.Replace("Two pets beside you", "Three pets"), "pack", Now).Objectives;
        store.Upsert(changed);
        Assert.Equal(0, store.Get("hero-costa-night")!.CurrentStepIndex);
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void RemoveAndEvents()
    {
        var store = new ObjectiveStore(null, new ManualTimeProvider());
        var changes = 0;
        var completed = new List<string>();
        store.Changed += () => changes++;
        store.Completed += o => completed.Add(o.Id);
        store.Upsert(ObjectivePack.Parse(ShotPack, "pack", Now).Objectives);
        Assert.Null(store.Update("nope", o => o));
        store.Update("settings-tour", o => ObjectiveFactory.Complete(o, true, Now));
        store.Update("settings-tour", o => ObjectiveFactory.Complete(o, true, Now)); // unchanged: no event
        Assert.Equal(["settings-tour"], completed);
        Assert.Equal(2, changes);

        Assert.Equal(1, store.Remove(null, completedOnly: true));
        Assert.Equal(0, store.Remove("hero-costa-night", completedOnly: true));
        Assert.Equal(1, store.Remove("Hero-Costa-Night"));
        Assert.Equal(1, store.Remove(null));
        Assert.Equal(0, store.Count);
        Assert.Equal(5, changes);
    }
}

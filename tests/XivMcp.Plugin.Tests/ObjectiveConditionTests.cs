using System.Numerics;
using XivMcp.Plugin.Objectives;
using XivMcp.Plugin.Util;

namespace XivMcp.Plugin.Tests;

public class ObjectiveConditionTests
{
    // A La Noscea-like table: Clear 20, Fair 30, Clouds 20, Fog 10, Rain 10, Showers 10.
    private static readonly WeatherTable Table = new(
    [
        new WeatherSlot(1, 20, ["Clear Skies"]),
        new WeatherSlot(2, 30, ["Fair Skies", "Heiter"]),
        new WeatherSlot(3, 20, ["Clouds"]),
        new WeatherSlot(4, 10, ["Fog"]),
        new WeatherSlot(7, 10, ["Rain"]),
        new WeatherSlot(8, 10, ["Showers"]),
    ]);

    private static readonly ZoneInfo Zone = new(137, "Eastern La Noscea", 100, 0, 0, Table);

    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    // ---- time windows ----------------------------------------------------------------------

    [Theory]
    [InlineData("21:00-03:00", 21 * 60, 3 * 60)]
    [InlineData("17:00-18:30", 17 * 60, 18 * 60 + 30)]
    [InlineData("9-17", 9 * 60, 17 * 60)]
    [InlineData("22:00–24:00", 22 * 60, 24 * 60)]
    [InlineData(" 05:15 - 06:45 ", 5 * 60 + 15, 6 * 60 + 45)]
    public void ParsesWindows(string text, int start, int end)
    {
        Assert.True(EorzeaTimeWindow.TryParse(text, out var window, out var error), error);
        Assert.Equal(new EorzeaTimeWindow(start, end), window);
    }

    [Theory]
    [InlineData("any")]
    [InlineData("ANY")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("00:00-24:00")]
    [InlineData("06:00-06:00")]
    public void AnyWindows(string? text)
    {
        Assert.True(EorzeaTimeWindow.TryParse(text, out var window, out _));
        Assert.True(window.IsAny);
        Assert.True(window.Contains(0) && window.Contains(719) && window.Contains(1439));
    }

    [Theory]
    [InlineData("25:00-03:00")]
    [InlineData("21:60-03:00")]
    [InlineData("night")]
    [InlineData("21:00")]
    [InlineData("21:00-03:00-04:00")]
    [InlineData("24:30-01:00")]
    public void RejectsBadWindows(string text)
    {
        Assert.False(EorzeaTimeWindow.TryParse(text, out _, out var error));
        Assert.Contains(text.Trim(), error);
    }

    [Fact]
    public void WindowAcrossMidnight()
    {
        var w = EorzeaTimeWindow.Parse("21:00-03:00");
        Assert.True(w.WrapsMidnight);
        Assert.True(w.Contains(21 * 60));
        Assert.True(w.Contains(23 * 60 + 59));
        Assert.True(w.Contains(0));
        Assert.True(w.Contains(2 * 60 + 59));
        Assert.False(w.Contains(3 * 60));
        Assert.False(w.Contains(20 * 60 + 59));
        Assert.False(w.Contains(12 * 60));

        // At 20:00 it opens in one Eorzea hour; at 02:00 it closes in one; at 23:00 it closes in four.
        Assert.Equal(3600, w.SecondsUntilOpen(Et(20, 0)));
        Assert.Equal(0, w.SecondsUntilOpen(Et(22, 0)));
        Assert.Equal(3600, w.SecondsUntilClose(Et(2, 0)));
        Assert.Equal(4 * 3600, w.SecondsUntilClose(Et(23, 0)));
        Assert.Null(w.SecondsUntilClose(Et(12, 0)));
        // At 03:00 (just closed) the next opening is 18 hours away.
        Assert.Equal(18 * 3600, w.SecondsUntilOpen(Et(3, 0)));
    }

    [Fact]
    public void WindowEndingAtMidnight()
    {
        var w = EorzeaTimeWindow.Parse("22:00-24:00");
        Assert.False(w.WrapsMidnight);
        Assert.True(w.Contains(23 * 60 + 59));
        Assert.False(w.Contains(0));
        Assert.Equal(1800, w.SecondsUntilClose(Et(23, 30)));
        Assert.Equal("22:00-00:00", w.ToString());
    }

    // ---- weather ---------------------------------------------------------------------------

    [Fact]
    public void WeatherTableMatchesTheGameAlgorithm()
    {
        byte[] rates = [20, 30, 20, 10, 10, 10];
        for (var i = 0; i < 500; i++)
        {
            var unix = T0.ToUnixTimeSeconds() + (i * 977L);
            var index = GameMath.PickWeatherIndex(rates, GameMath.WeatherTarget(GameMath.WeatherWindowStart(unix)));
            Assert.Equal(Table.Slots[index].Id, Table.WeatherAt(unix)!.Id);
        }
    }

    [Fact]
    public void WeatherNamesMatchCaseInsensitivelyAndByAlias()
    {
        var fair = Table.Slots[1];
        Assert.True(fair.Matches("fair skies"));
        Assert.True(fair.Matches(" HEITER "));
        Assert.False(fair.Matches("Clear Skies"));
        Assert.True(Table.CanHaveAny(["Blizzards", "rain"]));
        Assert.False(Table.CanHaveAny(["Blizzards", "Heat Waves"]));
    }

    [Fact]
    public void AnyWeatherIsNoConstraint()
    {
        var c = new ObjectiveConditions { Weather = ["any"] };
        Assert.Empty(c.WeatherConstraint);
        c = new ObjectiveConditions { Weather = ["Clear Skies", " clear skies ", "", "Fair Skies"] };
        Assert.Equal(["Clear Skies", "Fair Skies"], c.WeatherConstraint);
    }

    // ---- next window -----------------------------------------------------------------------

    [Theory]
    [InlineData("21:00-03:00", new[] { "Clear Skies", "Fair Skies" })]
    [InlineData("17:00-18:30", new[] { "Clear Skies", "Fair Skies" })]
    [InlineData("any", new[] { "Rain", "Showers" })]
    [InlineData("10:00-15:00", new string[0])]
    [InlineData("16:00-18:00", new[] { "Fog" })]
    public void NextWindowIsTheEarliestMatchingSecond(string time, string[] weather)
    {
        var window = EorzeaTimeWindow.Parse(time);
        for (var k = 0; k < 6; k++)
        {
            var from = T0.AddSeconds(k * 3917);
            var found = ObjectiveEvaluator.FindNextWindow(window, weather, Table, from, TimeSpan.FromDays(7));
            Assert.NotNull(found);
            var (start, end) = found.Value;
            Assert.True(start >= from);
            Assert.True(Matches(window, weather, start.ToUnixTimeSeconds()), $"start {start} does not match");
            Assert.NotNull(end);
            Assert.True(end > start);
            Assert.False(Matches(window, weather, end!.Value.ToUnixTimeSeconds()), $"end {end} still matches");

            // Nothing earlier matches, checked second by second.
            for (var t = from.ToUnixTimeSeconds(); t < start.ToUnixTimeSeconds(); t++)
                Assert.False(Matches(window, weather, t), $"{DateTimeOffset.FromUnixTimeSeconds(t)} matches before {start}");
            // And everything inside matches.
            for (var t = start.ToUnixTimeSeconds(); t < end.Value.ToUnixTimeSeconds(); t += 7)
                Assert.True(Matches(window, weather, t));
        }
    }

    [Fact]
    public void NextWindowWithoutAnyConstraintIsNowForever()
    {
        var found = ObjectiveEvaluator.FindNextWindow(EorzeaTimeWindow.Any, [], null, T0, TimeSpan.FromDays(1));
        Assert.Equal((T0, (DateTimeOffset?)null), found);
    }

    [Fact]
    public void NextWindowNeedsATableForWeather()
    {
        Assert.Null(ObjectiveEvaluator.FindNextWindow(EorzeaTimeWindow.Any, ["Rain"], null, T0, TimeSpan.FromDays(1)));
        Assert.Null(ObjectiveEvaluator.FindNextWindow(EorzeaTimeWindow.Any, ["Blizzards"], Table, T0, TimeSpan.FromDays(1)));
    }

    // ---- evaluation ------------------------------------------------------------------------

    private static Objective Quest(string time = "any", string[]? weather = null, float? x = 31, float? y = 30, uint? territory = 137) => new()
    {
        Id = "q",
        Title = "A Terminal Under the Stars",
        TerritoryId = territory,
        Zone = "Eastern La Noscea",
        MapX = x,
        MapY = y,
        Steps = [new("Go there", false), new("Capture", false)],
        Conditions = new ObjectiveConditions { EorzeaTime = time, Weather = weather ?? [], Radius = 30 },
    };

    private static ObjectiveContext Ctx(DateTimeOffset now, uint territory = 137, Vector3? pos = null, string? weather = null) =>
        new(now, territory, pos, weather is null ? null : [weather], id => id == 137 ? Zone : null);

    private static Vector3 SpotWorld(float x, float y)
    {
        var w = Zone.MapToWorld(x, y);
        return new Vector3(w.X, 12, w.Y);
    }

    [Fact]
    public void ReadyWhenAtTheSpotInTheWindow()
    {
        var now = FirstUnixWhere(t => EorzeaTimeWindow.Parse("21:00-03:00").Contains(MinuteAt(t)));
        var status = ObjectiveEvaluator.Evaluate(Quest("21:00-03:00"), Ctx(now, pos: SpotWorld(31.2f, 30.1f)));
        Assert.True(status.Ready);
        Assert.True(status.InZone);
        Assert.True(status.NearSpot);
        Assert.True(status.TimeOk);
        Assert.Null(status.WeatherOk);
        Assert.StartsWith("Ready now (", status.Summary);
        Assert.NotNull(status.NextWindowEnd);
    }

    [Fact]
    public void NotReadyFarFromTheSpot()
    {
        var status = ObjectiveEvaluator.Evaluate(Quest(), Ctx(T0, pos: SpotWorld(20, 20)));
        Assert.False(status.Ready);
        Assert.True(status.WindowOpen);
        Assert.False(status.NearSpot);
        Assert.InRange(status.DistanceYalms!.Value, 500, 1000);
        Assert.StartsWith("Window open: ", status.Summary);
        Assert.EndsWith("yalms to the spot", status.Summary);
    }

    [Fact]
    public void OtherZoneSaysWhereToGo()
    {
        var status = ObjectiveEvaluator.Evaluate(Quest(), Ctx(T0, territory: 128, pos: Vector3.Zero));
        Assert.False(status.Ready);
        Assert.False(status.InZone);
        Assert.False(status.NearSpot);
        Assert.Null(status.DistanceYalms);
        Assert.Equal("Window open: travel to Eastern La Noscea", status.Summary);
    }

    [Fact]
    public void ClosedWindowReportsNextWindow()
    {
        var now = FirstUnixWhere(t => MinuteAt(t) == 12 * 60);
        var status = ObjectiveEvaluator.Evaluate(Quest("21:00-03:00"), Ctx(now, pos: SpotWorld(31, 30)));
        Assert.False(status.Ready);
        Assert.False(status.TimeOk);
        Assert.NotNull(status.NextWindowStart);
        // Nine Eorzea hours = 26 min 15 s.
        Assert.InRange((status.NextWindowStart!.Value - now).TotalSeconds, (9 * 175) - 60, 9 * 175);
        Assert.Equal("Next window in 27 min", status.Summary);
    }

    [Fact]
    public void LiveWeatherDecidesInZone()
    {
        var q = Quest(weather: ["Rain"]);
        Assert.True(ObjectiveEvaluator.Evaluate(q, Ctx(T0, pos: SpotWorld(31, 30), weather: "rain")).Ready);
        var dry = ObjectiveEvaluator.Evaluate(q, Ctx(T0, pos: SpotWorld(31, 30), weather: "Clear Skies"));
        Assert.False(dry.Ready);
        Assert.False(dry.WeatherOk);
        Assert.Equal("Clear Skies", dry.CurrentWeather);
        Assert.NotNull(dry.NextWindowStart);
        Assert.True(dry.NextWindowStart > T0);
    }

    [Fact]
    public void ForecastDecidesOutsideTheZone()
    {
        var q = Quest(weather: ["Rain", "Showers"]);
        var rainy = FirstUnixWhere(t => Table.WeatherAt(t.ToUnixTimeSeconds())!.Name is "Rain" or "Showers");
        var status = ObjectiveEvaluator.Evaluate(q, Ctx(rainy, territory: 0));
        Assert.True(status.WeatherOk);
        Assert.True(status.WindowOpen);
        Assert.False(status.Ready);
        Assert.Equal("Window open: travel to Eastern La Noscea", status.Summary);
    }

    [Fact]
    public void ImpossibleWeatherIsAProblem()
    {
        var status = ObjectiveEvaluator.Evaluate(Quest(weather: ["Blizzards"]), Ctx(T0, pos: SpotWorld(31, 30)));
        Assert.False(status.Ready);
        Assert.StartsWith("Eastern La Noscea never has Blizzards", status.Problem);
        Assert.Equal(status.Problem, status.Summary);
    }

    [Fact]
    public void NoLocationMeansReadyAnywhereInTheWindow()
    {
        var q = Quest(territory: null, x: null, y: null);
        var status = ObjectiveEvaluator.Evaluate(q, Ctx(T0, territory: 0));
        Assert.True(status.Ready);
        Assert.Null(status.InZone);
        Assert.Null(status.NearSpot);
        Assert.Equal("Ready now", status.Summary);
    }

    [Fact]
    public void CompletedIsNeverReady()
    {
        var status = ObjectiveEvaluator.Evaluate(Quest() with { Completed = true }, Ctx(T0, pos: SpotWorld(31, 30)));
        Assert.False(status.Ready);
        Assert.Equal("Complete", status.Summary);
    }

    [Theory]
    [InlineData(10, "under a minute")]
    [InlineData(60, "1 min")]
    [InlineData(61, "2 min")]
    [InlineData(3600, "1 h 00 min")]
    [InlineData(3600 + 125, "1 h 03 min")]
    public void Durations(int seconds, string expected) =>
        Assert.Equal(expected, ObjectiveEvaluator.FormatDuration(TimeSpan.FromSeconds(seconds)));

    // ---- helpers ---------------------------------------------------------------------------

    private static long Et(int hour, int minute) => (1000L * 86400) + (hour * 3600) + (minute * 60);

    private static int MinuteAt(DateTimeOffset t) => EorzeaTimeWindow.MinuteOfDay(GameMath.ToEorzeaSeconds(t));

    private static DateTimeOffset FirstUnixWhere(Func<DateTimeOffset, bool> predicate)
    {
        for (var t = T0; t < T0.AddDays(2); t = t.AddSeconds(1))
        {
            if (predicate(t))
                return t;
        }

        throw new InvalidOperationException("no such time");
    }

    private static bool Matches(EorzeaTimeWindow window, string[] weather, long unix) =>
        window.Contains(EorzeaTimeWindow.MinuteOfDay(GameMath.ToEorzeaSeconds(unix)))
        && (weather.Length == 0 || weather.Any(Table.WeatherAt(unix)!.Matches));
}

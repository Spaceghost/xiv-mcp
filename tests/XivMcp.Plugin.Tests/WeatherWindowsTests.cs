using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;

namespace XivMcp.Plugin.Tests;

/// <summary>find_weather_windows' search, checked window by window against GameMath's weather roll.</summary>
public class WeatherWindowsTests
{
    // CA1861: array arguments in assertions are hoisted so they are allocated once.
    private static readonly int[] WindowStartHours = [0, 8, 16];
    private static readonly int[] FirstFourWindowHours = [0, 8, 16, 0];

    private const long Window = GameMath.SecondsPerWeatherWindow;
    private const long After = 1_700_000_123L;
    private const uint Clear = 1;
    private const uint Rain = 7;
    private const uint Fog = 4;

    private static readonly byte[] Rates = [40, 35, 25, 0, 0, 0, 0, 0];
    private static readonly uint[] Ids = [Clear, Rain, Fog, 0, 0, 0, 0, 0];

    private static uint Expected(long unix)
    {
        var target = GameMath.WeatherTarget(unix);
        return target < 40 ? Clear : target < 75 ? Rain : Fog;
    }

    [Fact]
    public void WeatherAtFollowsTheRollAndWindowHoursAreZeroEightSixteen()
    {
        for (var u = GameMath.WeatherWindowStart(After); u < After + (Window * 300); u += Window)
        {
            Assert.Equal(Expected(u), WeatherWindows.WeatherAt(Rates, Ids, u));
            Assert.Equal(Expected(u), WeatherWindows.WeatherAt(Rates, Ids, u + Window - 1));
            Assert.Contains(WeatherWindows.WindowStartHour(u), WindowStartHours);
        }

        // Unix 0 is Eorzea midnight, so consecutive windows start at 0, 8, 16, 0.
        Assert.Equal(FirstFourWindowHours, new[] { 0L, Window, 2 * Window, 3 * Window }.Select(WeatherWindows.WindowStartHour).ToArray());
        Assert.Equal(0u, WeatherWindows.WeatherAt(new byte[8], Ids, After));
    }

    [Fact]
    public void FindsExactlyTheWantedWindowsMergedWhenConsecutive()
    {
        var horizon = Window * 200;
        var hits = WeatherWindows.Find(Rates, Ids, id => id == Rain, null, null, null, After, 500, horizon);
        Assert.NotEmpty(hits);

        var covered = new HashSet<long>();
        foreach (var hit in hits)
        {
            Assert.Equal(Rain, hit.WeatherId);
            Assert.Equal(0, hit.StartUnix % Window);
            Assert.Equal(hit.Windows * Window, hit.EndUnix - hit.StartUnix);
            Assert.Equal(Expected(hit.StartUnix - Window), hit.PreviousWeatherId);
            if (hit.EndUnix < After + horizon) Assert.NotEqual(Rain, Expected(hit.EndUnix));
            for (var u = hit.StartUnix; u < hit.EndUnix; u += Window) Assert.True(covered.Add(u));
        }

        for (var u = GameMath.WeatherWindowStart(After); u < After + horizon; u += Window)
        {
            Assert.Equal(Expected(u) == Rain, covered.Contains(u));
        }

        // Merged spans never touch each other.
        for (var i = 1; i < hits.Count; i++) Assert.True(hits[i].StartUnix > hits[i - 1].EndUnix);
    }

    [Fact]
    public void TheWindowAlreadyRunningCountsAndCountIsRespected()
    {
        var first = GameMath.WeatherWindowStart(After);
        var running = Expected(first);
        var hits = WeatherWindows.Find(Rates, Ids, id => id == running, null, null, null, After, 3, Window * 500);
        Assert.Equal(3, hits.Count);
        Assert.Equal(first, hits[0].StartUnix);
        Assert.True(hits[0].StartUnix <= After && hits[0].EndUnix > After);
    }

    [Fact]
    public void PreviousWeatherRequirementIsHonoured()
    {
        var hits = WeatherWindows.Find(Rates, Ids, id => id == Rain, id => id == Fog, null, null, After, 20, Window * 2000);
        Assert.Equal(20, hits.Count);
        foreach (var hit in hits)
        {
            Assert.Equal(Fog, hit.PreviousWeatherId);
            Assert.Equal(Fog, Expected(hit.StartUnix - Window));
            Assert.Equal(Rain, Expected(hit.StartUnix));
        }

        // Every qualifying window before the last hit was reported (none skipped).
        var starts = hits.Select(h => h.StartUnix).ToHashSet();
        for (var u = GameMath.WeatherWindowStart(After); u < hits[^1].StartUnix; u += Window)
        {
            if (Expected(u) == Rain && Expected(u - Window) == Fog) Assert.Contains(u, starts);
        }
    }

    [Theory]
    [InlineData(null, null, "0-24")]
    [InlineData(5, 5, "0-24")]
    [InlineData(0, 24, "0-24")]
    [InlineData(9, 17, "9-17")]
    [InlineData(22, 4, "0-4,22-24")]
    [InlineData(18, 0, "18-24")]
    [InlineData(18, 24, "18-24")]
    public void HourSegmentsSplitAtMidnight(int? start, int? end, string expected) =>
        Assert.Equal(expected, string.Join(",", WeatherWindows.HourSegments(start, end).Select(s => $"{s.Start}-{s.End}")));

    [Fact]
    public void HourRangeTrimsEachWindowToTheHoursThatCount()
    {
        byte[] always = [100, 0, 0, 0, 0, 0, 0, 0];
        var start = 3 * GameMath.SecondsPerSun; // an Eorzea midnight
        var hits = WeatherWindows.Find(always, Ids, id => id == Clear, null, 9, 17, start, 2, GameMath.SecondsPerSun * 3);
        Assert.Equal(2, hits.Count);

        // 09:00-17:00 straddles the 08-16 and 16-24 windows: one merged span of 8 bells per sun.
        Assert.Equal(start + (9 * GameMath.SecondsPerBell), hits[0].StartUnix);
        Assert.Equal(start + (17 * GameMath.SecondsPerBell), hits[0].EndUnix);
        Assert.Equal(hits[0].StartUnix + GameMath.SecondsPerSun, hits[1].StartUnix);
        Assert.Equal(9, GameMath.ToEorzeaDate(GameMath.ToEorzeaSeconds(hits[0].StartUnix)).Hour);
        Assert.Equal(17, GameMath.ToEorzeaDate(GameMath.ToEorzeaSeconds(hits[0].EndUnix)).Hour);
    }

    [Fact]
    public void WrappingHourRangeMergesAcrossMidnight()
    {
        byte[] always = [100, 0, 0, 0, 0, 0, 0, 0];
        var noon = (5 * GameMath.SecondsPerSun) + (12 * GameMath.SecondsPerBell);
        var hits = WeatherWindows.Find(always, Ids, id => id == Clear, null, 22, 4, noon, 2, GameMath.SecondsPerSun * 4);
        Assert.Equal(2, hits.Count);
        Assert.Equal((5 * GameMath.SecondsPerSun) + (22 * GameMath.SecondsPerBell), hits[0].StartUnix);
        Assert.Equal(6 * GameMath.SecondsPerBell, hits[0].EndUnix - hits[0].StartUnix);
    }

    [Fact]
    public void NothingBeyondTheHorizonAndNothingForAnImpossibleWeather()
    {
        Assert.Empty(WeatherWindows.Find(Rates, Ids, id => id == 99, null, null, null, After, 5, Window * 1000));
        var hits = WeatherWindows.Find(Rates, Ids, id => id == Fog, null, null, null, After, 500, Window * 10);
        Assert.All(hits, h => Assert.True(h.StartUnix < After + (Window * 10)));
    }
}

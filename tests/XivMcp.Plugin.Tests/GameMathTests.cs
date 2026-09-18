using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using XivMcp.Plugin.Util;

namespace XivMcp.Plugin.Tests;

public class GameMathTests
{
    // Dalamud's MapLinkPayload converters (private), called on an uninitialized instance: they only use their arguments.
    private static readonly object Payload = RuntimeHelpers.GetUninitializedObject(typeof(MapLinkPayload));
    private static readonly MethodInfo RawToMap = typeof(MapLinkPayload).GetMethod("ConvertRawPositionToMapCoordinate", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo MapToRaw = typeof(MapLinkPayload).GetMethod("ConvertMapCoordinateToRawPosition", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void MapCoordinatesMatchDalamudMapLinkPayload()
    {
        Assert.NotNull(RawToMap);
        Assert.NotNull(MapToRaw);
        var rng = new Random(42);
        foreach (var sizeFactor in new ushort[] { 95, 100, 200, 400, 800 })
        {
            foreach (var offset in new short[] { 0, -100, 250, -1024, 1023 })
            {
                for (var i = 0; i < 300; i++)
                {
                    var world = (float)((rng.NextDouble() * 2000) - 1000);
                    var raw = (int)(world * 1000);
                    var theirs = (float)RawToMap.Invoke(Payload, [raw, (float)sizeFactor, offset])!;
                    Assert.InRange(GameMath.WorldToMapCoordinate(raw / 1000f, sizeFactor, offset) - theirs, -1e-3f, 1e-3f);

                    var map = (float)((rng.NextDouble() * 40) + 1);
                    var rawTheirs = (int)MapToRaw.Invoke(Payload, [map, (float)sizeFactor, offset])!;
                    var mine = GameMath.MapToWorldCoordinate(map, sizeFactor, offset);
                    Assert.InRange((mine * 1000) - rawTheirs, -2f, 2f); // Dalamud truncates to integer 1/1000 units
                    Assert.InRange(GameMath.WorldToMapCoordinate(mine, sizeFactor, offset) - map, -1e-3f, 1e-3f);
                }
            }
        }

        Assert.Equal(11.25f, GameMath.WorldToMapCoordinate(0, 200, 0), 3);
        Assert.Equal(21.5f, GameMath.WorldToMapCoordinate(0, 100, 0), 3);
        Assert.Equal(GameMath.WorldToMapCoordinate(12, 100, 0), GameMath.WorldToMapCoordinate(12, 0, 0)); // 0 = default 100
    }

    [Theory]
    [InlineData(0f, "S")]
    [InlineData(MathF.PI / 2, "E")]
    [InlineData(-MathF.PI / 2, "W")]
    [InlineData(MathF.PI, "N")]
    public void RotationHeadings(float rotation, string compass) =>
        Assert.Equal(compass, GameMath.HeadingToCompass(GameMath.RotationToHeadingDegrees(rotation)));

    [Fact]
    public void BearingNorthIsNegativeZ() =>
        Assert.Equal("N", GameMath.HeadingToCompass(GameMath.BearingDegrees(Vector3.Zero, new Vector3(0, 0, -10))));

    [Fact]
    public void EorzeaTimeAndWeatherWindows()
    {
        Assert.Equal(3600, GameMath.ToEorzeaSeconds(175));
        Assert.Equal(new GameMath.EorzeaDate(1, 1, 1, 0, 0, 0), GameMath.ToEorzeaDate(0));
        Assert.Equal("1st Astral Moon", GameMath.EorzeaMonthName(1));
        Assert.Equal("3rd Umbral Moon", GameMath.EorzeaMonthName(6));

        for (var u = 1_700_000_000L - (1_700_000_000L % 1400); u < 1_700_000_000L + (1400 * 200); u += 1400)
        {
            var date = GameMath.ToEorzeaDate(GameMath.ToEorzeaSeconds(u));
            Assert.True(date.Hour % 8 == 0 && date.Minute == 0 && date.Second == 0, $"{u} -> {date}");
            Assert.Equal(GameMath.WeatherTarget(u), GameMath.WeatherTarget(u + 1399));
            Assert.Equal(u, GameMath.FromEorzeaSeconds(GameMath.ToEorzeaSeconds(u)).ToUnixTimeSeconds());
        }

        byte[] rates = [20, 30, 50, 0, 0, 0, 0, 0];
        Assert.Equal(0, GameMath.PickWeatherIndex(rates, 19));
        Assert.Equal(1, GameMath.PickWeatherIndex(rates, 20));
        Assert.Equal(2, GameMath.PickWeatherIndex(rates, 99));
    }

    [Fact]
    public void WeatherTargetMatchesTheCommunityReferenceAlgorithm()
    {
        // FFXIVWeather's CalculateTarget, transcribed.
        static int Reference(long unix)
        {
            var bell = unix / 175;
            var increment = (uint)(bell + 8 - (bell % 8)) % 24;
            var totalDays = (uint)(unix / 4200);
            var calcBase = (totalDays * 0x64) + increment;
            var step1 = (calcBase << 0xB) ^ calcBase;
            var step2 = (step1 >> 8) ^ step1;
            return (int)(step2 % 0x64);
        }

        var rng = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var unix = 1_600_000_000L + rng.Next(0, 300_000_000);
            Assert.Equal(Reference(unix), GameMath.WeatherTarget(unix));
        }
    }

    [Fact]
    public void ResetTimes()
    {
        var wednesday = new DateTimeOffset(2026, 9, 16, 21, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero), GameMath.NextWeeklyUtc(wednesday, DayOfWeek.Tuesday, 8));
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), GameMath.NextWeeklyUtc(new DateTimeOffset(2026, 9, 15, 7, 0, 0, TimeSpan.Zero), DayOfWeek.Tuesday, 8));
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 15, 0, 0, TimeSpan.Zero), GameMath.NextDailyUtc(wednesday, 15));
    }
}

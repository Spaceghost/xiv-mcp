using XivMcp.Plugin.Util;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Pure search over the deterministic Eorzea weather sequence: which upcoming real-time spans have a wanted weather,
/// optionally right after a given previous weather and only during given Eorzea hours. Uses <see cref="GameMath"/> for
/// the roll, so it agrees with get_weather_forecast. No game data: rates and weather ids are passed in.
/// </summary>
internal static class WeatherWindows
{
    /// <summary>A real-time span (Unix seconds, end exclusive) with the wanted weather.</summary>
    /// <param name="StartUnix">When the span starts (the weather window start, or later when an hour range cuts it).</param>
    /// <param name="EndUnix">When it ends.</param>
    /// <param name="WeatherId">Weather during the span.</param>
    /// <param name="PreviousWeatherId">Weather of the 8-bell window before the span's first window.</param>
    /// <param name="Windows">How many consecutive 8-bell windows the span covers (in part or whole).</param>
    internal readonly record struct Hit(long StartUnix, long EndUnix, uint WeatherId, uint PreviousWeatherId, int Windows);

    /// <summary>Weather id for the 8-bell window containing <paramref name="unixSeconds"/>; 0 when the table has no entry for the roll.</summary>
    public static uint WeatherAt(ReadOnlySpan<byte> rates, ReadOnlySpan<uint> weatherIds, long unixSeconds)
    {
        var picked = GameMath.PickWeatherIndex(rates, GameMath.WeatherTarget(unixSeconds));
        return picked >= 0 && picked < weatherIds.Length ? weatherIds[picked] : 0;
    }

    /// <summary>Eorzea hour (0, 8 or 16) at which the weather window containing <paramref name="unixSeconds"/> starts.</summary>
    public static int WindowStartHour(long unixSeconds) =>
        (int)(GameMath.WeatherWindowStart(unixSeconds) / GameMath.SecondsPerBell % 24);

    /// <summary>
    /// [start, end) Eorzea-hour ranges, split at midnight when the range wraps (22 → 4 becomes 0-4 and 22-24). start == end,
    /// or a null bound, means the whole day.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> HourSegments(int? hourStart, int? hourEnd)
    {
        if (hourStart is not { } s || hourEnd is not { } e) return [(0, 24)];
        s = ((s % 24) + 24) % 24;
        e = e == 24 ? 24 : ((e % 24) + 24) % 24;
        if (s == e || (s == 0 && e == 24)) return [(0, 24)];
        if (s < e) return [(s, e)];
        return e == 0 ? [(s, 24)] : [(0, e), (s, 24)];
    }

    /// <summary>
    /// The next <paramref name="count"/> spans after <paramref name="afterUnix"/> (a span already running counts), looking
    /// no further than <paramref name="horizonSeconds"/> ahead. Adjacent spans are merged into one.
    /// </summary>
    public static List<Hit> Find(
        ReadOnlySpan<byte> rates,
        ReadOnlySpan<uint> weatherIds,
        Func<uint, bool> wanted,
        Func<uint, bool>? previousWanted,
        int? hourStart,
        int? hourEnd,
        long afterUnix,
        int count,
        long horizonSeconds)
    {
        var hits = new List<Hit>();
        var segments = HourSegments(hourStart, hourEnd);
        var first = GameMath.WeatherWindowStart(afterUnix);
        var limit = afterUnix + Math.Max(0, horizonSeconds);
        var previous = WeatherAt(rates, weatherIds, first - GameMath.SecondsPerWeatherWindow);
        for (var window = first; window < limit; window += GameMath.SecondsPerWeatherWindow)
        {
            var weather = WeatherAt(rates, weatherIds, window);
            var before = previous;
            previous = weather;
            if (weather == 0 || !wanted(weather) || (previousWanted != null && !previousWanted(before))) continue;

            var windowHour = WindowStartHour(window);
            foreach (var (segStart, segEnd) in segments)
            {
                var from = Math.Max(segStart, windowHour);
                var to = Math.Min(segEnd, windowHour + 8);
                if (from >= to) continue;
                var start = window + ((long)(from - windowHour) * GameMath.SecondsPerBell);
                var end = window + ((long)(to - windowHour) * GameMath.SecondsPerBell);
                if (end <= afterUnix || start >= limit) continue;

                if (hits.Count > 0 && hits[^1].EndUnix == start && hits[^1].WeatherId == weather)
                {
                    var last = hits[^1];
                    hits[^1] = last with { EndUnix = end, Windows = last.Windows + (start == window ? 1 : 0) };
                    continue;
                }

                if (hits.Count >= count) return hits;
                hits.Add(new Hit(start, end, weather, before, 1));
            }
        }

        return hits;
    }
}

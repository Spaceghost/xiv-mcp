using System.Numerics;
using XivMcp.Plugin.Util;

namespace XivMcp.Plugin.Objectives;

/// <summary>One weather a zone can have: id, chance (WeatherRate slot) and its names (client language first, then aliases).</summary>
public sealed record WeatherSlot(uint Id, byte Rate, IReadOnlyList<string> Names)
{
    public string Name => Names.Count > 0 ? Names[0] : $"#{Id}";

    public bool Matches(string name) => Names.Any(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A zone's WeatherRate row with names, driving the game's deterministic forecast (see <see cref="GameMath.WeatherTarget"/>).</summary>
public sealed class WeatherTable
{
    private readonly WeatherSlot[] slots;
    private readonly byte[] rates;

    public WeatherTable(IEnumerable<WeatherSlot> slots)
    {
        this.slots = slots.Take(8).ToArray();
        rates = this.slots.Select(s => s.Rate).ToArray();
    }

    public IReadOnlyList<WeatherSlot> Slots => slots;

    /// <summary>The weather for the 8-bell window containing <paramref name="unixSeconds"/>, or null for an empty table.</summary>
    public WeatherSlot? WeatherAt(long unixSeconds)
    {
        var index = GameMath.PickWeatherIndex(rates, GameMath.WeatherTarget(GameMath.WeatherWindowStart(unixSeconds)));
        return index >= 0 && slots[index].Id != 0 ? slots[index] : null;
    }

    /// <summary>Whether any wanted name is a weather this zone can have at all.</summary>
    public bool CanHaveAny(IReadOnlyCollection<string> wanted) =>
        wanted.Any(w => slots.Any(s => s.Rate > 0 && s.Matches(w)));
}

/// <summary>What conditions need from the zone an objective points at.</summary>
public sealed record ZoneInfo(uint TerritoryId, string? Name, ushort SizeFactor, short OffsetX, short OffsetY, WeatherTable? Weather)
{
    public Vector2 MapToWorld(float mapX, float mapY) => new(
        GameMath.MapToWorldCoordinate(mapX, SizeFactor, OffsetX),
        GameMath.MapToWorldCoordinate(mapY, SizeFactor, OffsetY));
}

/// <summary>
/// A snapshot of the game for condition evaluation, taken on the framework thread (or built by tests).
/// <paramref name="TerritoryId"/> is 0 when not logged in; <paramref name="PlayerPosition"/> is world X/Y/Z.
/// </summary>
public sealed record ObjectiveContext(
    DateTimeOffset Now,
    uint TerritoryId,
    Vector3? PlayerPosition,
    IReadOnlyList<string>? CurrentWeatherNames,
    Func<uint, ZoneInfo?> Zones);

/// <summary>Live state of one objective's conditions. Null booleans mean "not constrained" or "unknown".</summary>
public sealed record ObjectiveStatus(
    bool Ready,
    bool WindowOpen,
    bool? InZone,
    bool? NearSpot,
    double? DistanceYalms,
    bool? TimeOk,
    bool? WeatherOk,
    string? CurrentWeather,
    DateTimeOffset? NextWindowStart,
    DateTimeOffset? NextWindowEnd,
    string? Problem,
    string Summary);

/// <summary>Pure condition logic for objectives: zone, spot radius, Eorzea time window, weather and the next window.</summary>
public static class ObjectiveEvaluator
{
    /// <summary>How far ahead the next-window search looks (real time).</summary>
    public static readonly TimeSpan SearchHorizon = TimeSpan.FromDays(7);

    public static ObjectiveStatus Evaluate(Objective objective, ObjectiveContext ctx)
    {
        var conditions = objective.Conditions;
        var zone = objective.TerritoryId is { } tid and > 0 ? ctx.Zones(tid) : null;
        var zoneLabel = zone?.Name ?? objective.Zone ?? (objective.TerritoryId is { } t ? $"territory {t}" : null);
        var window = conditions.TimeWindow;
        var wanted = conditions.WeatherConstraint;
        var now = ctx.Now;
        var unix = now.ToUnixTimeSeconds();

        string? problem = null;
        if (!EorzeaTimeWindow.TryParse(conditions.EorzeaTime, out _, out var timeError))
            problem = timeError;
        else if (wanted.Count > 0 && zone?.Weather is { } table && !table.CanHaveAny(wanted))
            problem = $"{zoneLabel} never has {string.Join(" or ", wanted)} (possible: {string.Join(", ", table.Slots.Where(s => s.Rate > 0).Select(s => s.Name).Distinct())}).";
        else if (wanted.Count > 0 && objective.TerritoryId is > 0 && zone is not null && zone.Weather is null)
            problem = $"{zoneLabel} has fixed or scripted weather; the weather condition cannot be forecast.";

        bool? inZone = objective.TerritoryId is > 0 ? ctx.TerritoryId != 0 && ctx.TerritoryId == objective.TerritoryId : null;

        bool? near = null;
        double? distance = null;
        if (objective.HasSpot)
        {
            if (inZone == true && zone is not null && ctx.PlayerPosition is { } player)
            {
                var spot = zone.MapToWorld(objective.MapX!.Value, objective.MapY!.Value);
                distance = GameMath.Round(Vector2.Distance(new Vector2(player.X, player.Z), spot), 1);
                near = distance <= Math.Max(1f, conditions.Radius);
            }
            else
            {
                near = inZone == true ? null : false;
            }
        }

        bool? timeOk = window.IsAny ? null : window.Contains(EorzeaTimeWindow.MinuteOfDay(GameMath.ToEorzeaSeconds(now)));

        bool? weatherOk = null;
        string? currentWeather = null;
        if (inZone == true && ctx.CurrentWeatherNames is { Count: > 0 } names)
            currentWeather = names[0];
        else if (zone?.Weather?.WeatherAt(unix) is { } forecast)
            currentWeather = forecast.Name;

        if (wanted.Count > 0)
        {
            if (inZone == true && ctx.CurrentWeatherNames is { Count: > 0 } live)
                weatherOk = wanted.Any(w => live.Any(n => string.Equals(n, w, StringComparison.OrdinalIgnoreCase)));
            else if (zone?.Weather?.WeatherAt(unix) is { } slot)
                weatherOk = wanted.Any(slot.Matches);
            else
                weatherOk = false;
        }

        var windowOpen = timeOk != false && weatherOk != false;
        var ready = !objective.Completed && problem is null && windowOpen && inZone != false && near != false;

        DateTimeOffset? nextStart = null;
        DateTimeOffset? nextEnd = null;
        if (problem is null && (!window.IsAny || wanted.Count > 0))
        {
            var weatherTable = wanted.Count > 0 ? zone?.Weather : null;
            if (wanted.Count == 0 || weatherTable is not null)
            {
                // While the window is open, start from now to learn when it closes; the live weather can differ from the
                // forecast (scripted events), so an open window is reported from the live checks above.
                if (FindNextWindow(window, wanted, weatherTable, now, SearchHorizon) is { } found)
                {
                    nextStart = windowOpen ? now : found.Start;
                    nextEnd = windowOpen && found.Start > now ? null : found.End;
                    if (!windowOpen && found.Start <= now)
                    {
                        // The forecast says open but the live weather disagrees: look after the current weather window.
                        var after = DateTimeOffset.FromUnixTimeSeconds(GameMath.WeatherWindowStart(unix) + GameMath.SecondsPerWeatherWindow);
                        var later = FindNextWindow(window, wanted, weatherTable, after, SearchHorizon);
                        nextStart = later?.Start;
                        nextEnd = later?.End;
                    }
                }
            }
        }

        var summary = Summarize(objective, ready, windowOpen, inZone, near, distance, zoneLabel, problem, now, nextStart, nextEnd, weatherOk, wanted);
        return new ObjectiveStatus(ready, windowOpen, inZone, near, distance, timeOk, weatherOk, currentWeather, nextStart, nextEnd, problem, summary);
    }

    private static string Summarize(
        Objective objective, bool ready, bool windowOpen, bool? inZone, bool? near, double? distance, string? zoneLabel, string? problem,
        DateTimeOffset now, DateTimeOffset? nextStart, DateTimeOffset? nextEnd, bool? weatherOk, IReadOnlyList<string> wanted)
    {
        if (objective.Completed)
            return "Complete";
        if (problem is not null)
            return problem;
        if (ready)
            return nextEnd is { } end && end > now ? $"Ready now ({FormatDuration(end - now)} left)" : "Ready now";
        if (windowOpen)
        {
            if (inZone == false)
                return $"Window open: travel to {zoneLabel}";
            if (near == false && distance is { } d)
                return $"Window open: {d:0} yalms to the spot";
            return "Window open";
        }

        if (nextStart is { } start)
            return start <= now ? "Window opening" : $"Next window in {FormatDuration(start - now)}";
        if (wanted.Count > 0 && weatherOk == false)
            return $"Waiting for {string.Join(" or ", wanted)}";
        return "Waiting";
    }

    /// <summary>"under a minute", "14 min", "2 h 05 min".</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return "under a minute";
        var minutes = (long)Math.Ceiling(span.TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    /// <summary>
    /// The earliest real-time interval at or after <paramref name="from"/> in which the Eorzea time window is open and
    /// (when <paramref name="weather"/> is non-empty) the forecast weather of <paramref name="table"/> is one of them.
    /// End is exclusive and null when the interval never closes (no constraint at all). Returns null when nothing
    /// matches within <paramref name="horizon"/>, or when weather is constrained without a table.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset? End)? FindNextWindow(
        EorzeaTimeWindow time, IReadOnlyList<string> weather, WeatherTable? table, DateTimeOffset from, TimeSpan horizon)
    {
        var start = from.ToUnixTimeSeconds();
        var limit = start + (long)horizon.TotalSeconds;

        if (weather.Count == 0)
        {
            if (time.IsAny)
                return (from, null);
            return FirstOpenIn(time, start, limit) is { } hit ? (DateTimeOffset.FromUnixTimeSeconds(hit.Start), DateTimeOffset.FromUnixTimeSeconds(hit.End)) : null;
        }

        if (table is null)
            return null;

        // Walk 8-bell weather windows, merging consecutive matching ones into segments.
        var w = GameMath.WeatherWindowStart(start);
        while (w < limit)
        {
            if (!(table.WeatherAt(w) is { } slot && weather.Any(slot.Matches)))
            {
                w += GameMath.SecondsPerWeatherWindow;
                continue;
            }

            var segmentStart = Math.Max(w, start);
            var segmentEnd = w + GameMath.SecondsPerWeatherWindow;
            while (segmentEnd < limit + GameMath.SecondsPerWeatherWindow && table.WeatherAt(segmentEnd) is { } next && weather.Any(next.Matches))
                segmentEnd += GameMath.SecondsPerWeatherWindow;

            if (FirstOpenIn(time, segmentStart, segmentEnd) is { } hit)
                return (DateTimeOffset.FromUnixTimeSeconds(hit.Start), DateTimeOffset.FromUnixTimeSeconds(hit.End));
            w = segmentEnd;
        }

        return null;
    }

    /// <summary>First real-time interval inside [a, b) where the Eorzea time window is open, clipped to b.</summary>
    private static (long Start, long End)? FirstOpenIn(EorzeaTimeWindow time, long a, long b)
    {
        if (time.IsAny)
            return a < b ? (a, b) : null;

        var t = a;
        var untilOpen = time.SecondsUntilOpen(GameMath.ToEorzeaSeconds(t));
        if (untilOpen > 0)
        {
            t += RealSeconds(untilOpen);
            // Rounding between the clocks can land a real second either side of the opening minute.
            while (t - 1 >= a && time.Contains(EorzeaTimeWindow.MinuteOfDay(GameMath.ToEorzeaSeconds(t - 1))))
                t--;
            while (t < b && !time.Contains(EorzeaTimeWindow.MinuteOfDay(GameMath.ToEorzeaSeconds(t))))
                t++;
        }

        if (t >= b)
            return null;
        var untilClose = time.SecondsUntilClose(GameMath.ToEorzeaSeconds(t)) ?? 0;
        var end = Math.Min(b, t + RealSeconds(untilClose));
        while (end - 1 > t && !time.Contains(EorzeaTimeWindow.MinuteOfDay(GameMath.ToEorzeaSeconds(end - 1))))
            end--;
        while (end < b && time.Contains(EorzeaTimeWindow.MinuteOfDay(GameMath.ToEorzeaSeconds(end))))
            end++;
        return (t, end);
    }

    /// <summary>Eorzea seconds → real seconds, rounded up (one real second is 144/7 Eorzea seconds).</summary>
    private static long RealSeconds(long eorzeaSeconds) => ((eorzeaSeconds * 7) + 143) / 144;
}

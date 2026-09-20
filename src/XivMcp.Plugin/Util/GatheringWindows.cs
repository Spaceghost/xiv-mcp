namespace XivMcp.Plugin.Util;

/// <summary>
/// Pure maths for timed gathering nodes. The game stores node windows as Eorzea clock times packed as
/// <c>HHMM</c> (so 0400 is 4 bells, 1730 is 17:30) and durations in the same packed form. Unlimited/absent
/// values are <see cref="NoTime"/>. Nothing here touches game memory; host-testable.
/// </summary>
public static class GatheringWindows
{
    /// <summary>The value the sheets use for "no window".</summary>
    public const ushort NoTime = 65535;

    private const int EorzeaSecondsPerDay = 86400;

    /// <summary>Packed <c>HHMM</c> → seconds since Eorzea midnight, or null when the value is absent/invalid.</summary>
    public static int? PackedToSeconds(int packed)
    {
        if (packed is < 0 or >= NoTime)
        {
            return null;
        }

        var hours = packed / 100;
        var minutes = packed % 100;
        if (minutes >= 60)
        {
            return null;
        }

        var seconds = (hours * 3600) + (minutes * 60);
        return seconds is >= 0 and <= EorzeaSecondsPerDay ? seconds : null;
    }

    /// <summary>Packed <c>HHMM</c> → "HH:MM" for display, or null.</summary>
    public static string? PackedToClock(int packed) =>
        PackedToSeconds(packed) is { } seconds ? $"{seconds / 3600 % 24:00}:{seconds / 60 % 60:00}" : null;

    /// <summary>One occurrence of a node window on the Eorzea clock, in Eorzea seconds since the epoch.</summary>
    /// <param name="StartEorzeaSeconds">When the window opens.</param>
    /// <param name="EndEorzeaSeconds">When it closes (exclusive).</param>
    /// <param name="OpenNow">True when <c>now</c> was inside the window.</param>
    public readonly record struct Window(long StartEorzeaSeconds, long EndEorzeaSeconds, bool OpenNow);

    /// <summary>
    /// The window containing <paramref name="nowEorzeaSeconds"/>, or the next one after it, for a node that
    /// opens every Eorzea day at <paramref name="startPacked"/> for <paramref name="durationPacked"/>.
    /// Returns null when either value is absent.
    /// </summary>
    public static Window? Next(long nowEorzeaSeconds, int startPacked, int durationPacked)
    {
        if (PackedToSeconds(startPacked) is not { } start)
        {
            return null;
        }

        var duration = PackedToSeconds(durationPacked) ?? 0;
        if (duration <= 0)
        {
            return null;
        }

        var dayStart = nowEorzeaSeconds - Mod(nowEorzeaSeconds, EorzeaSecondsPerDay);

        // Yesterday's window can still be running when it crosses midnight.
        for (var day = -1; day <= 1; day++)
        {
            var windowStart = dayStart + ((long)day * EorzeaSecondsPerDay) + start;
            var windowEnd = windowStart + duration;
            if (nowEorzeaSeconds < windowEnd)
            {
                return new Window(windowStart, windowEnd, nowEorzeaSeconds >= windowStart);
            }
        }

        return null;
    }

    /// <summary>
    /// The soonest window (open now, else next to open) across a node's whole time table. Entries are
    /// (startPacked, durationPacked) pairs; absent entries are ignored.
    /// </summary>
    public static Window? Soonest(long nowEorzeaSeconds, IEnumerable<(int Start, int Duration)> entries)
    {
        Window? best = null;
        foreach (var (start, duration) in entries)
        {
            if (Next(nowEorzeaSeconds, start, duration) is not { } candidate)
            {
                continue;
            }

            if (best is not { } current ||
                (candidate.OpenNow && !current.OpenNow) ||
                (candidate.OpenNow == current.OpenNow && candidate.StartEorzeaSeconds < current.StartEorzeaSeconds))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static long Mod(long value, long modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}

using System.Globalization;

namespace XivMcp.Plugin.Objectives;

/// <summary>
/// A daily Eorzea time window such as "21:00-03:00" (wraps past midnight) or "17:00-18:30". Minutes of the Eorzea
/// day, start inclusive, end exclusive. <see cref="Any"/> is the unconstrained window.
/// </summary>
public readonly record struct EorzeaTimeWindow(int StartMinute, int EndMinute)
{
    public const int MinutesPerDay = 24 * 60;

    public static EorzeaTimeWindow Any { get; } = new(0, MinutesPerDay);

    public bool IsAny => EndMinute - StartMinute >= MinutesPerDay || StartMinute == EndMinute;

    public bool WrapsMidnight => !IsAny && EndMinute < StartMinute;

    /// <summary>Whether the Eorzea minute of day (0..1439) is inside the window.</summary>
    public bool Contains(int minuteOfDay)
    {
        if (IsAny)
            return true;
        minuteOfDay = ((minuteOfDay % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
        return WrapsMidnight
            ? minuteOfDay >= StartMinute || minuteOfDay < EndMinute
            : minuteOfDay >= StartMinute && minuteOfDay < EndMinute;
    }

    /// <summary>
    /// Eorzea seconds from <paramref name="eorzeaSeconds"/> until the window next opens: 0 when it is open now.
    /// </summary>
    public long SecondsUntilOpen(long eorzeaSeconds)
    {
        if (IsAny || Contains(MinuteOfDay(eorzeaSeconds)))
            return 0;
        var secondOfDay = SecondOfDay(eorzeaSeconds);
        var start = (long)StartMinute * 60;
        var delta = start - secondOfDay;
        return delta > 0 ? delta : delta + (MinutesPerDay * 60L);
    }

    /// <summary>Eorzea seconds until the window closes, or null when it is closed now (or never closes).</summary>
    public long? SecondsUntilClose(long eorzeaSeconds)
    {
        if (IsAny || !Contains(MinuteOfDay(eorzeaSeconds)))
            return null;
        var secondOfDay = SecondOfDay(eorzeaSeconds);
        var end = (long)(EndMinute % MinutesPerDay) * 60;
        var delta = end - secondOfDay;
        return delta > 0 ? delta : delta + (MinutesPerDay * 60L);
    }

    public static int MinuteOfDay(long eorzeaSeconds) => (int)(SecondOfDay(eorzeaSeconds) / 60);

    private static long SecondOfDay(long eorzeaSeconds) => ((eorzeaSeconds % 86400) + 86400) % 86400;

    public override string ToString() => IsAny ? "any" : $"{Format(StartMinute)}-{Format(EndMinute)}";

    private static string Format(int minute) => $"{minute % MinutesPerDay / 60:00}:{minute % 60:00}";

    /// <summary>
    /// Parses "HH:MM-HH:MM" (also "H-H", "HH:MM–HH:MM" with an en dash, "24:00" as an end), "any", "*" or empty.
    /// A window whose start equals its end means the whole day.
    /// </summary>
    public static bool TryParse(string? text, out EorzeaTimeWindow window, out string? error)
    {
        window = Any;
        error = null;
        var value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("any", StringComparison.OrdinalIgnoreCase) || value == "*")
            return true;

        var parts = value.Replace('–', '-').Replace('—', '-').Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryParseClock(parts[0], out var start) || !TryParseClock(parts[1], out var end))
        {
            error = $"Eorzea time window '{value}' is not HH:MM-HH:MM (e.g. \"21:00-03:00\") or \"any\".";
            return false;
        }

        if (start == MinutesPerDay)
            start = 0;
        window = start == end % MinutesPerDay ? Any : new EorzeaTimeWindow(start, end == MinutesPerDay ? MinutesPerDay : end);
        return true;
    }

    public static EorzeaTimeWindow Parse(string? text) =>
        TryParse(text, out var window, out var error) ? window : throw new FormatException(error);

    private static bool TryParseClock(string text, out int minute)
    {
        minute = 0;
        var pieces = text.Split(':');
        if (pieces.Length is < 1 or > 2
            || !int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || hour is < 0 or > 24)
            return false;
        var minutes = 0;
        if (pieces.Length == 2 && (!int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes is < 0 or > 59))
            return false;
        if (hour == 24 && minutes != 0)
            return false;
        minute = (hour * 60) + minutes;
        return true;
    }
}

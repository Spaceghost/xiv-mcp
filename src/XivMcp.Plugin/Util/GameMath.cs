using System.Numerics;

namespace XivMcp.Plugin.Util;

/// <summary>
/// Pure math helpers: world → map coordinates, distances, headings, Eorzea time and the
/// Eorzea weather forecast algorithm. No game memory access; safe from any thread.
/// </summary>
public static class GameMath
{
    /// <summary>Real seconds per Eorzea bell (hour).</summary>
    public const int SecondsPerBell = 175;

    /// <summary>Real seconds per weather window (8 bells).</summary>
    public const int SecondsPerWeatherWindow = SecondsPerBell * 8;

    /// <summary>Real seconds per Eorzea sun (day).</summary>
    public const int SecondsPerSun = SecondsPerBell * 24;

    /// <summary>Eorzea time runs 3600/175 = 144/7 (~20.57) times faster than real time.</summary>
    public const double EorzeaMultiplier = 3600.0 / 175.0;

    private static readonly string[] Compass8 = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private static readonly string[] MoonPhases =
    [
        "New Moon", "Waxing Crescent", "First Quarter", "Waxing Gibbous",
        "Full Moon", "Waning Gibbous", "Last Quarter", "Waning Crescent",
    ];

    /// <summary>
    /// World axis value (X or Z) → in-game map coordinate. Same formula as Dalamud's
    /// MapLinkPayload: 41/s * (((raw + offset) * s + 1024) / 2048) + 1, s = SizeFactor/100.
    /// </summary>
    public static float WorldToMapCoordinate(float worldValue, ushort sizeFactor, short offset)
    {
        var scale = (sizeFactor == 0 ? 100 : sizeFactor) / 100f;
        return 41f / scale * ((((worldValue + offset) * scale) + 1024f) / 2048f) + 1f;
    }

    /// <summary>
    /// Inverse of <see cref="WorldToMapCoordinate"/>: in-game map coordinate → world axis value (X or Z). Same formula
    /// as Dalamud's MapLinkPayload.ConvertMapCoordinateToRawPosition, without its 1/1000 integer raw units.
    /// </summary>
    public static float MapToWorldCoordinate(float mapValue, ushort sizeFactor, short offset)
    {
        var scale = (sizeFactor == 0 ? 100 : sizeFactor) / 100f;
        return (((mapValue - 1f) * scale / 41f * 2048f) - 1024f) / scale - offset;
    }

    /// <summary>MapMarker texture-space position (0..2048) → map coordinate.</summary>
    public static float MarkerToMapCoordinate(float texturePosition, ushort sizeFactor)
    {
        var scale = (sizeFactor == 0 ? 100 : sizeFactor) / 100f;
        return 41f / scale * (texturePosition / 2048f) + 1f;
    }

    /// <summary>World Y (height) → map Z shown by the game, using TerritoryTypeTransient.OffsetZ.</summary>
    public static float WorldHeightToMapZ(float worldY, short offsetZ) =>
        (worldY - (offsetZ == -10000 ? 0 : offsetZ)) / 100f;

    /// <summary>Truncates to one decimal like the in-game coordinate display.</summary>
    public static double DisplayCoordinate(float value) =>
        float.IsFinite(value) ? Math.Truncate(value * 10.0) / 10.0 : 0;

    public static double Round(float value, int digits = 2) =>
        float.IsFinite(value) ? Math.Round(value, digits) : 0;

    public static double Round(double value, int digits = 2) =>
        double.IsFinite(value) ? Math.Round(value, digits) : 0;

    public static float Distance3D(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

    /// <summary>Distance on the ground plane (X/Z), ignoring height.</summary>
    public static float DistanceHorizontal(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// Game rotation (radians, 0 = facing south/+Z, +π/2 = east/+X, ±π = north) → compass heading
    /// in degrees clockwise from north (0 = N, 90 = E).
    /// </summary>
    public static double RotationToHeadingDegrees(float rotation)
    {
        if (!float.IsFinite(rotation))
        {
            return 0;
        }

        var degrees = 180.0 - (rotation * 180.0 / Math.PI);
        degrees %= 360.0;
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        return Math.Round(degrees, 1) % 360.0;
    }

    /// <summary>Heading in degrees → 8-point compass label.</summary>
    public static string HeadingToCompass(double headingDegrees)
    {
        var index = (int)Math.Round(headingDegrees / 45.0) % 8;
        return Compass8[index < 0 ? index + 8 : index];
    }

    /// <summary>Heading (degrees from north) of the vector from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double BearingDegrees(Vector3 from, Vector3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        if (dx == 0 && dz == 0)
        {
            return 0;
        }

        // North is -Z, east is +X.
        var degrees = Math.Atan2(dx, -dz) * 180.0 / Math.PI;
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        return Math.Round(degrees, 1) % 360.0;
    }

    // ---- Eorzea time -------------------------------------------------------------------------

    /// <summary>Eorzea seconds since the Eorzean epoch for a real Unix time.</summary>
    public static long ToEorzeaSeconds(long unixSeconds) => unixSeconds * 144 / 7;

    public static long ToEorzeaSeconds(DateTimeOffset utc) =>
        utc.ToUnixTimeMilliseconds() * 144 / 7000;

    /// <summary>Real UTC instant at which the Eorzea clock reads <paramref name="eorzeaSeconds"/>.</summary>
    public static DateTimeOffset FromEorzeaSeconds(long eorzeaSeconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((eorzeaSeconds * 7000 + 143) / 144);

    public readonly record struct EorzeaDate(int Year, int Month, int Day, int Hour, int Minute, int Second);

    /// <summary>Eorzean calendar: 12 moons per year, 32 suns per moon, 24 bells per sun.</summary>
    public static EorzeaDate ToEorzeaDate(long eorzeaSeconds)
    {
        const long secondsPerDay = 86400;
        var totalDays = eorzeaSeconds / secondsPerDay;
        return new EorzeaDate(
            Year: (int)(totalDays / (32 * 12)) + 1,
            Month: (int)(totalDays / 32 % 12) + 1,
            Day: (int)(totalDays % 32) + 1,
            Hour: (int)(eorzeaSeconds / 3600 % 24),
            Minute: (int)(eorzeaSeconds / 60 % 60),
            Second: (int)(eorzeaSeconds % 60));
    }

    /// <summary>"1st Astral Moon" .. "6th Umbral Moon" (odd months are Astral).</summary>
    public static string EorzeaMonthName(int month)
    {
        var n = (month + 1) / 2;
        var suffix = n switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n}{suffix} {(month % 2 == 1 ? "Astral" : "Umbral")} Moon";
    }

    /// <summary>Moon phase for an Eorzean day of the moon (1..32); each phase lasts 4 suns.</summary>
    public static string MoonPhaseName(int day) => MoonPhases[Math.Clamp((day - 1) / 4, 0, 7)];

    // ---- Weather -----------------------------------------------------------------------------

    /// <summary>Start of the 8-bell weather window containing <paramref name="unixSeconds"/>.</summary>
    public static long WeatherWindowStart(long unixSeconds) => unixSeconds - (unixSeconds % SecondsPerWeatherWindow);

    /// <summary>
    /// The 0..99 roll the client uses to pick weather from a WeatherRate row for the window
    /// containing <paramref name="unixSeconds"/>.
    /// </summary>
    public static int WeatherTarget(long unixSeconds)
    {
        var bell = (uint)(unixSeconds / SecondsPerBell);
        var increment = (bell + 8 - (bell % 8)) % 24;
        var totalDays = (uint)(unixSeconds / SecondsPerSun);
        var calcBase = (totalDays * 100) + increment;
        var step1 = (calcBase << 11) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 100);
    }

    /// <summary>Picks the index into a WeatherRate row (8 weather/rate pairs) for a roll.</summary>
    public static int PickWeatherIndex(ReadOnlySpan<byte> rates, int target)
    {
        var cumulative = 0;
        for (var i = 0; i < rates.Length; i++)
        {
            cumulative += rates[i];
            if (target < cumulative)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- Real-world resets -------------------------------------------------------------------

    /// <summary>Next occurrence (strictly after <paramref name="now"/>) of hh:00 UTC.</summary>
    public static DateTimeOffset NextDailyUtc(DateTimeOffset now, int hourUtc)
    {
        var utc = now.ToUniversalTime();
        var candidate = new DateTimeOffset(utc.Year, utc.Month, utc.Day, hourUtc, 0, 0, TimeSpan.Zero);
        return candidate > utc ? candidate : candidate.AddDays(1);
    }

    /// <summary>Next occurrence (strictly after <paramref name="now"/>) of a weekday at hh:00 UTC.</summary>
    public static DateTimeOffset NextWeeklyUtc(DateTimeOffset now, DayOfWeek day, int hourUtc)
    {
        var utc = now.ToUniversalTime();
        var candidate = new DateTimeOffset(utc.Year, utc.Month, utc.Day, hourUtc, 0, 0, TimeSpan.Zero);
        var delta = ((int)day - (int)candidate.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(delta);
        return candidate > utc ? candidate : candidate.AddDays(7);
    }
}

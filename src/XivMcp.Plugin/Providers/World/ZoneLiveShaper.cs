using XivMcp.Plugin.Providers.Character;

namespace XivMcp.Plugin.Providers.World;

public sealed record ZoneFateDto(
    uint Id,
    string Name,
    int Level,
    string State,
    int ProgressPercent,
    long? TimeRemainingSeconds,
    bool? HasBonus,
    MapCoordsDto? MapCoordinates,
    double Distance,
    bool? PlayerIsIn);

public sealed record ZoneFatesDto(int Active, uint? CurrentFateId, IReadOnlyList<ZoneFateDto> Nearest);

public sealed record ZoneAetheryteDto(uint AetheryteId, string? Name, uint GilCost, bool? IsHomePoint, bool? IsFavourite);

public sealed record ZoneHousingDto(string Area, int? Ward, int? Plot, int? Room, bool Inside);

public sealed record ZoneLiveDto(
    uint TerritoryId,
    string? Zone,
    string? Region,
    string IntendedUse,
    IdNameDto? Map,
    string? Area,
    string? SubArea,
    uint? Instance,
    bool IsInstancedZone,
    IdNameDto? Weather,
    MapCoordsDto? PlayerMapCoordinates,
    bool InSanctuary,
    bool InDuty,
    IdNameDto? Duty,
    bool InHousingDistrict,
    ZoneHousingDto? Housing,
    bool IsPvp,
    bool CanFly,
    ZoneFatesDto? Fates,
    int AttunedAetherytesHere,
    IReadOnlyList<ZoneAetheryteDto> Aetherytes,
    string? NearestAetheryte,
    IReadOnlyList<string>? Unavailable);

/// <summary>Pure shaping for get_zone_live: folds get_location, list_fates and list_aetherytes snapshots into one zone summary.</summary>
internal static class ZoneLiveShaper
{
    public const int MaxAetherytes = 20;
    public const int MaxFates = 25;

    public static ZoneLiveDto Build(
        LocationProvider.LocationDto location,
        FateProvider.FatesDto? fates,
        AetheryteProvider.AetherytesDto? aetherytes,
        int topFates,
        IReadOnlyList<string>? unavailable)
    {
        var here = (aetherytes?.Aetherytes ?? [])
            .Where(a => a.IsInCurrentZone && !a.IsHousing)
            .Select(a => new ZoneAetheryteDto(a.AetheryteId, a.Name, a.GilCost, a.IsHomePoint ? true : null, a.IsFavourite ? true : null))
            .ToList();

        var housing = location.Housing is { } h ? new ZoneHousingDto(h.Area, h.Ward, h.Plot, h.Room, h.Inside) : null;
        return new ZoneLiveDto(
            location.Territory.Id,
            location.Territory.Name,
            location.Territory.Region,
            location.Territory.IntendedUse,
            location.Map,
            location.Area,
            location.SubArea,
            location.Instance != 0 ? location.Instance : null,
            location.Instance != 0,
            location.Weather,
            location.MapCoordinates,
            location.InSanctuary,
            location.Duty != null,
            location.Duty,
            housing != null,
            housing,
            location.Territory.IsPvp,
            location.CanFly,
            fates == null ? null : Fates(fates, topFates),
            here.Count,
            here.Take(MaxAetherytes).ToList(),
            location.NearestAetheryte is { } nearest ? Describe(nearest) : null,
            unavailable is { Count: > 0 } ? unavailable : null);
    }

    public static ZoneFatesDto Fates(FateProvider.FatesDto fates, int top)
    {
        top = Math.Clamp(top, 0, MaxFates);
        // "Active" is what the map shows a marker for: FATEs that have ended or failed linger in the table briefly.
        var active = fates.Fates.Where(f => f.State is not ("Ended" or "Failed")).ToList();
        var ended = fates.Fates.Count - active.Count;
        return new ZoneFatesDto(
            Math.Max(0, fates.Total - ended),
            fates.CurrentFateId,
            active.Take(top).Select(f => new ZoneFateDto(
                f.Id,
                f.Name,
                f.Level,
                f.State,
                f.ProgressPercent,
                f.TimeRemainingSeconds,
                f.HasBonus ? true : null,
                f.MapCoordinates,
                f.Distance,
                f.IsOccupiedByPlayer || f.PlayerInsideRadius ? true : null)).ToList());
    }

    private static string Describe(LocationProvider.AetheryteNearbyDto aetheryte)
    {
        var name = aetheryte.Name ?? $"Aetheryte #{aetheryte.Id}";
        var distance = aetheryte.MapDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return aetheryte.Unlocked ? $"{name} ({distance} map units away)" : $"{name} ({distance} map units away, not attuned)";
    }
}

using System.Globalization;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Zones (TerritoryType): search, maps, aetherytes, weather tables, map markers and weather-window search.</summary>
[McpProvider("gamedata")]
public sealed class ZoneDataProvider
{
    private const int MaxHorizonDays = 60;

    private readonly GameDataIndex index;

    public ZoneDataProvider(IGameDataSource data) => index = GameDataIndex.For(data);

    public sealed record ZoneSummary(uint TerritoryId, string Name, string? Region, string Kind, uint IntendedUse, string? Expansion, NamedRef? Duty, bool HasWeather);

    public sealed record ZoneMap(uint MapId, string? Path, string? Name, string? SubName, int SizeFactor, int OffsetX, int OffsetY, bool IsDefault);

    public sealed record ZoneAetheryte(uint AetheryteId, string? Name, bool IsShard, uint? MapId, double? X, double? Y, int AethernetGroup);

    public sealed record WeatherChance(uint WeatherId, string? Name, int ChancePercent);

    public sealed record ZoneMarker(string Name, uint MapId, double X, double Y, uint Icon, int DataType);

    public sealed record ZoneInfo(
        uint TerritoryId,
        string Name,
        string? Region,
        string? ZoneName,
        string? InternalName,
        string Kind,
        uint IntendedUse,
        string? Expansion,
        NamedRef? Duty,
        bool IsPvp,
        bool MountsAllowed,
        IReadOnlyList<ZoneMap> Maps,
        IReadOnlyList<ZoneAetheryte> Aetherytes,
        uint WeatherRateId,
        IReadOnlyList<WeatherChance> Weather,
        SourceSection<ZoneMarker> Markers);

    public sealed record WeatherWindow(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string EorzeaStart,
        string EorzeaEnd,
        long DurationSeconds,
        long SecondsUntilStart,
        bool ActiveNow,
        NamedRef Weather,
        NamedRef? PreviousWeather,
        int Windows);

    public sealed record WeatherWindowsResult(
        uint TerritoryId,
        string? Zone,
        IReadOnlyList<NamedRef> Wanted,
        IReadOnlyList<NamedRef>? PreviousWanted,
        string? EorzeaHours,
        DateTimeOffset AfterUtc,
        int HorizonDays,
        int ChancePercent,
        IReadOnlyList<WeatherChance> PossibleWeather,
        IReadOnlyList<WeatherWindow> Windows,
        string Note);

    [McpTool("search_zones",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:TerritoryType", "lumina:PlaceName"],
        Title = "Search zones",
        Description =
            "Searches zones (TerritoryType rows that have a place name) by name, ranked exact > prefix > word > substring; a numeric query matches " +
            "the territory id. Filter by kind: city, field, inn, housing, duty, pvp or other (coarse, from TerritoryIntendedUse and the zone's Duty " +
            "Finder entry) and by region name. Query may be omitted when a filter is set. Returns {total, offset, returned, truncated, results:" +
            "[{territoryId, name, region, kind, intendedUse, expansion, duty, hasWeather}]}. The same name can appear several times: instanced " +
            "and quest copies of a zone are separate territories; the town or field copy is normally the lowest id with kind city/field. " +
            "Use get_zone_info for maps, aetherytes, weather and markers, and search_duties for duties by name.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<ZoneSummary> SearchZones(
        [McpParam("Zone name text (any case) or a territory id.")] string? query = null,
        [McpParam("Zone kind.", Enum = ["city", "field", "inn", "housing", "duty", "pvp", "other"])] string? kind = null,
        [McpParam("Region name text, e.g. \"La Noscea\", \"Thanalan\", \"Norvrandt\" (partial ok).")] string? region = null,
        [McpParam("Maximum results (1-500).", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var filters = new List<Func<GameDataIndex.ZoneEntry, bool>>();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var wanted = ZoneLookup.Kinds.FirstOrDefault(k => k.Equals(kind.Trim(), StringComparison.OrdinalIgnoreCase))
                         ?? throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"Unknown kind \"{kind}\". Use one of: {string.Join(", ", ZoneLookup.Kinds)}.");
            filters.Add(z => ZoneLookup.KindOf(z.IntendedUse, z.DutyId, z.IsPvp) == wanted);
        }

        if (!string.IsNullOrWhiteSpace(region))
        {
            var text = region.Trim();
            filters.Add(z => z.Region != null && z.Region.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(query) && filters.Count == 0)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Provide a query or at least one filter.");

        var page = string.IsNullOrWhiteSpace(query)
            ? Page<GameDataIndex.ZoneEntry>.From(index.Zones.Where(z => filters.All(f => f(z))).OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ThenBy(z => z.Id).ToList(), offset, limit)
            : TextSearch.Search(index.Zones, z => z.Id, z => z.Lower, query, filters.Count == 0 ? null : z => filters.All(f => f(z)), offset, limit);
        var results = page.Items.Select(Summarize).ToList();
        return new PagedResult<ZoneSummary>(page.Total, page.Offset, results.Count, page.Truncated, results);
    }

    [McpTool("get_zone_info",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:TerritoryType", "lumina:PlaceName", "lumina:Map", "lumina:MapMarker", "lumina:Aetheryte", "lumina:WeatherRate", "lumina:Weather", "lumina:ContentFinderCondition"],
        Title = "Get zone details",
        Description =
            "Static details of one zone (territoryId, or zone = a name): name, region, internalName (the level path id such as s1f1), kind " +
            "(city, field, inn, housing, duty, pvp, other) with the raw intendedUse id, expansion, the duty it hosts, isPvp, mountsAllowed, its " +
            "maps (mapId, path like s1f1/00, sub-name for floors, sizeFactor where 100 = a 41x41 coordinate grid, offsets, isDefault), aetherytes " +
            "and aethernet shards (isShard) with the map X/Y the game shows, the weather table (weatherRateId and each weather with chancePercent; " +
            "empty when weather is fixed or scripted), and markers: the named labels drawn on the zone's maps (settlements, landmarks, exits to " +
            "neighbouring zones) with map X/Y, icon and dataType (1 = link to another map, 3 = aetheryte, 4 = place tooltip, which aethernet shards use), " +
            "paged with markerLimit/markerOffset as {total, truncated, results}. Coordinates are ready for set_map_flag. " +
            "Nothing here depends on the character: use get_location for where the player is, list_aetherytes for unlocked teleports and " +
            "find_weather_windows or get_weather_forecast for when a weather occurs.",
        GameThread = false,
        RequiresLogin = false)]
    public ZoneInfo GetZoneInfo(
        [McpParam("TerritoryType row id. Provide this or zone.", Minimum = 1)] uint? territoryId = null,
        [McpParam("Zone name (any case); must identify one zone name. Ignored when territoryId is given.")] string? zone = null,
        [McpParam("Maximum map markers to return (0-200).", Minimum = 0, Maximum = 200)] int markerLimit = 40,
        [McpParam("Map markers to skip for paging.", Minimum = 0)] int markerOffset = 0)
    {
        var entry = ResolveZone(territoryId, zone, null);
        var row = index.Row<Sheets.TerritoryType>(entry.Id)!.Value;
        markerLimit = Math.Clamp(markerLimit, 0, 200);
        markerOffset = TextSearch.ClampOffset(markerOffset);

        var maps = new List<ZoneMap>();
        var markers = new List<ZoneMarker>();
        var aetherytes = new Dictionary<uint, ZoneAetheryte>();
        var mapIds = index.MapsByTerritory.TryGetValue(entry.Id, out var ids) ? ids : [];
        var placed = index.AetherytesByTerritory.TryGetValue(entry.Id, out var placedIds) ? placedIds : [];
        foreach (var mapId in mapIds.OrderBy(m => m != row.Map.RowId).ThenBy(m => m))
        {
            if (index.Row<Sheets.Map>(mapId) is not { } map) continue;
            maps.Add(new ZoneMap(
                mapId,
                GameDataIndex.NullIfEmpty(SheetJson.Text(map.Id)),
                GameDataIndex.NullIfEmpty(SheetJson.Text(map.PlaceName.ValueNullable?.Name ?? default)),
                GameDataIndex.NullIfEmpty(SheetJson.Text(map.PlaceNameSub.ValueNullable?.Name ?? default)),
                map.SizeFactor,
                map.OffsetX,
                map.OffsetY,
                mapId == row.Map.RowId));
            ReadMarkers(map, placed, markers, aetherytes);
        }

        // Aetherytes placed in the territory that no map marker shows still belong in the list, without coordinates.
        foreach (var aetheryteId in placed)
        {
            if (aetherytes.ContainsKey(aetheryteId) || index.Row<Sheets.Aetheryte>(aetheryteId) is not { } aetheryte) continue;
            aetherytes[aetheryteId] = ToAetheryte(aetheryte, null, null, null);
        }

        var markerPage = markerLimit == 0
            ? new SourceSection<ZoneMarker>(markers.Count, markers.Count > 0, [])
            : SourceMerge.Slice(markers, markerOffset, markerLimit);
        var (rateId, weather) = WeatherTable(row, out _, out _);
        return new ZoneInfo(
            entry.Id,
            entry.Name,
            entry.Region,
            GameDataIndex.NullIfEmpty(SheetJson.Text(row.PlaceNameZone.ValueNullable?.Name ?? default)),
            GameDataIndex.NullIfEmpty(SheetJson.Text(row.Name)),
            ZoneLookup.KindOf(entry.IntendedUse, entry.DutyId, entry.IsPvp),
            entry.IntendedUse,
            ExpansionName(entry.Expansion),
            DutyRef(entry.DutyId),
            entry.IsPvp,
            row.Mount,
            maps,
            aetherytes.Values.OrderBy(a => a.IsShard).ThenBy(a => a.AetheryteId).ToList(),
            rateId,
            weather,
            markerPage);
    }

    [McpResourceTemplate("ffxiv://zone/{territoryId}",
        Name = "Zone",
        Description = "Game-data record for a zone (TerritoryType id; same content as the get_zone_info tool with default paging).",
        GameThread = false,
        RequiresLogin = false)]
    public ZoneInfo ZoneResource(uint territoryId) => GetZoneInfo(territoryId);

    [McpTool("find_weather_windows",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:TerritoryType", "lumina:WeatherRate", "lumina:Weather", "clock:host"],
        Title = "Find upcoming windows of a weather",
        Description =
            "When a wanted weather next occurs in a zone, in real time. Weather is deterministic: it changes every 8 Eorzea hours (23m20s real " +
            "time, at ET 00:00, 08:00 and 16:00) and this uses the same algorithm as get_weather_forecast, but searches ahead (up to horizonDays, " +
            "default 14, max 60) instead of listing the next few windows. zone is a territory id or a zone name; weather is a name in the client " +
            "language (\"Rain\", \"Fog\") or a Weather id. Optional: previousWeather (the weather of the 8-bell window before must be this, as some " +
            "fish, FATE and hunt spawns require) and an Eorzea hour range eorzeaHourStart/eorzeaHourEnd (end exclusive, may wrap past midnight, e.g. " +
            "22 and 4), which trims each window to the hours that count. Returns the zone, the resolved wanted weather ids, chancePercent per " +
            "window from the zone's table, possibleWeather, and windows: startUtc, endUtc, eorzeaStart/eorzeaEnd (HH:00), durationSeconds, " +
            "secondsUntilStart (0 when activeNow), weather, previousWeather and windows = how many consecutive 8-bell windows were merged. " +
            "Fewer than count windows means nothing more was found within the horizon. Fails with not_found when the zone has no weather table " +
            "(duties, interiors) or the weather never occurs there; the message then lists what does. Times come from the host clock.",
        GameThread = false,
        RequiresLogin = false)]
    public WeatherWindowsResult FindWeatherWindows(
        [McpParam("Zone: TerritoryType id or zone name (any case), e.g. \"Eastern La Noscea\".")] string zone,
        [McpParam("Wanted weather: name in the client language (any case) or Weather row id.")] string weather,
        [McpParam("Weather the previous 8-bell window must have had (name or id). Omit for no requirement.")] string? previousWeather = null,
        [McpParam("First Eorzea hour that counts (0-23). Give together with eorzeaHourEnd.", Minimum = 0, Maximum = 23)] int? eorzeaHourStart = null,
        [McpParam("Eorzea hour at which it stops counting (0-24, exclusive). A value below eorzeaHourStart wraps past midnight.", Minimum = 0, Maximum = 24)] int? eorzeaHourEnd = null,
        [McpParam("How many windows to return (1-50).", Minimum = 1, Maximum = 50)] int count = 5,
        [McpParam("How many real days ahead to search (1-60).", Minimum = 1, Maximum = MaxHorizonDays)] int horizonDays = 14,
        [McpParam("Search from this instant instead of now: ISO 8601 UTC, e.g. 2026-01-31T18:00:00Z.")] string? afterUtc = null)
    {
        count = Math.Clamp(count, 1, 50);
        horizonDays = Math.Clamp(horizonDays, 1, MaxHorizonDays);
        if (eorzeaHourStart.HasValue != eorzeaHourEnd.HasValue)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Give eorzeaHourStart and eorzeaHourEnd together, or neither.");
        if (eorzeaHourStart is < 0 or > 23 || eorzeaHourEnd is < 0 or > 24)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "eorzeaHourStart must be 0-23 and eorzeaHourEnd 0-24.");

        var now = DateTimeOffset.UtcNow;
        var after = now;
        if (!string.IsNullOrWhiteSpace(afterUtc) &&
            !DateTimeOffset.TryParse(afterUtc.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out after))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"afterUtc \"{afterUtc}\" is not an ISO 8601 date-time, e.g. 2026-01-31T18:00:00Z.");

        var entry = ResolveZone(null, zone, z => z.HasWeather);
        var row = index.Row<Sheets.TerritoryType>(entry.Id)!.Value;
        var (_, possible) = WeatherTable(row, out var rates, out var weatherIds);
        if (possible.Count == 0)
            throw McpToolException.WithCode(McpErrorCodes.NotFound, $"{entry.Name} (territory {entry.Id}) has no weather table: weather there is fixed or scripted.");

        var available = string.Join(", ", possible.Select(p => $"{p.Name} ({p.ChancePercent}%)"));
        var wanted = ResolveWeather(weather, "weather");
        var wantedHere = possible.Where(p => wanted.Contains(p.WeatherId)).ToList();
        if (wantedHere.Count == 0)
            throw McpToolException.WithCode(McpErrorCodes.NotFound, $"{WeatherLabel(wanted)} never occurs in {entry.Name}. Weather there: {available}.");

        HashSet<uint>? previous = null;
        if (!string.IsNullOrWhiteSpace(previousWeather))
        {
            previous = ResolveWeather(previousWeather, "previousWeather");
            if (!possible.Any(p => previous.Contains(p.WeatherId)))
                throw McpToolException.WithCode(McpErrorCodes.NotFound, $"{WeatherLabel(previous)} never occurs in {entry.Name}, so it cannot be the previous weather. Weather there: {available}.");
        }

        var afterUnix = after.ToUnixTimeSeconds();
        var hits = WeatherWindows.Find(rates, weatherIds, wanted.Contains, previous != null ? previous.Contains : null,
            eorzeaHourStart, eorzeaHourEnd, afterUnix, count, horizonDays * 86400L);

        var windows = hits.Select(h => new WeatherWindow(
            DateTimeOffset.FromUnixTimeSeconds(h.StartUnix),
            DateTimeOffset.FromUnixTimeSeconds(h.EndUnix),
            EorzeaHour(h.StartUnix),
            EorzeaHour(h.EndUnix),
            h.EndUnix - h.StartUnix,
            Math.Max(0, h.StartUnix - afterUnix),
            h.StartUnix <= afterUnix,
            new NamedRef(h.WeatherId, WeatherName(h.WeatherId) ?? $"Weather #{h.WeatherId}"),
            h.PreviousWeatherId != 0 ? new NamedRef(h.PreviousWeatherId, WeatherName(h.PreviousWeatherId) ?? $"Weather #{h.PreviousWeatherId}") : null,
            h.Windows)).ToList();

        var segments = WeatherWindows.HourSegments(eorzeaHourStart, eorzeaHourEnd);
        return new WeatherWindowsResult(
            entry.Id,
            entry.Name,
            wantedHere.Select(p => new NamedRef(p.WeatherId, p.Name ?? $"Weather #{p.WeatherId}")).ToList(),
            previous?.Where(id => possible.Any(p => p.WeatherId == id)).Select(id => new NamedRef(id, WeatherName(id) ?? $"Weather #{id}")).ToList(),
            segments.Count == 1 && segments[0] == (0, 24) ? null : $"{eorzeaHourStart:00}:00-{eorzeaHourEnd:00}:00",
            after,
            horizonDays,
            wantedHere.Sum(p => p.ChancePercent),
            possible,
            windows,
            "Computed from the host clock with the game's weather algorithm; secondsUntilStart and activeNow are relative to afterUtc. " +
            "Scripted events and zones with individual weather can differ.");
    }

    // ------------------------------------------------------------------ helpers

    private GameDataIndex.ZoneEntry ResolveZone(uint? territoryId, string? zone, Func<GameDataIndex.ZoneEntry, bool>? prefer)
    {
        var text = territoryId is { } id ? id.ToString(CultureInfo.InvariantCulture) : zone;
        if (string.IsNullOrWhiteSpace(text))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Provide territoryId or zone.");
        var resolution = ZoneLookup.Resolve(index.Zones, text, prefer);
        if (resolution.Match is { } match) return match;
        if (resolution.Candidates.Count == 0)
            throw McpToolException.WithCode(McpErrorCodes.NotFound, $"No zone matches \"{text.Trim()}\". Use search_zones to look up territory ids.");
        throw McpToolException.WithCode(McpErrorCodes.InvalidArguments,
            $"\"{text.Trim()}\" matches several zones: {string.Join(", ", resolution.Candidates.Select(c => $"{c.Name} ({c.Id})"))}. Pass the full name or the territory id.");
    }

    private ZoneSummary Summarize(GameDataIndex.ZoneEntry z) => new(
        z.Id, z.Name, z.Region, ZoneLookup.KindOf(z.IntendedUse, z.DutyId, z.IsPvp), z.IntendedUse, ExpansionName(z.Expansion), DutyRef(z.DutyId), z.HasWeather);

    private NamedRef? DutyRef(uint dutyId) =>
        dutyId != 0 && index.Row<Sheets.ContentFinderCondition>(dutyId) is { } duty && SheetJson.Text(duty.Name) is { Length: > 0 } name
            ? new NamedRef(dutyId, GameDataIndex.Capitalize(name))
            : null;

    private string? ExpansionName(uint id) =>
        index.Row<Sheets.ExVersion>(id) is { } e ? GameDataIndex.NullIfEmpty(SheetJson.Text(e.Name)) : null;

    private void ReadMarkers(Sheets.Map map, uint[] placed, List<ZoneMarker> markers, Dictionary<uint, ZoneAetheryte> aetherytes)
    {
        if (map.MapMarkerRange == 0 || index.SubrowSheet<Sheets.MapMarker>().GetRowOrDefault(map.MapMarkerRange) is not { } rows) return;
        foreach (var marker in rows)
        {
            var x = GameMath.DisplayCoordinate(GameMath.MarkerToMapCoordinate(marker.X, map.SizeFactor));
            var y = GameMath.DisplayCoordinate(GameMath.MarkerToMapCoordinate(marker.Y, map.SizeFactor));
            // DataType 3 keys an Aetheryte row. DataType 4 keys a PlaceName: aethernet shards are drawn that way, so they
            // are matched through Aetheryte.AethernetName within this territory.
            if (marker.DataType == 3 && marker.DataKey.RowId != 0 && index.Row<Sheets.Aetheryte>(marker.DataKey.RowId) is { } aetheryte)
            {
                aetherytes.TryAdd(aetheryte.RowId, ToAetheryte(aetheryte, map.RowId, x, y));
            }
            else if (marker.DataType == 4 && marker.DataKey.RowId != 0)
            {
                foreach (var shardId in placed)
                {
                    if (index.Row<Sheets.Aetheryte>(shardId) is { IsAetheryte: false } shard && shard.AethernetName.RowId == marker.DataKey.RowId)
                        aetherytes.TryAdd(shardId, ToAetheryte(shard, map.RowId, x, y));
                }
            }

            var name = SheetJson.Text(marker.PlaceNameSubtext.ValueNullable?.Name ?? default);
            if (name.Length == 0) continue;
            markers.Add(new ZoneMarker(name, map.RowId, x, y, marker.Icon, marker.DataType));
        }
    }

    private static ZoneAetheryte ToAetheryte(Sheets.Aetheryte row, uint? mapId, double? x, double? y)
    {
        var isShard = !row.IsAetheryte;
        var name = SheetJson.Text((isShard ? row.AethernetName : row.PlaceName).ValueNullable?.Name ?? default);
        return new ZoneAetheryte(row.RowId, GameDataIndex.NullIfEmpty(name), isShard, mapId, x, y, row.AethernetGroup);
    }

    private (uint RateId, List<WeatherChance> Possible) WeatherTable(Sheets.TerritoryType territory, out byte[] rates, out uint[] weatherIds)
    {
        rates = new byte[8];
        weatherIds = new uint[8];
        var possible = new List<WeatherChance>();
        var rateId = territory.WeatherRate.RowId;
        if (index.Row<Sheets.WeatherRate>(rateId) is not { } rate) return (rateId, possible);
        for (var i = 0; i < 8 && i < rate.Rate.Count && i < rate.Weather.Count; i++)
        {
            rates[i] = rate.Rate[i];
            weatherIds[i] = rate.Weather[i].RowId;
            var weatherId = weatherIds[i];
            int chance = rates[i];
            if (chance == 0 || weatherId == 0) continue;
            var existing = possible.FindIndex(p => p.WeatherId == weatherId);
            if (existing >= 0) possible[existing] = possible[existing] with { ChancePercent = possible[existing].ChancePercent + chance };
            else possible.Add(new WeatherChance(weatherId, WeatherName(weatherId), chance));
        }

        return (rateId, possible);
    }

    private string? WeatherName(uint weatherId) =>
        index.Row<Sheets.Weather>(weatherId) is { } w ? GameDataIndex.NullIfEmpty(SheetJson.Text(w.Name)) : null;

    private string WeatherLabel(HashSet<uint> ids) => WeatherName(ids.First()) ?? $"Weather #{ids.First()}";

    private HashSet<uint> ResolveWeather(string text, string argument)
    {
        var t = text.Trim();
        var ids = new HashSet<uint>();
        if (uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            if (id != 0 && WeatherName(id) != null) ids.Add(id);
        }
        else
        {
            foreach (var row in index.Sheet<Sheets.Weather>())
            {
                if (row.RowId != 0 && SheetJson.Text(row.Name).Equals(t, StringComparison.OrdinalIgnoreCase)) ids.Add(row.RowId);
            }
        }

        return ids.Count > 0
            ? ids
            : throw McpToolException.WithCode(McpErrorCodes.NotFound, $"No weather matches {argument} \"{t}\". Use the exact name shown by get_zone_info or get_weather_forecast, or a Weather id.");
    }

    private static string EorzeaHour(long unixSeconds)
    {
        var date = GameMath.ToEorzeaDate(GameMath.ToEorzeaSeconds(unixSeconds));
        return $"{date.Hour:00}:{date.Minute:00}";
    }
}

using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Where a gatherable item comes from: which nodes, in which zone, at which map coordinates, and — for timed
/// (unspoiled/ephemeral) nodes — when the window opens on the Eorzea clock. Pure game data plus the host
/// clock, so it works at the title screen.
/// </summary>
[McpProvider("gamedata")]
public sealed class GatheringDataProvider
{
    private const int MaxPointsPerSource = 12;

    private readonly GameDataIndex index;

    public GatheringDataProvider(IDataManager data) => index = GameDataIndex.For(data);

    public sealed record NodeWindowDto(
        string Start,
        string End,
        bool OpenNow,
        DateTimeOffset NextStartUtc,
        DateTimeOffset NextEndUtc,
        long SecondsUntilStart,
        long SecondsUntilEnd);

    public sealed record NodePointDto(
        uint GatheringPointBaseId,
        uint TerritoryId,
        string? Zone,
        string? Place,
        uint MapId,
        double? MapX,
        double? MapY,
        int? RadiusYalms,
        int Level,
        string? GatheringType,
        bool Timed,
        NodeWindowDto? Window,
        string? WindowKind);

    public sealed record GatheringSourceDto(
        uint GatheringItemId,
        int GatheringItemLevel,
        int Stars,
        bool Hidden,
        int PerceptionRequired,
        bool IsTimed,
        int TotalPoints,
        int Returned,
        bool Truncated,
        List<NodePointDto> Points);

    public sealed record GatheringInfoDto(
        uint ItemId,
        string? ItemName,
        bool Gatherable,
        string EorzeaTime,
        DateTimeOffset AsOfUtc,
        List<GatheringSourceDto> Sources,
        string Note);

    [McpTool("get_gathering_info",
        Title = "Get gathering nodes for an item",
        GameThread = false,
        RequiresLogin = false,
        Description =
            "Where a gatherable item is found and, for timed nodes, when. Returns sources (one per GatheringItem row) with " +
            "gatheringItemLevel, stars, hidden, perceptionRequired, isTimed and points: territoryId, zone, place, mapId, map " +
            "coordinates (the x/y the game shows, ready for set_map_flag), radiusYalms, node level, gatheringType " +
            "(Mining/Quarrying/Logging/Harvesting) and, when the node is timed, window {start, end (Eorzea HH:MM), openNow, " +
            "nextStartUtc, nextEndUtc, secondsUntilStart, secondsUntilEnd} plus windowKind (unspoiled = rare pop table, " +
            "ephemeral = node only exists during those bells). Times are computed from the host clock with the game's own " +
            "Eorzea-time maths (1 bell = 175 real seconds), like get_weather_forecast. " +
            "Items that are not gathered return gatherable=false with no sources; use get_item to see how they are obtained instead.")]
    public GatheringInfoDto GetGatheringInfo(
        [McpParam("Item id to look up. Use search_items with isGatherable=true to find one.", Minimum = 1)] uint itemId,
        [McpParam("Only return nodes that are timed (unspoiled or ephemeral).")] bool timedOnly = false,
        [McpParam("Maximum node locations per source (1-12).", Minimum = 1, Maximum = MaxPointsPerSource)] int pointLimit = 6)
    {
        if (itemId == 0)
        {
            throw new McpToolException("itemId is required.");
        }

        pointLimit = Math.Clamp(pointLimit, 1, MaxPointsPerSource);
        var now = DateTimeOffset.UtcNow;
        var eorzeaSeconds = GameMath.ToEorzeaSeconds(now);
        var date = GameMath.ToEorzeaDate(eorzeaSeconds);
        var itemName = index.ItemName(itemId);

        if (!index.Gathering.ByItem.TryGetValue(itemId, out var gatheringItems))
        {
            return new GatheringInfoDto(
                itemId,
                itemName,
                false,
                $"{date.Hour:00}:{date.Minute:00}",
                now,
                [],
                "This item is not gathered from mining/quarrying/logging/harvesting nodes. get_item lists its other sources.");
        }

        var sources = new List<GatheringSourceDto>();
        foreach (var gatheringItemId in gatheringItems)
        {
            if (index.Row<Sheets.GatheringItem>(gatheringItemId) is not { } gatheringItem)
            {
                continue;
            }

            var levelRow = gatheringItem.GatheringItemLevel.ValueNullable;
            var points = new List<NodePointDto>();
            var total = 0;
            var anyTimed = false;
            var seen = new HashSet<(uint, uint)>();

            if (index.Gathering.BasesByGatheringItem.TryGetValue(gatheringItemId, out var bases))
            {
                foreach (var baseId in bases)
                {
                    if (index.Row<Sheets.GatheringPointBase>(baseId) is not { } pointBase)
                    {
                        continue;
                    }

                    var typeName = GameDataIndex.NullIfEmpty(SheetJson.Text(pointBase.GatheringType.ValueNullable?.Name ?? default));
                    if (!index.Gathering.PointsByBase.TryGetValue(baseId, out var pointIds))
                    {
                        continue;
                    }

                    foreach (var pointId in pointIds)
                    {
                        if (index.Row<Sheets.GatheringPoint>(pointId) is not { } point || point.TerritoryType.RowId == 0)
                        {
                            continue;
                        }

                        if (!seen.Add((point.TerritoryType.RowId, point.PlaceName.RowId)))
                        {
                            continue;
                        }

                        var (window, kind) = WindowFor(pointId, eorzeaSeconds, now);
                        var timed = window != null;
                        anyTimed |= timed;
                        if (timedOnly && !timed)
                        {
                            continue;
                        }

                        total++;
                        if (points.Count >= pointLimit)
                        {
                            continue;
                        }

                        var (mapId, mapX, mapY, radius) = MapPosition(baseId, point.TerritoryType.RowId);
                        points.Add(new NodePointDto(
                            baseId,
                            point.TerritoryType.RowId,
                            index.TerritoryName(point.TerritoryType.RowId),
                            GameDataIndex.NullIfEmpty(SheetJson.Text(point.PlaceName.ValueNullable?.Name ?? default)),
                            mapId,
                            mapX,
                            mapY,
                            radius,
                            pointBase.GatheringLevel,
                            typeName,
                            timed,
                            window,
                            kind));
                    }
                }
            }

            if (timedOnly && points.Count == 0)
            {
                continue;
            }

            sources.Add(new GatheringSourceDto(
                gatheringItemId,
                levelRow?.GatheringItemLevel ?? 0,
                levelRow?.Stars ?? 0,
                gatheringItem.IsHidden,
                gatheringItem.PerceptionReq,
                anyTimed,
                total,
                points.Count,
                points.Count < total,
                points));
        }

        return new GatheringInfoDto(
            itemId,
            itemName,
            sources.Count > 0,
            $"{date.Hour:00}:{date.Minute:00}",
            now,
            sources,
            "Node windows are computed from the host clock; the game server's Eorzea clock can be a second or two off. " +
            "Hidden nodes still need the listed perception to reveal the slot.");
    }

    private (NodeWindowDto? Window, string? Kind) WindowFor(uint gatheringPointId, long eorzeaSeconds, DateTimeOffset now)
    {
        if (index.Row<Sheets.GatheringPointTransient>(gatheringPointId) is not { } transient)
        {
            return (null, null);
        }

        var rareTable = transient.GatheringRarePopTimeTable;
        if (rareTable.RowId != 0 && rareTable.ValueNullable is { } table)
        {
            var entries = new List<(int Start, int Duration)>(3);
            for (var i = 0; i < table.StartTime.Count && i < table.Duration.Count; i++)
            {
                entries.Add((table.StartTime[i], table.Duration[i]));
            }

            if (GatheringWindows.Soonest(eorzeaSeconds, entries) is { } unspoiled)
            {
                return (Describe(unspoiled, now), "unspoiled");
            }
        }

        if (GatheringWindows.PackedToSeconds(transient.EphemeralStartTime) is { } start &&
            GatheringWindows.PackedToSeconds(transient.EphemeralEndTime) is { } end &&
            start != end)
        {
            var duration = end > start ? end - start : 86400 - start + end;
            var packedDuration = (duration / 3600 * 100) + (duration % 3600 / 60);
            if (GatheringWindows.Next(eorzeaSeconds, transient.EphemeralStartTime, packedDuration) is { } ephemeral)
            {
                return (Describe(ephemeral, now), "ephemeral");
            }
        }

        return (null, null);
    }

    private static NodeWindowDto Describe(GatheringWindows.Window window, DateTimeOffset now)
    {
        var startUtc = GameMath.FromEorzeaSeconds(window.StartEorzeaSeconds);
        var endUtc = GameMath.FromEorzeaSeconds(window.EndEorzeaSeconds);
        return new NodeWindowDto(
            $"{window.StartEorzeaSeconds / 3600 % 24:00}:{window.StartEorzeaSeconds / 60 % 60:00}",
            $"{window.EndEorzeaSeconds / 3600 % 24:00}:{window.EndEorzeaSeconds / 60 % 60:00}",
            window.OpenNow,
            startUtc,
            endUtc,
            (long)Math.Round((startUtc - now).TotalSeconds),
            (long)Math.Round((endUtc - now).TotalSeconds));
    }

    private (uint MapId, double? X, double? Y, int? Radius) MapPosition(uint gatheringPointBaseId, uint territoryId)
    {
        var mapId = 0u;
        ushort sizeFactor = 100;
        short offsetX = 0;
        short offsetY = 0;
        if (index.Row<Sheets.TerritoryType>(territoryId) is { } territory && territory.Map.ValueNullable is { } map)
        {
            mapId = territory.Map.RowId;
            sizeFactor = map.SizeFactor == 0 ? (ushort)100 : map.SizeFactor;
            offsetX = map.OffsetX;
            offsetY = map.OffsetY;
        }

        if (index.Row<Sheets.ExportedGatheringPoint>(gatheringPointBaseId) is not { } exported ||
            (exported.X == 0 && exported.Y == 0))
        {
            return (mapId, null, null, null);
        }

        return (
            mapId,
            GameMath.Round(GameMath.WorldToMapCoordinate(exported.X, sizeFactor, offsetX), 1),
            GameMath.Round(GameMath.WorldToMapCoordinate(exported.Y, sizeFactor, offsetY), 1),
            exported.Radius > 0 ? exported.Radius : null);
    }
}

using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.World;

/// <summary>
/// Map maths helpers: converting between the coordinates the game shows on the map and the world positions
/// other tools report, and finding the aetheryte nearest a point. Pure game data, so both work at the title
/// screen and on any zone, not just the one the player is in.
/// </summary>
[McpProvider("world")]
public sealed class CoordinateProvider
{
    private readonly GameDataIndex index;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;

    public CoordinateProvider(IDataManager data, IClientState clientState, IObjectTable objects)
    {
        index = GameDataIndex.For(data);
        this.clientState = clientState;
        this.objects = objects;
    }

    public sealed record CoordinatesDto(
        uint TerritoryId,
        string? Zone,
        uint MapId,
        string? MapName,
        double MapX,
        double MapY,
        double WorldX,
        double WorldZ,
        int SizeFactor,
        string FlagText,
        string Note);

    public sealed record NearestAetheryteDto(
        uint AetheryteId,
        string? Name,
        uint TerritoryId,
        string? Zone,
        double? MapX,
        double? MapY,
        double? DistanceOnMap,
        bool IsAethernetShard);

    public sealed record NearestAetherytesDto(
        uint TerritoryId,
        string? Zone,
        double FromMapX,
        double FromMapY,
        int Total,
        List<NearestAetheryteDto> Aetherytes,
        string Note);

    [McpTool("convert_coordinates",
        Sources = ["lumina:Map", "lumina:TerritoryType"],
        Title = "Convert map and world coordinates",
        GameThread = false,
        RequiresLogin = false,
        Description =
            "Converts between the map coordinates the game displays (the x/y in quest guides, hunt trains and <flag> links, roughly 1-42) " +
            "and the world position other tools report (worldX / worldZ). Give either x and y, or worldX and worldZ, plus the zone: " +
            "territoryId or mapId (default: the player's current zone). Returns both coordinate pairs, the map's sizeFactor and flagText, " +
            "a ready-to-read \"Zone ( x , y )\" string. Use it to turn a position from get_player, list_nearby_objects or list_fates into " +
            "the numbers a human reads, or to feed set_map_flag from map coordinates you were told. No game memory is read for another zone.")]
    public CoordinatesDto ConvertCoordinates(
        [McpParam("Map X as the game displays it. Use with y.")] double? x = null,
        [McpParam("Map Y as the game displays it. Use with x.")] double? y = null,
        [McpParam("World X position. Use with worldZ instead of x/y.")] double? worldX = null,
        [McpParam("World Z position (the horizontal axis that maps to map Y). Use with worldX.")] double? worldZ = null,
        [McpParam("TerritoryType row id; default is the player's current zone.", Minimum = 0)] uint? territoryId = null,
        [McpParam("Map row id; takes precedence over territoryId.", Minimum = 0)] uint? mapId = null)
    {
        var hasMap = x.HasValue && y.HasValue;
        var hasWorld = worldX.HasValue && worldZ.HasValue;
        if (hasMap == hasWorld)
        {
            throw new McpToolException("Give either map coordinates (x and y) or world coordinates (worldX and worldZ), not both and not neither.");
        }

        var (territory, map) = ResolveMap(territoryId, mapId);
        var sizeFactor = map.SizeFactor == 0 ? (ushort)100 : map.SizeFactor;

        float mx, my, wx, wz;
        if (hasMap)
        {
            mx = (float)x!.Value;
            my = (float)y!.Value;
            wx = GameMath.MapToWorldCoordinate(mx, sizeFactor, map.OffsetX);
            wz = GameMath.MapToWorldCoordinate(my, sizeFactor, map.OffsetY);
        }
        else
        {
            wx = (float)worldX!.Value;
            wz = (float)worldZ!.Value;
            mx = GameMath.WorldToMapCoordinate(wx, sizeFactor, map.OffsetX);
            my = GameMath.WorldToMapCoordinate(wz, sizeFactor, map.OffsetY);
        }

        if (!float.IsFinite(mx) || !float.IsFinite(my) || !float.IsFinite(wx) || !float.IsFinite(wz))
        {
            throw new McpToolException("Those coordinates are not finite numbers.");
        }

        var zone = index.TerritoryName(territory);
        var mapName = GameDataIndex.NullIfEmpty(SheetJson.Text(map.PlaceName.ValueNullable?.Name ?? default));
        var displayX = GameMath.DisplayCoordinate(mx);
        var displayY = GameMath.DisplayCoordinate(my);

        return new CoordinatesDto(
            territory,
            zone,
            map.RowId,
            mapName,
            displayX,
            displayY,
            GameMath.Round(wx),
            GameMath.Round(wz),
            sizeFactor,
            $"{zone ?? mapName ?? "Unknown"} ( {displayX:0.0}  , {displayY:0.0} )",
            "Map coordinates are rounded down to one decimal exactly like the in-game display; converting back can differ by a few world units.");
    }

    [McpTool("find_nearest_aetheryte",
        Sources = ["lumina:Aetheryte", "lumina:Level", "dalamud:IClientState"],
        Title = "Find the nearest aetheryte",
        GameThread = false,
        RequiresLogin = false,
        Description =
            "Aetherytes in a zone, nearest first, to a point given as map coordinates (x/y) or world coordinates (worldX/worldZ); with no " +
            "point and a logged-in character it uses the player's position. Each entry: aetheryteId, name, territoryId, zone, map " +
            "coordinates, distanceOnMap (in map units, not yalms) and isAethernetShard (city aethernet shards cannot be teleported to " +
            "from outside). This is game data: it lists every aetheryte in the zone whether or not the character has attuned to it. " +
            "Use list_aetherytes for what the player can actually teleport to and what it costs, then teleport.")]
    public NearestAetherytesDto FindNearestAetheryte(
        [McpParam("Map X of the point. Use with y.")] double? x = null,
        [McpParam("Map Y of the point. Use with x.")] double? y = null,
        [McpParam("World X of the point. Use with worldZ.")] double? worldX = null,
        [McpParam("World Z of the point. Use with worldX.")] double? worldZ = null,
        [McpParam("TerritoryType row id; default is the player's current zone.", Minimum = 0)] uint? territoryId = null,
        [McpParam("Maximum entries (1-50).", Minimum = 1, Maximum = 50)] int limit = 5,
        [McpParam("Leave out city aethernet shards.")] bool excludeAethernet = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        var (territory, map) = ResolveMap(territoryId, null);
        var sizeFactor = map.SizeFactor == 0 ? (ushort)100 : map.SizeFactor;

        double fromX, fromY;
        if (x.HasValue && y.HasValue)
        {
            (fromX, fromY) = (x.Value, y.Value);
        }
        else if (worldX.HasValue && worldZ.HasValue)
        {
            fromX = GameMath.WorldToMapCoordinate((float)worldX.Value, sizeFactor, map.OffsetX);
            fromY = GameMath.WorldToMapCoordinate((float)worldZ.Value, sizeFactor, map.OffsetY);
        }
        else
        {
            var position = objects.LocalPlayer?.Position
                           ?? throw new McpToolException("No point given and no character is logged in. Pass x/y or worldX/worldZ.");
            if (clientState.TerritoryType != territory)
            {
                throw new McpToolException($"The player is not in territory {territory}; pass x/y or worldX/worldZ for that zone.");
            }

            fromX = GameMath.WorldToMapCoordinate(position.X, sizeFactor, map.OffsetX);
            fromY = GameMath.WorldToMapCoordinate(position.Z, sizeFactor, map.OffsetY);
        }

        var found = new List<NearestAetheryteDto>();
        foreach (var aetheryte in index.Sheet<Sheets.Aetheryte>())
        {
            if (aetheryte.RowId == 0 || aetheryte.Territory.RowId != territory)
            {
                continue;
            }

            var shard = !aetheryte.IsAetheryte;
            if (excludeAethernet && shard)
            {
                continue;
            }

            var name = GameDataIndex.NullIfEmpty(SheetJson.Text(aetheryte.PlaceName.ValueNullable?.Name ?? default))
                       ?? GameDataIndex.NullIfEmpty(SheetJson.Text(aetheryte.AethernetName.ValueNullable?.Name ?? default));
            var location = aetheryte.Level.Count > 0 ? index.Location(aetheryte.Level[0].RowId) : null;
            double? distance = location is { X: { } lx, Y: { } ly }
                ? Math.Round(Math.Sqrt(((lx - fromX) * (lx - fromX)) + ((ly - fromY) * (ly - fromY))), 1)
                : null;

            found.Add(new NearestAetheryteDto(
                aetheryte.RowId,
                name,
                territory,
                index.TerritoryName(territory),
                location?.X,
                location?.Y,
                distance,
                shard));
        }

        var ordered = found
            .OrderBy(a => a.DistanceOnMap ?? double.MaxValue)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return new NearestAetherytesDto(
            territory,
            index.TerritoryName(territory),
            Math.Round(fromX, 1),
            Math.Round(fromY, 1),
            found.Count,
            ordered,
            "Distances are straight lines on the map, ignoring walls, height and travel routes.");
    }

    private (uint TerritoryId, Sheets.Map Map) ResolveMap(uint? territoryId, uint? mapId)
    {
        if (mapId is { } requestedMap && requestedMap != 0)
        {
            if (index.Row<Sheets.Map>(requestedMap) is not { } map)
            {
                throw new McpToolException($"Map {requestedMap} not found.");
            }

            return (territoryId ?? map.TerritoryType.RowId, map);
        }

        var territory = territoryId ?? clientState.TerritoryType;
        if (territory == 0)
        {
            throw new McpToolException("No zone given and the player is not in one; pass territoryId or mapId.");
        }

        if (index.Row<Sheets.TerritoryType>(territory) is not { } territoryRow)
        {
            throw new McpToolException($"Territory {territory} not found.");
        }

        if (territoryRow.Map.ValueNullable is not { } territoryMap)
        {
            throw new McpToolException($"Territory {territory} has no map.");
        }

        return (territory, territoryMap);
    }
}

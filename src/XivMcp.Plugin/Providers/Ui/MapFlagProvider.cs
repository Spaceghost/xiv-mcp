using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using AgentMapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace XivMcp.Plugin.Providers.Ui;

/// <summary>Places the map flag (the same marker &lt;flag&gt; refers to) and optionally opens the map.</summary>
[McpProvider("ui")]
public sealed unsafe class MapFlagProvider
{
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;

    public MapFlagProvider(IClientState clientState, IDataManager dataManager)
    {
        this.clientState = clientState;
        this.dataManager = dataManager;
    }

    [McpTool("set_map_flag",
        Sources = ["client:AgentMap", "lumina:Map"],
        ApprovalSummary = "Place your map flag at {x} {y} {worldX} {worldZ} (territory {territoryId}, map {mapId}) and open the map: {openMap}.",
        RequiresApproval = true,
        Title = "Set map flag",
        Description =
            "Places the user's map flag marker (the one shown on the map/minimap and inserted by <flag> in chat) and by default opens the map window on it. Local only; nothing is sent to other players. " +
            "Give EITHER map coordinates x/y exactly as the game displays them (e.g. x=11.2, y=9.7 — the numbers in quest guides and hunt trains) OR world coordinates worldX/worldZ (from position data of other tools; world Y/height is not needed). " +
            "territoryId defaults to the current zone and mapId to the current map (or the territory's main map); pass mapId alone for another zone's map. " +
            "Returns territoryId, mapId, placeName, the map coordinates and the world coordinates actually used.",
        Permission = ToolPermission.Ui)]
    public MapFlagResult SetMapFlag(
        [McpParam("Map X coordinate as shown in game (roughly 1-42). Use with y.")] float? x = null,
        [McpParam("Map Y coordinate as shown in game (roughly 1-42). Use with x.")] float? y = null,
        [McpParam("World X position. Use with worldZ instead of x/y.")] float? worldX = null,
        [McpParam("World Z position (the horizontal axis that maps to map Y). Use with worldX.")] float? worldZ = null,
        [McpParam("TerritoryType row id; default is the current zone.")] uint? territoryId = null,
        [McpParam("Map row id; default is the current map of that territory.")] uint? mapId = null,
        [McpParam("Open the map window centred on the flag.")] bool openMap = true,
        [McpParam("Optional map window title.")] string? title = null)
    {
        var hasMap = x.HasValue || y.HasValue;
        var hasWorld = worldX.HasValue || worldZ.HasValue;
        if (hasMap && hasWorld)
            throw new McpToolException("Give either map coordinates (x, y) or world coordinates (worldX, worldZ), not both.");
        if (!hasMap && !hasWorld)
            throw new McpToolException("Give map coordinates x and y (as shown in game) or world coordinates worldX and worldZ.");
        if (hasMap && !(x.HasValue && y.HasValue))
            throw new McpToolException("Map coordinates need both x and y.");
        if (hasWorld && !(worldX.HasValue && worldZ.HasValue))
            throw new McpToolException("World coordinates need both worldX and worldZ.");

        var maps = dataManager.GetExcelSheet<Map>();
        var territories = dataManager.GetExcelSheet<TerritoryType>();

        uint resolvedTerritory;
        uint resolvedMap;
        if (mapId is { } requestedMap)
        {
            if (!maps.TryGetRow(requestedMap, out var mapRowForId) || requestedMap == 0)
                throw new McpToolException($"Map {requestedMap} not found.");
            resolvedMap = requestedMap;
            resolvedTerritory = territoryId ?? mapRowForId.TerritoryType.RowId;
        }
        else if (territoryId is { } requestedTerritory)
        {
            if (!territories.TryGetRow(requestedTerritory, out var territoryRow) || requestedTerritory == 0)
                throw new McpToolException($"Territory {requestedTerritory} not found.");
            resolvedTerritory = requestedTerritory;
            resolvedMap = requestedTerritory == clientState.TerritoryType && clientState.MapId != 0
                ? clientState.MapId
                : territoryRow.Map.RowId;
        }
        else
        {
            resolvedTerritory = clientState.TerritoryType;
            if (resolvedTerritory == 0)
                throw new McpToolException("Not currently in a zone; pass territoryId or mapId.");
            resolvedMap = clientState.MapId != 0
                ? clientState.MapId
                : territories.TryGetRow(resolvedTerritory, out var current) ? current.Map.RowId : 0;
        }

        if (resolvedMap == 0 || !maps.TryGetRow(resolvedMap, out var map))
            throw new McpToolException($"Territory {resolvedTerritory} has no map.");
        if (resolvedTerritory == 0)
            throw new McpToolException($"Map {resolvedMap} is not attached to a territory; pass territoryId.");

        var sizeFactor = map.SizeFactor == 0 ? (ushort)100 : map.SizeFactor;
        float wx, wz, mx, my;
        if (hasMap)
        {
            mx = x!.Value;
            my = y!.Value;
            var max = 41f / (sizeFactor / 100f) + 1.5f;
            if (!float.IsFinite(mx) || !float.IsFinite(my) || mx < 0.5f || my < 0.5f || mx > max || my > max)
                throw new McpToolException($"Map coordinates ({mx}, {my}) are outside this map (valid range about 1 to {max - 0.5f:0.#}).");
            wx = GameMath.MapToWorldCoordinate(mx, sizeFactor, map.OffsetX);
            wz = GameMath.MapToWorldCoordinate(my, sizeFactor, map.OffsetY);
        }
        else
        {
            wx = worldX!.Value;
            wz = worldZ!.Value;
            if (!float.IsFinite(wx) || !float.IsFinite(wz) || Math.Abs(wx) > 5000 || Math.Abs(wz) > 5000)
                throw new McpToolException($"World coordinates ({wx}, {wz}) are not a valid position.");
            mx = GameMath.WorldToMapCoordinate(wx, sizeFactor, map.OffsetX);
            my = GameMath.WorldToMapCoordinate(wz, sizeFactor, map.OffsetY);
        }

        var agent = AgentMap.Instance();
        if (agent == null)
            throw new McpToolException("The map agent is not available right now.");

        agent->SetFlagMapMarker(resolvedTerritory, resolvedMap, wx, wz);

        var windowTitle = string.IsNullOrWhiteSpace(title) ? null : new string(title.Where(c => !char.IsControl(c)).Take(100).ToArray()).Trim();
        if (openMap)
            agent->OpenMap(resolvedMap, resolvedTerritory, string.IsNullOrEmpty(windowTitle) ? null : windowTitle, AgentMapType.FlagMarker);

        var placeName = map.PlaceName.ValueNullable?.Name.ExtractText();
        var subName = map.PlaceNameSub.ValueNullable?.Name.ExtractText();

        return new MapFlagResult(
            resolvedTerritory,
            resolvedMap,
            string.IsNullOrEmpty(placeName) ? null : placeName,
            string.IsNullOrEmpty(subName) ? null : subName,
            MathF.Round(mx, 1),
            MathF.Round(my, 1),
            MathF.Round(wx, 2),
            MathF.Round(wz, 2),
            openMap);
    }

    public sealed record MapFlagResult(
        uint TerritoryId,
        uint MapId,
        string? PlaceName,
        string? PlaceNameSub,
        float MapX,
        float MapY,
        float WorldX,
        float WorldZ,
        bool MapOpened);
}

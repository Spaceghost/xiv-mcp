using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Character;
using XivMcp.Plugin.Util;
using CsEventFramework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework;
using CsGameMain = FFXIVClientStructs.FFXIV.Client.Game.GameMain;
using CsHousingManager = FFXIVClientStructs.FFXIV.Client.Game.HousingManager;
using CsPlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using CsTerritoryInfo = FFXIVClientStructs.FFXIV.Client.Game.UI.TerritoryInfo;
using CsTerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;
using CsUIState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState;
using CsWeatherManager = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager;
using LAetheryte = Lumina.Excel.Sheets.Aetheryte;
using LContentFinderCondition = Lumina.Excel.Sheets.ContentFinderCondition;
using LMap = Lumina.Excel.Sheets.Map;
using LMapMarker = Lumina.Excel.Sheets.MapMarker;
using LPlaceName = Lumina.Excel.Sheets.PlaceName;
using LTerritoryType = Lumina.Excel.Sheets.TerritoryType;
using LWeather = Lumina.Excel.Sheets.Weather;

namespace XivMcp.Plugin.Providers.World;

[McpProvider("world")]
public sealed class LocationProvider : IDisposable
{
    public const string ResourceUri = "ffxiv://location";

    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IDataManager data;
    private readonly IMcpNotifier notifier;
    private readonly IPluginLog log;

    public LocationProvider(IObjectTable objects, IClientState clientState, IDataManager data, IMcpNotifier notifier, IPluginLog log)
    {
        this.objects = objects;
        this.clientState = clientState;
        this.data = data;
        this.notifier = notifier;
        this.log = log;
        clientState.TerritoryChanged += OnTerritoryChanged;
        clientState.MapIdChanged += OnMapChanged;
    }

    public sealed record TerritoryDto(
        uint Id,
        string? Name,
        string? InternalName,
        string? Region,
        string? Zone,
        uint IntendedUseId,
        string IntendedUse,
        bool IsPvp,
        bool MountsAllowed);

    public sealed record HousingDto(
        string Area,
        int? Ward,
        int? Plot,
        int? Room,
        int Division,
        bool Inside,
        bool InWorkshop,
        int RawWard,
        int RawPlot);

    public sealed record AetheryteNearbyDto(uint Id, string? Name, bool IsAethernetShard, bool Unlocked, MapCoordsDto MapCoordinates, double MapDistance);

    public sealed record LocationDto(
        TerritoryDto Territory,
        IdNameDto? Map,
        string? Area,
        string? SubArea,
        uint Instance,
        IdNameDto? Duty,
        Vec3Dto Position,
        MapCoordsDto? MapCoordinates,
        IdNameDto? Weather,
        HousingDto? Housing,
        bool InSanctuary,
        bool CanFly,
        bool FlyingUnlockedInZone,
        AetheryteNearbyDto? NearestAetheryte,
        AetheryteNearbyDto? NearestAethernetShard);

    [McpTool("get_location",
        Title = "Get current location",
        Description = "Where the player is. Returns territory {id, name, internalName, region, zone, intendedUseId, intendedUse (e.g. Town, " +
                      "Overworld, Dungeon, Raid1, HousingOutdoor, Eureka...), isPvp, mountsAllowed}, map {id, name}, area and subArea place names " +
                      "(the text under the minimap), instance (shard number, 0 when the zone is not instanced), duty (content finder condition " +
                      "when inside instanced content), position {x,y,z}, mapCoordinates as shown on the in-game map, current weather {id, name}, " +
                      "housing {area, ward, plot, room, division, inside, inWorkshop} when in a housing district (ward/plot are 1-based as shown in " +
                      "game), inSanctuary (resting/logout area), canFly (flying unlocked in this zone and not currently disabled), and the nearest " +
                      "aetheryte and aethernet shard on this map by map distance, with unlocked flags. Use for 'where am I' and before teleport/route advice.")]
    public LocationDto GetLocation() => Build();

    [McpResource(ResourceUri,
        Name = "Current location",
        Description = "Same JSON as get_location. Subscribers are notified when the territory or map changes.")]
    public LocationDto LocationResource() => Build();

    private unsafe LocationDto Build()
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        var territoryId = clientState.TerritoryType;
        var map = MapContext.Current(clientState, data);
        var position = player.Position;

        TerritoryDto territoryDto;
        uint contentFinderFromTerritory = 0;
        if (data.GetExcelSheet<LTerritoryType>().TryGetRow(territoryId, out var territory))
        {
            var intendedUseId = territory.TerritoryIntendedUse.RowId;
            territoryDto = new TerritoryDto(
                territoryId,
                Text(territory.PlaceName.ValueNullable?.Name),
                territory.Name.ExtractText(),
                Text(territory.PlaceNameRegion.ValueNullable?.Name),
                Text(territory.PlaceNameZone.ValueNullable?.Name),
                intendedUseId,
                intendedUseId <= byte.MaxValue ? ((CsTerritoryIntendedUse)intendedUseId).ToString() : intendedUseId.ToString(),
                territory.IsPvpZone,
                territory.Mount);
            contentFinderFromTerritory = territory.ContentFinderCondition.RowId;
        }
        else
        {
            territoryDto = new TerritoryDto(territoryId, null, null, null, null, 0, "Unknown", clientState.IsPvP, false);
        }

        IdNameDto? mapDto = null;
        if (map.Valid && data.GetExcelSheet<LMap>().TryGetRow(map.MapId, out var mapRow))
        {
            var name = Text(mapRow.PlaceName.ValueNullable?.Name);
            var sub = Text(mapRow.PlaceNameSub.ValueNullable?.Name);
            mapDto = new IdNameDto(map.MapId, sub == null ? name : $"{name} - {sub}");
        }

        string? area = null;
        string? subArea = null;
        var inSanctuary = false;
        var flyingDisabled = false;
        var territoryInfo = CsTerritoryInfo.Instance();
        if (territoryInfo != null)
        {
            area = PlaceName(territoryInfo->AreaPlaceNameId);
            subArea = PlaceName(territoryInfo->SubAreaPlaceNameId);
            inSanctuary = territoryInfo->InSanctuary;
            flyingDisabled = territoryInfo->FlyingDisabled;
        }

        IdNameDto? duty = null;
        uint cfcId = 0;
        var gameMain = CsGameMain.Instance();
        if (gameMain != null)
        {
            cfcId = gameMain->CurrentContentFinderConditionId;
        }

        if (cfcId == 0 && CsEventFramework.GetCurrentContentId() != 0)
        {
            cfcId = contentFinderFromTerritory;
        }

        if (cfcId != 0 && data.GetExcelSheet<LContentFinderCondition>().TryGetRow(cfcId, out var cfc))
        {
            duty = new IdNameDto(cfcId, Text(cfc.Name));
        }

        IdNameDto? weather = null;
        var weatherManager = CsWeatherManager.Instance();
        if (weatherManager != null)
        {
            var weatherId = weatherManager->GetCurrentWeather();
            if (weatherId != 0)
            {
                weather = new IdNameDto(weatherId, Text(data.GetExcelSheet<LWeather>().GetRowOrDefault(weatherId)?.Name));
            }
        }

        HousingDto? housing = null;
        var housingManager = CsHousingManager.Instance();
        if (housingManager != null && housingManager->CurrentTerritory != null)
        {
            var ward = housingManager->GetCurrentWard();
            var plot = housingManager->GetCurrentPlot();
            var room = housingManager->GetCurrentRoom();
            housing = new HousingDto(
                housingManager->GetCurrentHousingTerritoryType().ToString(),
                ward >= 0 ? ward + 1 : null,
                plot >= 0 ? plot + 1 : null,
                room > 0 ? room : null,
                housingManager->GetCurrentDivision(),
                housingManager->IsInside(),
                housingManager->IsInWorkshop(),
                ward,
                plot);
        }

        var playerState = CsPlayerState.Instance();
        var flyingUnlocked = playerState != null && playerState->CanFly;

        var playerMap = map.Valid
            ? (X: GameMath.WorldToMapCoordinate(position.X, map.SizeFactor, map.OffsetX),
               Y: GameMath.WorldToMapCoordinate(position.Z, map.SizeFactor, map.OffsetY))
            : (X: 0f, Y: 0f);
        var (aetheryte, shard) = map.Valid ? NearestAetherytes(map, playerMap.X, playerMap.Y) : (null, null);

        return new LocationDto(
            territoryDto,
            mapDto,
            area,
            subArea,
            clientState.Instance,
            duty,
            Snapshots.Vec(position),
            map.ToMap(position),
            weather,
            housing,
            inSanctuary,
            flyingUnlocked && !flyingDisabled && territoryDto.MountsAllowed,
            flyingUnlocked,
            aetheryte,
            shard);
    }

    private unsafe (AetheryteNearbyDto? Aetheryte, AetheryteNearbyDto? Shard) NearestAetherytes(in MapContext map, float playerX, float playerY)
    {
        if (!data.GetExcelSheet<LMap>().TryGetRow(map.MapId, out var mapRow))
        {
            return (null, null);
        }

        var markers = data.GetSubrowExcelSheet<LMapMarker>().GetRowOrDefault(mapRow.MapMarkerRange);
        if (markers == null)
        {
            return (null, null);
        }

        var aetherytes = data.GetExcelSheet<LAetheryte>();
        var uiState = CsUIState.Instance();
        AetheryteNearbyDto? bestAetheryte = null;
        AetheryteNearbyDto? bestShard = null;
        foreach (var marker in markers.Value)
        {
            // DataType 3 = aetheryte, 4 = aethernet shard; DataKey is the Aetheryte row id.
            if (marker.DataType is not (3 or 4) || !aetherytes.TryGetRow(marker.DataKey.RowId, out var row))
            {
                continue;
            }

            var x = GameMath.MarkerToMapCoordinate(marker.X, map.SizeFactor);
            var y = GameMath.MarkerToMapCoordinate(marker.Y, map.SizeFactor);
            var distance = MathF.Sqrt(((x - playerX) * (x - playerX)) + ((y - playerY) * (y - playerY)));
            var isShard = !row.IsAetheryte;
            var name = isShard ? Text(row.AethernetName.ValueNullable?.Name) : Text(row.PlaceName.ValueNullable?.Name);
            var candidate = new AetheryteNearbyDto(
                row.RowId,
                name,
                isShard,
                uiState != null && uiState->IsAetheryteUnlocked(row.RowId),
                new MapCoordsDto(GameMath.DisplayCoordinate(x), GameMath.DisplayCoordinate(y)),
                GameMath.Round(distance, 1));

            if (isShard)
            {
                if (bestShard == null || candidate.MapDistance < bestShard.MapDistance)
                {
                    bestShard = candidate;
                }
            }
            else if (bestAetheryte == null || candidate.MapDistance < bestAetheryte.MapDistance)
            {
                bestAetheryte = candidate;
            }
        }

        return (bestAetheryte, bestShard);
    }

    private string? PlaceName(uint id) =>
        id == 0 ? null : Text(data.GetExcelSheet<LPlaceName>().GetRowOrDefault(id)?.Name);

    private static string? Text(Lumina.Text.ReadOnly.ReadOnlySeString? value)
    {
        if (value is not { } text)
        {
            return null;
        }

        var extracted = text.ExtractText();
        return string.IsNullOrEmpty(extracted) ? null : extracted;
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        try
        {
            notifier.ResourceUpdated(ResourceUri);
        }
        catch (Exception ex)
        {
            log.Error(ex, "xiv-mcp: location notification failed");
        }
    }

    private void OnMapChanged(uint mapId) => OnTerritoryChanged(mapId);

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        clientState.MapIdChanged -= OnMapChanged;
    }
}

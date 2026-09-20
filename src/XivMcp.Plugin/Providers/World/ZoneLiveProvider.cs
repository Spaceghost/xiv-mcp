using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Character;

namespace XivMcp.Plugin.Providers.World;

/// <summary>
/// "What is going on in this zone right now", composed from the existing location, FATE and aetheryte tools: this class
/// owns private instances of those providers and calls their tool methods, so it reads game memory exactly as they do.
/// </summary>
[McpProvider("world")]
public sealed class ZoneLiveProvider : IDisposable
{
    public const string ResourceUri = "ffxiv://zone/current";

    private readonly IClientState clientState;
    private readonly IMcpNotifier notifier;
    private readonly IPluginLog log;
    private readonly LocationProvider location;
    private readonly FateProvider fates;
    private readonly AetheryteProvider aetherytes;

    public ZoneLiveProvider(
        IObjectTable objects,
        IClientState clientState,
        IPlayerState playerState,
        IFateTable fateTable,
        IDataManager data,
        IMcpNotifier notifier,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.notifier = notifier;
        this.log = log;
        // The inner location provider only serves reads; ffxiv://location is announced by the registered instance.
        location = new LocationProvider(objects, clientState, data, SilentNotifier.Instance, log);
        fates = new FateProvider(fateTable, objects, clientState, data);
        aetherytes = new AetheryteProvider(objects, clientState, playerState, data);
        clientState.TerritoryChanged += OnTerritoryChanged;
    }

    [McpTool("get_zone_live",
        Sources =
        [
            "dalamud:IClientState", "dalamud:IFateTable", "dalamud:IAetheryteList", "client:AgentMap", "client:TerritoryInfo",
            "client:WeatherManager", "client:HousingManager", "lumina:TerritoryType",
        ],
        Title = "Get live zone summary",
        Description =
            "What is going on in the zone the player is standing in, right now, in one call: territoryId, zone and region names, " +
            "intendedUse (Town, Overworld, Dungeon, HousingOutdoor, ...), map, area/subArea place names, instance (the instance number " +
            "when the zone is split into instances; omitted otherwise), current weather, the player's map coordinates, inSanctuary, " +
            "inDuty + duty, inHousingDistrict + housing {area, ward, plot, room, inside}, isPvp, canFly, fates {active count, " +
            "currentFateId, nearest topFates with level, state, progressPercent, timeRemainingSeconds, hasBonus, mapCoordinates, distance}, " +
            "and the attuned aetherytes in this zone with teleport cost, plus the nearest aetheryte. A part that cannot be read is listed " +
            "under unavailable instead of failing the call. This is the digest; use get_location, list_fates, list_aetherytes, " +
            "get_weather_forecast or get_duty_state for the full detail of one part. Also served as resource ffxiv://zone/current.",
        RequiresLogin = true)]
    public ZoneLiveDto GetZoneLive(
        [McpParam("How many of the nearest active FATEs to include (0-25).", Minimum = 0, Maximum = 25)] int topFates = 5) => Build(topFates);

    [McpResource(ResourceUri,
        Name = "Current zone (live)",
        Description = "Same JSON as get_zone_live with the five nearest FATEs. Subscribers are notified when the territory changes.")]
    public ZoneLiveDto ZoneResource() => Build(5);

    private ZoneLiveDto Build(int topFates)
    {
        var here = location.GetLocation();
        var unavailable = new List<string>();
        var fateList = Section("fates", unavailable, () => fates.ListFates(100));
        var aetheryteList = Section("aetherytes", unavailable, () => aetherytes.ListAetherytes(null, 500, 0));
        return ZoneLiveShaper.Build(here, fateList, aetheryteList, topFates, unavailable);
    }

    private static T? Section<T>(string name, List<string> unavailable, Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (McpToolException ex)
        {
            unavailable.Add($"{name}: {ex.Message}");
            return null;
        }
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        try
        {
            notifier.ResourceUpdated(ResourceUri);
        }
        catch (Exception ex)
        {
            log.Error(ex, "xiv-mcp: zone notification failed");
        }
    }

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        location.Dispose();
    }
}

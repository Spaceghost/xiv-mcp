using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.Gui.Toast;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using XivMcp.Plugin.Objectives;
using AgentMapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;
using CsWeatherManager = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager;
using LMap = Lumina.Excel.Sheets.Map;
using LTerritoryType = Lumina.Excel.Sheets.TerritoryType;
using LWeather = Lumina.Excel.Sheets.Weather;
using LWeatherRate = Lumina.Excel.Sheets.WeatherRate;

namespace XivMcp.Plugin.Services;

/// <summary>An objective with its live condition state.</summary>
public sealed record ObjectiveView(Objective Objective, ObjectiveStatus Status);

/// <summary>
/// Game-side glue for objectives: snapshots the game (zone, position, weather) on the framework thread, evaluates
/// every objective about once a second for the overlay, places map flags and shows the game's own toasts. The rules
/// themselves live in <see cref="ObjectiveEvaluator"/>.
/// </summary>
public sealed unsafe class ObjectiveTracker : IDisposable
{
    private readonly ObjectiveStore store;
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly IDataManager data;
    private readonly IToastGui toasts;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<uint, ZoneInfo?> zones = new();
    private readonly HashSet<string> readyIds = new(StringComparer.Ordinal);
    private IReadOnlyList<ObjectiveView> latest = [];
    private DateTime nextEvaluation;
    private volatile bool dirty = true;

    public ObjectiveTracker(
        ObjectiveStore store, Configuration config, IFramework framework, IClientState clientState, IObjectTable objects,
        IDataManager data, IToastGui toasts, IPluginLog log)
    {
        this.store = store;
        this.config = config;
        this.framework = framework;
        this.clientState = clientState;
        this.objects = objects;
        this.data = data;
        this.toasts = toasts;
        this.log = log;
        store.Changed += OnStoreChanged;
        store.Completed += OnCompleted;
        framework.Update += OnUpdate;
    }

    /// <summary>The latest evaluation (framework thread, at most a second old). Safe to read from any thread.</summary>
    public IReadOnlyList<ObjectiveView> Latest => latest;

    public ObjectiveStore Store => store;

    /// <summary>Evaluates every objective now. Framework thread only.</summary>
    public IReadOnlyList<ObjectiveView> EvaluateNow()
    {
        var ctx = Snapshot();
        var views = store.Snapshot().Select(o => new ObjectiveView(o, ObjectiveEvaluator.Evaluate(o, ctx))).ToArray();
        latest = views;
        return views;
    }

    public ObjectiveView? Find(string id) =>
        latest.FirstOrDefault(v => v.Objective.Id == ObjectiveText.NormalizeId(id));

    /// <summary>Places the map flag on the objective's spot and optionally opens the map. Framework thread only.</summary>
    public string PlaceFlag(Objective objective, bool openMap)
    {
        if (!objective.HasSpot)
            throw new InvalidOperationException($"'{objective.Title}' has no zone and map coordinates.");
        var territoryId = objective.TerritoryId!.Value;
        if (!data.GetExcelSheet<LTerritoryType>().TryGetRow(territoryId, out var territory) || territory.Map.RowId == 0)
            throw new InvalidOperationException($"Territory {territoryId} has no map.");
        var zone = Zone(territoryId) ?? throw new InvalidOperationException($"Territory {territoryId} has no map.");
        var agent = AgentMap.Instance();
        if (agent == null)
            throw new InvalidOperationException("The map is not available right now.");

        var world = zone.MapToWorld(objective.MapX!.Value, objective.MapY!.Value);
        var mapId = territoryId == clientState.TerritoryType && clientState.MapId != 0 ? clientState.MapId : territory.Map.RowId;
        agent->SetFlagMapMarker(territoryId, mapId, world.X, world.Y);
        if (openMap)
            agent->OpenMap(mapId, territoryId, objective.Title, AgentMapType.FlagMarker);
        return $"{zone.Name ?? objective.Zone} ({objective.MapX:0.0}, {objective.MapY:0.0})";
    }

    /// <summary>Zone data for conditions and flags, cached per territory. Any thread.</summary>
    public ZoneInfo? Zone(uint territoryId) => zones.GetOrAdd(territoryId, LoadZone);

    private ObjectiveContext Snapshot()
    {
        var territory = clientState.IsLoggedIn ? (uint)clientState.TerritoryType : 0;
        Vector3? position = objects.LocalPlayer?.Position;
        IReadOnlyList<string>? weatherNames = null;
        if (territory != 0)
        {
            var manager = CsWeatherManager.Instance();
            var weatherId = manager != null ? manager->GetCurrentWeather() : (byte)0;
            if (weatherId != 0)
                weatherNames = WeatherNames(weatherId);
        }

        return new ObjectiveContext(store.Now, territory, position, weatherNames, Zone);
    }

    private ZoneInfo? LoadZone(uint territoryId)
    {
        try
        {
            if (!data.GetExcelSheet<LTerritoryType>().TryGetRow(territoryId, out var territory))
                return null;
            ushort sizeFactor = 100;
            short offsetX = 0, offsetY = 0;
            if (data.GetExcelSheet<LMap>().TryGetRow(territory.Map.RowId, out var map))
            {
                sizeFactor = map.SizeFactor == 0 ? (ushort)100 : map.SizeFactor;
                offsetX = map.OffsetX;
                offsetY = map.OffsetY;
            }

            WeatherTable? table = null;
            var rateId = territory.WeatherRate.RowId;
            if (rateId != 0 && data.GetExcelSheet<LWeatherRate>().TryGetRow(rateId, out var rate))
            {
                var slots = new List<WeatherSlot>();
                for (var i = 0; i < 8; i++)
                    slots.Add(new WeatherSlot(rate.Weather[i].RowId, rate.Rate[i], WeatherNames(rate.Weather[i].RowId)));
                if (slots.Sum(s => s.Rate) > 0)
                    table = new WeatherTable(slots);
            }

            var name = territory.PlaceName.ValueNullable?.Name.ExtractText();
            return new ZoneInfo(territoryId, string.IsNullOrEmpty(name) ? null : name, sizeFactor, offsetX, offsetY, table);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Objective zone data for territory {Territory} unavailable", territoryId);
            return null;
        }
    }

    /// <summary>Weather names in the client language, then English (packs are usually written in English).</summary>
    private IReadOnlyList<string> WeatherNames(uint weatherId)
    {
        var names = new List<string>(2);
        foreach (var language in new ClientLanguage?[] { null, ClientLanguage.English })
        {
            var name = data.GetExcelSheet<LWeather>(language).GetRowOrDefault(weatherId)?.Name.ExtractText();
            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        return names;
    }

    private void OnUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow;
        if (!dirty && now < nextEvaluation)
            return;
        dirty = false;
        nextEvaluation = now + TimeSpan.FromSeconds(1);
        try
        {
            var views = EvaluateNow();
            AnnounceReady(views);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Objective evaluation failed");
        }
    }

    private void AnnounceReady(IReadOnlyList<ObjectiveView> views)
    {
        var ready = views.Where(v => v.Status.Ready).Select(v => v.Objective.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var view in views)
        {
            if (view.Status.Ready && !readyIds.Contains(view.Objective.Id) && config.NotifyObjectiveReady && config.ShowObjectives)
                toasts.ShowNormal($"{view.Objective.Title}: ready now");
        }

        readyIds.Clear();
        readyIds.UnionWith(ready);
    }

    private void OnStoreChanged() => dirty = true;

    private void OnCompleted(Objective objective)
    {
        // Store events arrive on whichever thread changed the store; toasts must be shown on the framework thread.
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                toasts.ShowQuest($"{objective.Title}  complete", new QuestToastOptions { DisplayCheckmark = true, PlaySound = true });
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Objective completion toast failed");
            }
        });
    }

    /// <summary>Shows the game's quest-accepted style toast for a new objective. Any thread.</summary>
    public void AnnounceNew(Objective objective)
    {
        if (!config.ShowObjectives)
            return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                toasts.ShowQuest($"New objective: {objective.Title}", new QuestToastOptions { PlaySound = true });
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Objective toast failed");
            }
        });
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        store.Changed -= OnStoreChanged;
        store.Completed -= OnCompleted;
    }
}

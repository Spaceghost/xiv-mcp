using XivMcp.Core;
using XivMcp.Plugin.Objectives;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Providers.Objectives;

public sealed record ObjectiveStepDto(int Index, string Text, bool Done);

public sealed record ObjectiveStatusDto(
    bool Ready,
    string Summary,
    bool WindowOpen,
    bool? InZone,
    bool? NearSpot,
    double? DistanceYalms,
    bool? TimeOk,
    bool? WeatherOk,
    string? CurrentWeather,
    DateTimeOffset? NextWindowStartUtc,
    DateTimeOffset? NextWindowEndUtc,
    double? MinutesUntilNextWindow,
    string? Problem);

public sealed record ObjectiveDto(
    string Id,
    string Title,
    bool Completed,
    int CurrentStep,
    string? CurrentStepText,
    int StepsDone,
    List<ObjectiveStepDto> Steps,
    string? Zone,
    uint? TerritoryId,
    float? MapX,
    float? MapY,
    string? Spot,
    string? Description,
    string? Note,
    string? EorzeaTime,
    IReadOnlyList<string> Weather,
    float Radius,
    string? Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    ObjectiveStatusDto? Status);

public sealed record ObjectiveListDto(int Count, int Active, int Ready, List<ObjectiveDto> Objectives);

public sealed record ClearObjectivesResult(int Removed, int Remaining);

public sealed record LoadPackResult(string? Title, int Loaded, List<string> Ids, List<string> Errors, int Total);

/// <summary>
/// Custom objectives ("quests") shown in game under the Duty List: agents post them, report progress, and the plugin
/// evaluates their conditions live. They are plugin state only; the game's Journal is server-side and never touched.
/// </summary>
[McpProvider("objectives")]
public sealed class ObjectivesProvider : IDisposable
{
    public const string ResourceUri = "ffxiv://objectives";

    private readonly ObjectiveTracker tracker;
    private readonly IMcpNotifier notifier;

    public ObjectivesProvider(ObjectiveTracker tracker, IMcpNotifier notifier)
    {
        this.tracker = tracker;
        this.notifier = notifier;
        tracker.Store.Changed += OnChanged;
    }

    private ObjectiveStore Store => tracker.Store;

    [McpTool("post_objective",
        Sources = ["xivmcp:objectives"],
        Title = "Post a custom objective (quest) to the player's game",
        Description =
            "Creates or replaces (same id) a custom objective that the player sees in game like a tracked quest: title and current step " +
            "under the Duty List, live 'ready now' / 'next window in N min' state, click to place the map flag. It is NOT a real Journal " +
            "quest (those are server-side). Give a location with territoryId (TerritoryType row id, e.g. 137 Eastern La Noscea) plus map " +
            "x/y as the game displays them; optional conditions: eorzeaTime (daily Eorzea window \"HH:MM-HH:MM\", may wrap midnight, e.g. " +
            "\"21:00-03:00\"), weather (names such as \"Clear Skies\"; any listed one matches), radius (yalms from the spot, default 30). " +
            "Re-posting with identical steps keeps the player's progress; changed steps reset it. Advance it with update_objective. " +
            "Returns the stored objective with its live status.",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false)]
    public async Task<ObjectiveDto> PostObjective(
        [McpParam("Stable id, e.g. \"hero-costa-night\" (lowercased; letters, digits, '-', '_', '.'). Max 64 chars.")] string id,
        [McpParam("Quest title shown in game. Max 120 chars.")] string title,
        [McpParam("Ordered steps; the first not-done one is shown as the current step. Max 30, 300 chars each.")] string[]? steps = null,
        [McpParam("TerritoryType row id of the zone (see get_location / search_sheet TerritoryType).", Minimum = 1)] uint? territoryId = null,
        [McpParam("Map X coordinate as shown in game. Needs y and territoryId.")] float? x = null,
        [McpParam("Map Y coordinate as shown in game. Needs x and territoryId.")] float? y = null,
        [McpParam("Zone name for display, e.g. \"Eastern La Noscea\".")] string? zone = null,
        [McpParam("Short description of the spot, e.g. \"the beach below the villa\".")] string? spot = null,
        [McpParam("Longer description shown in the tooltip. Max 2000 chars.")] string? description = null,
        [McpParam("Daily Eorzea time window \"HH:MM-HH:MM\" (wraps past midnight) or \"any\".")] string? eorzeaTime = null,
        [McpParam("Acceptable weather names in the game's wording, e.g. [\"Clear Skies\", \"Fair Skies\"]; omit or [\"any\"] for any.")] string[]? weather = null,
        [McpParam("Distance from the spot (yalms, ground plane) that counts as there. Default 30.", Minimum = 1, Maximum = 1000)] float? radius = null,
        ToolContext? ctx = null)
    {
        Objective objective;
        try
        {
            objective = ObjectiveFactory.Create(new ObjectiveDraft
            {
                Id = id,
                Title = title,
                Steps = steps ?? [],
                TerritoryId = territoryId,
                MapX = x,
                MapY = y,
                Zone = zone,
                Spot = spot,
                Description = description,
                EorzeaTime = eorzeaTime,
                Weather = weather ?? [],
                Radius = radius,
                Source = ctx?.ClientName is { Length: > 0 } client ? client : "mcp",
            }, Store.Now);
        }
        catch (ArgumentException ex)
        {
            throw new McpToolException(ex.Message);
        }

        var isNew = Store.Get(objective.Id) is null;
        Objective stored;
        try
        {
            stored = Store.Upsert(objective);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpToolException(ex.Message);
        }

        if (isNew)
            tracker.AnnounceNew(stored);
        return await WithStatus(stored, ctx).ConfigureAwait(false);
    }

    [McpTool("update_objective",
        Sources = ["xivmcp:objectives"],
        Title = "Update a custom objective's progress",
        Description =
            "Reports progress on an objective posted with post_objective or loaded from a pack: advance=true marks the current step done " +
            "(after the last step the objective completes), step=N makes step N (0-based) current with every earlier step done, " +
            "complete=true completes it (the player sees the game's quest-complete toast), complete=false reopens it, note sets a short " +
            "line shown under the step (empty string clears it). Returns the objective with its live status.",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false)]
    public async Task<ObjectiveDto> UpdateObjective(
        [McpParam("Objective id.")] string id,
        [McpParam("Mark the current step done.")] bool advance = false,
        [McpParam("Make this 0-based step current (steps before it done). Equal to the step count means all done.", Minimum = 0, Maximum = 30)] int? step = null,
        [McpParam("true completes the objective, false reopens it.")] bool? complete = null,
        [McpParam("Short note shown under the current step; empty string clears it. Max 300 chars.")] string? note = null,
        ToolContext? ctx = null)
    {
        if (!advance && step is null && complete is null && note is null)
            throw new McpToolException("Nothing to update: pass advance, step, complete or note.");
        if (advance && step is not null)
            throw new McpToolException("Pass either advance or step, not both.");

        Objective? updated;
        try
        {
            updated = Store.Update(id, o =>
            {
                var now = Store.Now;
                if (step is { } s)
                    o = ObjectiveFactory.SetCurrentStep(o, s, now);
                if (advance)
                    o = ObjectiveFactory.Advance(o, now);
                if (complete is { } c)
                    o = ObjectiveFactory.Complete(o, c, now);
                if (note is not null)
                    o = o with { Note = ObjectiveText.CleanOrNull(note, Objective.MaxStepLength), UpdatedAt = now };
                return o;
            });
        }
        catch (ArgumentException ex)
        {
            throw new McpToolException(ex.Message);
        }

        if (updated is null)
            throw new McpToolException($"No objective '{id}'. list_objectives shows the ids.");
        return await WithStatus(updated, ctx).ConfigureAwait(false);
    }

    [McpTool("list_objectives",
        Sources = ["xivmcp:objectives", "client:WeatherManager"],
        Title = "List custom objectives with live status",
        Description =
            "Every custom objective in insertion order with steps, location, conditions and live status: ready (in the zone, within the " +
            "radius, inside the Eorzea time window and weather), summary (the line the player sees, e.g. \"Next window in 14 min\"), " +
            "windowOpen (time and weather match now, wherever the player is), inZone, nearSpot, distanceYalms, currentWeather, " +
            "nextWindowStartUtc/EndUtc (from the game's weather algorithm; while open, End is when it closes) and problem (e.g. a weather the " +
            "zone never has). Works at the title screen with location checks false.",
        RequiresLogin = false)]
    public ObjectiveListDto ListObjectives(
        [McpParam("Include completed objectives.")] bool includeCompleted = true) => BuildList(includeCompleted);

    [McpTool("clear_objectives",
        Sources = ["xivmcp:objectives"],
        Title = "Remove custom objectives",
        Description =
            "Removes one objective (id), every completed one (completedOnly=true) or all of them (no arguments). " +
            "Prefer update_objective complete=true so the player sees the completion; clear afterwards. Returns removed and remaining counts.",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false)]
    public ClearObjectivesResult ClearObjectives(
        [McpParam("Objective id; omit for all.")] string? id = null,
        [McpParam("Only remove completed objectives.")] bool completedOnly = false)
    {
        var removed = Store.Remove(id, completedOnly);
        return new ClearObjectivesResult(removed, Store.Count);
    }

    [McpTool("load_objective_pack",
        Sources = ["xivmcp:objectives", "file:objective pack"],
        Title = "Load a quest pack",
        Description =
            "Loads many objectives at once from a quest pack: pass the JSON text (json) or a file path on the player's machine (path; " +
            "host paths such as /home/me/pack.json or ~/pack.json are mapped to Wine's Z: drive). Shape: {\"quests\": [{id, name, zone, " +
            "territoryId, spot, map: {x, y}, eorzea_time, weather: [...], setup: [...], capture}]} (setup and capture become steps; or give " +
            "steps directly), {\"objectives\": [...]} or a bare array. Entries with the same id replace existing ones, keeping progress when " +
            "the steps are unchanged. Invalid entries are skipped and reported in errors.",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false)]
    public LoadPackResult LoadObjectivePack(
        [McpParam("Pack JSON text.")] string? json = null,
        [McpParam("Path to a .json pack file on the player's machine.")] string? path = null)
    {
        if ((json is null) == (path is null))
            throw new McpToolException("Pass either json or path.");
        try
        {
            return ObjectiveCommands.LoadPack(Store, json, path);
        }
        catch (ArgumentException ex)
        {
            throw new McpToolException(ex.Message);
        }
    }

    [McpResource(ResourceUri,
        Name = "Custom objectives",
        Description = "Same JSON as list_objectives (with completed ones). Subscribe for notifications/resources/updated when objectives change.",
        RequiresLogin = false)]
    public ObjectiveListDto ReadObjectives() => BuildList(true);

    private ObjectiveListDto BuildList(bool includeCompleted)
    {
        var views = tracker.EvaluateNow();
        var items = views.Where(v => includeCompleted || !v.Objective.Completed).Select(v => ToDto(v.Objective, v.Status, Store.Now)).ToList();
        return new ObjectiveListDto(items.Count, views.Count(v => !v.Objective.Completed), views.Count(v => v.Status.Ready), items);
    }

    private async Task<ObjectiveDto> WithStatus(Objective objective, ToolContext? ctx)
    {
        ObjectiveStatus? status = null;
        if (ctx is not null)
        {
            var views = await ctx.Game.InvokeAsync(tracker.EvaluateNow, ctx.CancellationToken).ConfigureAwait(false);
            status = views.FirstOrDefault(v => v.Objective.Id == objective.Id)?.Status;
        }

        return ToDto(objective, status, Store.Now);
    }

    public static ObjectiveDto ToDto(Objective o, ObjectiveStatus? s, DateTimeOffset now) => new(
        o.Id,
        o.Title,
        o.Completed,
        o.CurrentStepIndex,
        o.CurrentStepText,
        o.StepsDone,
        o.Steps.Select((step, i) => new ObjectiveStepDto(i, step.Text, step.Done)).ToList(),
        o.Zone,
        o.TerritoryId,
        o.MapX,
        o.MapY,
        o.Spot,
        o.Description,
        o.Note,
        o.Conditions.EorzeaTime,
        o.Conditions.Weather,
        o.Conditions.Radius,
        o.Source,
        o.CreatedAt,
        o.UpdatedAt,
        o.CompletedAt,
        s is null
            ? null
            : new ObjectiveStatusDto(
                s.Ready,
                s.Summary,
                s.WindowOpen,
                s.InZone,
                s.NearSpot,
                s.DistanceYalms,
                s.TimeOk,
                s.WeatherOk,
                s.CurrentWeather,
                s.NextWindowStart,
                s.NextWindowEnd,
                s.NextWindowStart is { } start && start > now ? Math.Round((start - now).TotalMinutes, 1) : s.NextWindowStart is null ? null : 0,
                s.Problem));

    private void OnChanged()
    {
        try
        {
            notifier.ResourceUpdated(ResourceUri);
        }
        catch
        {
            // Never throw from an event handler.
        }
    }

    public void Dispose() => tracker.Store.Changed -= OnChanged;
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Events;

/// <summary>
/// A bounded stream of "what just happened" — zone changes, duty state, combat, party changes, inventory
/// changes, level ups and login/logout — that a client can poll with a cursor or subscribe to as a resource.
/// The buffer is capped, so an agent that stops reading cannot grow the plugin's memory; the result says when
/// events were dropped.
/// </summary>
[McpProvider("events")]
public sealed class EventsProvider : IDisposable
{
    /// <summary>Resource clients subscribe to for "something happened".</summary>
    public const string EventsUri = "ffxiv://events";

    /// <summary>Condition flags worth an event; everything else is noise.</summary>
    private static readonly ConditionFlag[] WatchedConditions =
    [
        ConditionFlag.InCombat,
        ConditionFlag.BoundByDuty,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.Occupied,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.Fishing,
        ConditionFlag.Mounted,
        ConditionFlag.BetweenAreas,
    ];

    private static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(500);

    private readonly EventStream stream = new(500);
    private readonly IClientState clientState;
    private readonly IDutyState dutyState;
    private readonly ICondition condition;
    private readonly IGameInventory gameInventory;
    private readonly IPartyList party;
    private readonly IFramework framework;
    private readonly IMcpNotifier notifier;
    private readonly IPluginLog log;
    // GameDataIndex.For hands back the per-IDataManager shared index; this provider borrows it and
    // must not dispose it, so CA2213's "never disposed" is the correct behaviour here.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Shared GameDataIndex owned by GameDataIndex.For, not by this provider.")]
    private readonly GameDataIndex index;

    private readonly object partyGate = new();
    private int lastPartySize = -1;
    private DateTime lastPartyCheck = DateTime.MinValue;
    private DateTime lastNotify = DateTime.MinValue;

    public EventsProvider(
        IClientState clientState,
        IDutyState dutyState,
        ICondition condition,
        IGameInventory gameInventory,
        IPartyList party,
        IFramework framework,
        IDataManager data,
        IMcpNotifier notifier,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.dutyState = dutyState;
        this.condition = condition;
        this.gameInventory = gameInventory;
        this.party = party;
        this.framework = framework;
        this.notifier = notifier;
        this.log = log;
        index = GameDataIndex.For(data);

        stream.Appended += OnAppended;
        clientState.TerritoryChanged += OnTerritoryChanged;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
        clientState.LevelChanged += OnLevelChanged;
        clientState.ClassJobChanged += OnClassJobChanged;
        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyCompleted += OnDutyCompleted;
        dutyState.DutyWiped += OnDutyWiped;
        dutyState.DutyRecommenced += OnDutyRecommenced;
        condition.ConditionChange += OnConditionChange;
        gameInventory.InventoryChanged += OnInventoryChanged;
        framework.Update += OnFrameworkUpdate;
    }

    public sealed record EventDto(long Seq, DateTimeOffset At, string Kind, string Summary, JsonObject? Data);

    public sealed record EventsDto(
        List<EventDto> Events,
        long Cursor,
        int Returned,
        int Remaining,
        bool MissedEvents,
        int Buffered,
        int Capacity,
        long DroppedTotal,
        string Note);

    public sealed record EventKindDto(string Kind, int Buffered, string Description);

    public sealed record EventKindsDto(long Cursor, int Buffered, int Capacity, List<EventKindDto> Kinds);

    [McpTool("get_events",
        Sources = ["xivmcp:event stream"],
        Title = "Get recent game events",
        GameThread = false,
        RequiresLogin = false,
        Description =
            "Polls the plugin's bounded stream of game events and returns them oldest first. Pass afterSeq with the cursor from the " +
            "previous call to get only what is new (0 or omitted returns everything still buffered). Kinds: zone (territory change), " +
            "duty (started/completed/wiped/recommenced), combat (entering and leaving combat), condition (cutscene, crafting, gathering, " +
            "mounted, ...), party (size changed), inventory (items added/removed/moved), level (level up), job (class/job change), " +
            "session (login/logout). Each event has seq, at, kind, summary and sometimes data. " +
            "The result carries cursor (pass it back), remaining (matches not returned yet), missedEvents=true when the buffer had " +
            "already discarded events past your cursor, and buffered/capacity/droppedTotal. " +
            "Subscribe to the ffxiv://events resource to be told when something new arrives instead of polling tightly.")]
    public EventsDto GetEvents(
        [McpParam("Return only events after this sequence number. Use the cursor from the previous call.", Minimum = 0)] long afterSeq = 0,
        [McpParam("Only these kinds, e.g. [\"duty\", \"combat\"]. Omit for all.")] string[]? kinds = null,
        [McpParam("Maximum events to return (1-500).", Minimum = 1, Maximum = 500)] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        var filter = kinds?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToArray();
        var (events, cursor, remaining, gap) = stream.Since(Math.Max(0, afterSeq), filter, limit);

        return new EventsDto(
            events.Select(e => new EventDto(e.Seq, e.At, e.Kind, e.Summary, e.Data)).ToList(),
            cursor,
            events.Count,
            remaining,
            gap,
            stream.Count,
            stream.Capacity,
            stream.DroppedTotal,
            gap
                ? "Events between your cursor and the oldest buffered one were dropped; re-read the state you care about with the normal tools."
                : "Events are captured only while the plugin is loaded; nothing before that is available.");
    }

    [McpTool("list_event_kinds",
        Sources = ["xivmcp:event stream"],
        Title = "List event kinds",
        GameThread = false,
        RequiresLogin = false,
        Description =
            "The event kinds currently in the buffer with how many of each, plus the current cursor and the buffer size. " +
            "Use it to see what the stream is actually producing before filtering get_events by kind.")]
    public EventKindsDto ListEventKinds() =>
        new(stream.NextSequence - 1,
            stream.Count,
            stream.Capacity,
            stream.Kinds().Select(k => new EventKindDto(k.Kind, k.Count, Describe(k.Kind))).ToList());

    [McpResource(EventsUri,
        Name = "Game events",
        Description = "The newest buffered game events (same shape as get_events with no cursor). Subscribe to be told when something happens.",
        GameThread = false,
        RequiresLogin = false)]
    public EventsDto EventsResource() => GetEvents(0, null, 100);

    [McpResourceTemplate("ffxiv://events/{kind}",
        Name = "Game events of one kind",
        Description = "The newest buffered events of one kind (zone, duty, combat, condition, party, inventory, level, job, session).",
        GameThread = false,
        RequiresLogin = false)]
    public EventsDto EventsByKindResource(string kind) => GetEvents(0, [kind], 100);

    public void Dispose()
    {
        try
        {
            stream.Appended -= OnAppended;
            clientState.TerritoryChanged -= OnTerritoryChanged;
            clientState.Login -= OnLogin;
            clientState.Logout -= OnLogout;
            clientState.LevelChanged -= OnLevelChanged;
            clientState.ClassJobChanged -= OnClassJobChanged;
            dutyState.DutyStarted -= OnDutyStarted;
            dutyState.DutyCompleted -= OnDutyCompleted;
            dutyState.DutyWiped -= OnDutyWiped;
            dutyState.DutyRecommenced -= OnDutyRecommenced;
            condition.ConditionChange -= OnConditionChange;
            gameInventory.InventoryChanged -= OnInventoryChanged;
            framework.Update -= OnFrameworkUpdate;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Unsubscribing the event stream failed");
        }
    }

    internal static string Describe(string kind) => kind switch
    {
        "zone" => "The player moved to another territory.",
        "duty" => "Instanced content started, completed, wiped or recommenced.",
        "combat" => "The player entered or left combat.",
        "condition" => "A watched condition flag changed (cutscene, crafting, gathering, mounted, ...).",
        "party" => "The party size changed.",
        "inventory" => "Items were added, removed or moved.",
        "level" => "A class or job gained a level.",
        "job" => "The player changed class or job.",
        "session" => "The character logged in or out.",
        _ => "Other plugin event.",
    };

    private void OnAppended(GameEvent entry)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - lastNotify < NotifyInterval)
            {
                return;
            }

            lastNotify = now;
            notifier.ResourceUpdated(EventsUri);
            notifier.ResourceUpdated($"ffxiv://events/{entry.Kind}");
        }
        catch
        {
            // Notifications are best effort.
        }
    }

    private void OnTerritoryChanged(uint territoryId) => Safe(() =>
    {
        var name = index.TerritoryName(territoryId);
        stream.Append("zone", name is null ? $"Entered territory {territoryId}." : $"Entered {name}.",
            new JsonObject { ["territoryId"] = territoryId, ["zone"] = name });
    });

    private void OnLogin() => Safe(() => stream.Append("session", "Logged in.", null));

    private void OnLogout(int type, int code) => Safe(() =>
        stream.Append("session", "Logged out.", new JsonObject { ["type"] = type, ["code"] = code }));

    private void OnLevelChanged(uint classJobId, uint level) => Safe(() =>
    {
        var job = index.ClassJob(classJobId);
        stream.Append("level", $"{job?.Name ?? $"Class/job {classJobId}"} reached level {level}.",
            new JsonObject { ["classJobId"] = classJobId, ["job"] = job?.Abbreviation, ["level"] = level });
    });

    private void OnClassJobChanged(uint classJobId) => Safe(() =>
    {
        var job = index.ClassJob(classJobId);
        stream.Append("job", $"Switched to {job?.Name ?? $"class/job {classJobId}"}.",
            new JsonObject { ["classJobId"] = classJobId, ["job"] = job?.Abbreviation });
    });

    private void OnDutyStarted(IDutyStateEventArgs args) => Safe(() => Duty("started", args.TerritoryType.RowId));

    private void OnDutyCompleted(IDutyStateEventArgs args) => Safe(() => Duty("completed", args.TerritoryType.RowId));

    private void OnDutyWiped(IDutyStateEventArgs args) => Safe(() => Duty("wiped", args.TerritoryType.RowId));

    private void OnDutyRecommenced(IDutyStateEventArgs args) => Safe(() => Duty("recommenced", args.TerritoryType.RowId));

    private void Duty(string what, uint territoryId)
    {
        var name = index.TerritoryName(territoryId);
        stream.Append("duty", $"Duty {what}{(name is null ? "" : $" in {name}")}.",
            new JsonObject { ["event"] = what, ["territoryId"] = territoryId, ["zone"] = name });
    }

    private void OnConditionChange(ConditionFlag flag, bool value) => Safe(() =>
    {
        if (!WatchedConditions.Contains(flag))
        {
            return;
        }

        if (flag == ConditionFlag.InCombat)
        {
            stream.Append("combat", value ? "Entered combat." : "Left combat.",
                new JsonObject { ["inCombat"] = value });
            return;
        }

        stream.Append("condition", $"{flag} {(value ? "started" : "ended")}.",
            new JsonObject { ["flag"] = flag.ToString(), ["value"] = value });
    });

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> args) => Safe(() =>
    {
        if (args.Count == 0)
        {
            return;
        }

        var added = 0;
        var removed = 0;
        var other = 0;
        string? firstItem = null;
        foreach (var change in args)
        {
            switch (change.Type)
            {
                case GameInventoryEvent.Added:
                    added++;
                    firstItem ??= index.ItemName(change.Item.ItemId);
                    break;
                case GameInventoryEvent.Removed:
                    removed++;
                    firstItem ??= index.ItemName(change.Item.ItemId);
                    break;
                default:
                    other++;
                    break;
            }
        }

        var summary = added + removed > 0
            ? $"Inventory: {added} added, {removed} removed{(firstItem is null ? "" : $" (e.g. {firstItem})")}."
            : $"Inventory: {other} change(s).";
        stream.Append("inventory", summary, new JsonObject
        {
            ["added"] = added,
            ["removed"] = removed,
            ["other"] = other,
            ["changes"] = args.Count,
        });
    });

    private void OnFrameworkUpdate(IFramework source) => Safe(() =>
    {
        var now = DateTime.UtcNow;
        if (now - lastPartyCheck < TimeSpan.FromSeconds(1))
        {
            return;
        }

        lastPartyCheck = now;
        var size = party.Length;
        lock (partyGate)
        {
            if (lastPartySize < 0)
            {
                lastPartySize = size;
                return;
            }

            if (size == lastPartySize)
            {
                return;
            }

            var previous = lastPartySize;
            lastPartySize = size;
            stream.Append("party", $"Party size changed from {previous} to {size}.",
                new JsonObject { ["previous"] = previous, ["size"] = size });
        }
    });

    private void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Event stream handler failed");
        }
    }
}

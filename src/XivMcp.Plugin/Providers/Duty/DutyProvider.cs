using System.Globalization;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Character;
using XivMcp.Plugin.Util;
using CsEventFramework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework;
using CsGameMain = FFXIVClientStructs.FFXIV.Client.Game.GameMain;
using LContentFinderCondition = Lumina.Excel.Sheets.ContentFinderCondition;
using LTerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace XivMcp.Plugin.Providers.Duty;

[McpProvider("duty")]
public sealed class DutyProvider : IDisposable
{
    private readonly IDutyState dutyState;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPartyList party;
    private readonly IObjectTable objects;
    private readonly IDataManager data;
    private readonly IPluginLog log;

    private readonly object gate = new();
    private uint trackedTerritory;
    private DateTimeOffset? enteredAt;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? completedAt;
    private DateTimeOffset? lastWipeAt;
    private DateTimeOffset? recommencedAt;
    private int wipes;
    private string? lastEvent;

    public DutyProvider(
        IDutyState dutyState,
        IClientState clientState,
        ICondition condition,
        IPartyList party,
        IObjectTable objects,
        IDataManager data,
        IPluginLog log)
    {
        this.dutyState = dutyState;
        this.clientState = clientState;
        this.condition = condition;
        this.party = party;
        this.objects = objects;
        this.data = data;
        this.log = log;

        trackedTerritory = clientState.TerritoryType;
        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyWiped += OnDutyWiped;
        dutyState.DutyRecommenced += OnDutyRecommenced;
        dutyState.DutyCompleted += OnDutyCompleted;
        clientState.TerritoryChanged += OnTerritoryChanged;
    }

    public sealed record ContentDto(
        uint Id,
        string? Name,
        IdNameDto? ContentType,
        int LevelRequired,
        int LevelSync,
        int ItemLevelRequired,
        int ItemLevelSync,
        int? PartySize,
        bool IsAllianceContent,
        bool HighEndDuty,
        bool IsPvp,
        bool DutyRecorderAllowed);

    public sealed record TrackingDto(
        string? LastEvent,
        DateTimeOffset? EnteredAtUtc,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        DateTimeOffset? LastWipeAtUtc,
        DateTimeOffset? RecommencedAtUtc,
        int Wipes,
        double? ElapsedSinceStartSeconds,
        bool ObservedSinceEntry);

    public sealed record DirectorDto(string? Title, string? Objective, double? TimeRemainingSeconds, string ContentKind);

    public sealed record PartySummaryDto(int Size, bool IsAlliance, int Tanks, int Healers, int Dps, int Other);

    public sealed record DutyStateDto(
        bool InDuty,
        bool IsDutyStarted,
        bool IsCompleted,
        bool IsPvp,
        IdNameDto Territory,
        string? TerritoryIntendedUse,
        ContentDto? Content,
        TrackingDto Tracking,
        DirectorDto? Director,
        PartySummaryDto Party);

    [McpTool("get_duty_state",
        Sources = ["dalamud:IDutyState", "client:ContentsFinder", "lumina:ContentFinderCondition"],
        Title = "Get duty state",
        Description = "Instanced-content status. Returns inDuty (bound by duty), isDutyStarted (the duty's barrier dropped/commenced), isCompleted, " +
                      "isPvp, territory, territoryIntendedUse, content {id, name, contentType, levelRequired, levelSync, itemLevelRequired, " +
                      "itemLevelSync, partySize, isAllianceContent, highEndDuty, isPvp, dutyRecorderAllowed} from the ContentFinderCondition, " +
                      "tracking {lastEvent (started|wiped|recommenced|completed), enteredAtUtc, startedAtUtc, completedAtUtc, lastWipeAtUtc, " +
                      "recommencedAtUtc, wipes, elapsedSinceStartSeconds, observedSinceEntry (false if the plugin loaded mid-duty, so " +
                      "counts may be incomplete)}, director {title, objective, timeRemainingSeconds (duty timer; for some content a pre-start " +
                      "countdown), contentKind} and a party role summary. Use for 'how long is left', 'how many wipes', 'what duty am I in'.")]
    public unsafe DutyStateDto GetDutyState()
    {
        if (objects.LocalPlayer == null)
        {
            throw new McpToolException("No character is logged in.");
        }

        var territoryId = clientState.TerritoryType;
        string? territoryName = null;
        string? intendedUse = null;
        uint cfcFromTerritory = 0;
        if (data.GetExcelSheet<LTerritoryType>().TryGetRow(territoryId, out var territory))
        {
            territoryName = territory.PlaceName.ValueNullable?.Name.ExtractText();
            var use = territory.TerritoryIntendedUse.RowId;
            intendedUse = use <= byte.MaxValue
                ? ((FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse)use).ToString()
                : use.ToString(CultureInfo.InvariantCulture);
            cfcFromTerritory = territory.ContentFinderCondition.RowId;
        }

        var inDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];

        uint cfcId = dutyState.ContentFinderCondition.RowId;
        var gameMain = CsGameMain.Instance();
        if (cfcId == 0 && gameMain != null)
        {
            cfcId = gameMain->CurrentContentFinderConditionId;
        }

        if (cfcId == 0 && inDuty)
        {
            cfcId = cfcFromTerritory;
        }

        ContentDto? content = null;
        if (cfcId != 0 && data.GetExcelSheet<LContentFinderCondition>().TryGetRow(cfcId, out var cfc))
        {
            IdNameDto? contentType = cfc.ContentType.RowId != 0
                ? new IdNameDto(cfc.ContentType.RowId, cfc.ContentType.ValueNullable?.Name.ExtractText())
                : null;
            int? partySize = null;
            var alliance = false;
            if (cfc.ContentMemberType.TryGetValue(out var memberType) && memberType.MembersPerParty > 0)
            {
                var parties = Math.Max(1, (int)memberType.PartyCount);
                partySize = memberType.MembersPerParty * parties;
                alliance = parties > 1;
            }

            content = new ContentDto(
                cfcId,
                NullIfEmpty(cfc.Name.ExtractText()),
                contentType,
                cfc.ClassJobLevelRequired,
                cfc.ClassJobLevelSync,
                cfc.ItemLevelRequired,
                cfc.ItemLevelSync,
                partySize,
                alliance,
                cfc.HighEndDuty,
                cfc.PvP,
                cfc.DutyRecorderAllowed);
        }

        TrackingDto tracking;
        bool completed;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var sameTerritory = trackedTerritory == territoryId;
            completed = sameTerritory && completedAt != null;
            tracking = sameTerritory
                ? new TrackingDto(
                    lastEvent,
                    enteredAt,
                    startedAt,
                    completedAt,
                    lastWipeAt,
                    recommencedAt,
                    wipes,
                    startedAt != null ? GameMath.Round(((completedAt ?? now) - startedAt.Value).TotalSeconds, 0) : null,
                    enteredAt != null)
                : new TrackingDto(null, null, null, null, null, null, 0, null, false);
        }

        DirectorDto? director = null;
        var eventFramework = CsEventFramework.Instance();
        if (eventFramework != null)
        {
            var contentDirector = eventFramework->GetContentDirector();
            if (contentDirector != null)
            {
                var timeLeft = contentDirector->ContentTimeLeft;
                director = new DirectorDto(
                    NullIfEmpty(contentDirector->Director.Title.ToString()),
                    NullIfEmpty(contentDirector->Director.Objective.ToString()),
                    float.IsFinite(timeLeft) && timeLeft > 0 ? GameMath.Round(timeLeft, 0) : null,
                    CsEventFramework.GetCurrentContentType().ToString());
            }
        }

        int tanks = 0, healers = 0, dps = 0, other = 0;
        var size = 0;
        for (var i = 0; i < party.Length; i++)
        {
            var member = party[i];
            if (member == null)
            {
                continue;
            }

            size++;
            var role = member.ClassJob.TryGetValue(out var job) ? Snapshots.RoleName(job) : "none";
            switch (role)
            {
                case "tank": tanks++; break;
                case "healer": healers++; break;
                case "melee" or "physicalRanged" or "magicalRanged": dps++; break;
                default: other++; break;
            }
        }

        return new DutyStateDto(
            inDuty,
            dutyState.IsDutyStarted,
            completed,
            clientState.IsPvP,
            new IdNameDto(territoryId, territoryName),
            intendedUse,
            content,
            tracking,
            director,
            new PartySummaryDto(Math.Max(size, 1), party.IsAlliance, tanks, healers, dps, other));
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        try
        {
            lock (gate)
            {
                trackedTerritory = territoryId;
                enteredAt = DateTimeOffset.UtcNow;
                startedAt = null;
                completedAt = null;
                lastWipeAt = null;
                recommencedAt = null;
                wipes = 0;
                lastEvent = null;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "xiv-mcp: duty tracking territory reset failed");
        }
    }

    private void Record(IDutyStateEventArgs args, string name)
    {
        try
        {
            lock (gate)
            {
                var territory = args.TerritoryType.RowId;
                if (territory != 0 && territory != trackedTerritory)
                {
                    trackedTerritory = territory;
                    enteredAt = null;
                    startedAt = null;
                    completedAt = null;
                    lastWipeAt = null;
                    recommencedAt = null;
                    wipes = 0;
                }

                lastEvent = name;
                var now = DateTimeOffset.UtcNow;
                switch (name)
                {
                    case "started":
                        startedAt ??= now;
                        break;
                    case "wiped":
                        wipes++;
                        lastWipeAt = now;
                        break;
                    case "recommenced":
                        recommencedAt = now;
                        break;
                    case "completed":
                        completedAt = now;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "xiv-mcp: duty event tracking failed");
        }
    }

    private void OnDutyStarted(IDutyStateEventArgs args) => Record(args, "started");

    private void OnDutyWiped(IDutyStateEventArgs args) => Record(args, "wiped");

    private void OnDutyRecommenced(IDutyStateEventArgs args) => Record(args, "recommenced");

    private void OnDutyCompleted(IDutyStateEventArgs args) => Record(args, "completed");

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public void Dispose()
    {
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyWiped -= OnDutyWiped;
        dutyState.DutyRecommenced -= OnDutyRecommenced;
        dutyState.DutyCompleted -= OnDutyCompleted;
        clientState.TerritoryChanged -= OnTerritoryChanged;
    }
}

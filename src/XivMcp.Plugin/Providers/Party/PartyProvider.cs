using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Character;
using XivMcp.Plugin.Util;
using CsGroupManager = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager;
using CsInfoProxyCrossRealm = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm;
using LClassJob = Lumina.Excel.Sheets.ClassJob;

namespace XivMcp.Plugin.Providers.Party;

[McpProvider("party")]
public sealed class PartyProvider : IDisposable
{
    public const string ResourceUri = "ffxiv://party";

    private const long PollIntervalMs = 500;

    private readonly IPartyList party;
    private readonly IBuddyList buddies;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IMcpNotifier notifier;
    private readonly IPluginLog log;

    private long nextPoll;
    private ulong lastSignature;
    private bool faulted;

    public PartyProvider(
        IPartyList party,
        IBuddyList buddies,
        IObjectTable objects,
        IClientState clientState,
        IPlayerState playerState,
        IDataManager data,
        IFramework framework,
        IMcpNotifier notifier,
        IPluginLog log)
    {
        this.party = party;
        this.buddies = buddies;
        this.objects = objects;
        this.clientState = clientState;
        this.playerState = playerState;
        this.data = data;
        this.framework = framework;
        this.notifier = notifier;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public sealed record CompositionDto(int Tanks, int Healers, int Melee, int PhysicalRanged, int MagicalRanged, int Other);

    public sealed record PartyMemberDto(
        int Index,
        string Name,
        string ContentId,
        uint? EntityId,
        WorldDto? HomeWorld,
        JobDto? Job,
        int Level,
        PoolDto Hp,
        PoolDto? Mp,
        IdNameDto? Territory,
        bool InSameTerritory,
        bool ObjectLoaded,
        Vec3Dto? Position,
        MapCoordsDto? MapCoordinates,
        double? Distance,
        bool IsLeader,
        bool IsSelf,
        int? AllianceGroup);

    public sealed record CrossRealmMemberDto(
        int GroupIndex,
        int MemberIndex,
        string Name,
        string ContentId,
        uint? EntityId,
        int Level,
        JobDto? Job,
        WorldDto? HomeWorld,
        WorldDto? CurrentWorld,
        bool IsLeader,
        bool IsSelf);

    public sealed record BuddyDto(string Kind, uint? EntityId, string? Name, PoolDto Hp, double? Distance);

    public sealed record PartyDto(
        string Mode,
        int Size,
        bool IsAlliance,
        bool IsCrossRealm,
        string? LeaderName,
        CompositionDto Composition,
        List<PartyMemberDto> Members,
        List<PartyMemberDto>? AllianceMembers,
        List<CrossRealmMemberDto>? CrossRealmMembers,
        List<BuddyDto>? Buddies,
        string? Note);

    [McpTool("get_party",
        Title = "Get party",
        Description = "The player's party. mode is solo | party | crossRealmParty | alliance. members (the 8-slot party list; empty when solo) " +
                      "each have index, name, contentId (string), entityId, homeWorld, job {abbreviation, name, role}, level, hp/mp {current, max, " +
                      "percent}, territory and inSameTerritory, objectLoaded (in object range), position, mapCoordinates and distance when in the " +
                      "same zone, isLeader, isSelf. allianceMembers lists the other alliance parties' members with allianceGroup (0-based) inside " +
                      "alliance raids. crossRealmMembers comes from the cross-world party list (name, worlds, job, level, leader) and is the only " +
                      "source outside instances when a cross-world Party Finder group is not in the same zone. buddies lists your chocobo " +
                      "companion (kind chocobo), pet and battle buddies such as trust/squadron NPCs (kind battleBuddy). composition counts tanks/healers/melee/physicalRanged/magicalRanged. size and " +
                      "leaderName summarize. Use for 'who is in my party', party roles, and checking members' HP or distance.")]
    public PartyDto GetParty() => Build();

    [McpResource(ResourceUri,
        Name = "Party",
        Description = "Same JSON as get_party. Subscribers are notified when party composition, leader or alliance/cross-world membership " +
                      "changes (not on HP changes), checked at most twice per second.")]
    public PartyDto PartyResource() => Build();

    private unsafe PartyDto Build()
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        var map = MapContext.Current(clientState, data);
        var origin = player.Position;
        var selfContentId = playerState.ContentId;
        var territoryId = clientState.TerritoryType;
        var composition = new int[6];

        var members = new List<PartyMemberDto>();
        var leaderIndex = (int)party.PartyLeaderIndex;
        string? leaderName = null;
        for (var i = 0; i < party.Length; i++)
        {
            var member = party[i];
            if (member == null)
            {
                continue;
            }

            var dto = Member(member, i, i == leaderIndex, null, selfContentId, territoryId, origin, map);
            members.Add(dto);
            Count(composition, member.ClassJob.ValueNullable);
            if (dto.IsLeader)
            {
                leaderName = dto.Name;
            }
        }

        List<PartyMemberDto>? alliance = null;
        if (party.IsAlliance)
        {
            alliance = [];
            for (var i = 0; i < 20; i++)
            {
                var member = party.CreateAllianceMemberReference(party.GetAllianceMemberAddress(i));
                if (member == null || Snapshots.EntityIdOrNull(member.EntityId) == null || string.IsNullOrEmpty(member.Name.TextValue))
                {
                    continue;
                }

                alliance.Add(Member(member, i, false, i / 8, selfContentId, territoryId, origin, map));
            }
        }

        List<CrossRealmMemberDto>? crossRealm = null;
        var isCrossRealm = false;
        var proxy = CsInfoProxyCrossRealm.Instance();
        if (proxy != null && (proxy->IsInCrossRealmParty || proxy->IsCrossRealm))
        {
            isCrossRealm = true;
            crossRealm = [];
            var jobs = data.GetExcelSheet<LClassJob>();
            var groupCount = Math.Min((int)proxy->GroupCount, proxy->CrossRealmGroups.Length);
            for (var g = 0; g < groupCount; g++)
            {
                ref var group = ref proxy->CrossRealmGroups[g];
                var memberCount = Math.Min((int)group.GroupMemberCount, group.GroupMembers.Length);
                for (var m = 0; m < memberCount; m++)
                {
                    ref var cm = ref group.GroupMembers[m];
                    if (cm.ContentId == 0)
                    {
                        continue;
                    }

                    JobDto? job = cm.ClassJobId != 0 && jobs.TryGetRow(cm.ClassJobId, out var jobRow) ? Snapshots.Job(jobRow) : null;
                    crossRealm.Add(new CrossRealmMemberDto(
                        cm.GroupIndex,
                        cm.MemberIndex,
                        cm.NameString,
                        cm.ContentId.ToString(),
                        Snapshots.EntityIdOrNull(cm.EntityId),
                        cm.Level,
                        job,
                        Snapshots.World(data, (uint)Math.Max((short)0, cm.HomeWorld)),
                        Snapshots.World(data, (uint)Math.Max((short)0, cm.CurrentWorld)),
                        cm.IsPartyLeader,
                        cm.ContentId == selfContentId));
                }
            }

            if (members.Count == 0)
            {
                var ownGroup = proxy->LocalPlayerGroupIndex;
                foreach (var cm in crossRealm.Where(c => c.GroupIndex == ownGroup))
                {
                    if (cm.Job != null && jobs.TryGetRow(cm.Job.Id, out var jobRow))
                    {
                        Count(composition, jobRow);
                    }
                    else
                    {
                        composition[5]++;
                    }

                    if (cm.IsLeader)
                    {
                        leaderName = cm.Name;
                    }
                }
            }
        }

        var buddyList = Buddies(origin);
        var size = members.Count > 0
            ? members.Count
            : crossRealm?.Count(c => c.GroupIndex == (proxy != null ? proxy->LocalPlayerGroupIndex : 0)) ?? 0;
        var mode = party.IsAlliance || (proxy != null && proxy->IsInAllianceRaid)
            ? "alliance"
            : isCrossRealm && size > 1 ? "crossRealmParty"
            : size > 1 ? "party"
            : "solo";

        string? note = null;
        if (mode == "solo")
        {
            note = "Not in a party.";
        }
        else if (members.Count == 0 && isCrossRealm)
        {
            note = "Party list is empty in this zone; members come from the cross-world party list (no HP/position available).";
        }

        return new PartyDto(
            mode,
            Math.Max(size, mode == "solo" ? 1 : size),
            party.IsAlliance,
            isCrossRealm,
            leaderName,
            new CompositionDto(composition[0], composition[1], composition[2], composition[3], composition[4], composition[5]),
            members,
            alliance is { Count: > 0 } ? alliance : null,
            crossRealm is { Count: > 0 } ? crossRealm : null,
            buddyList.Count > 0 ? buddyList : null,
            note);
    }

    private static PartyMemberDto Member(
        IPartyMember member,
        int index,
        bool isLeader,
        int? allianceGroup,
        ulong selfContentId,
        uint territoryId,
        System.Numerics.Vector3 origin,
        in MapContext map)
    {
        var memberTerritory = member.Territory.RowId;
        var sameTerritory = memberTerritory == territoryId;
        var loaded = member.GameObject != null;
        var position = member.Position;
        IdNameDto? territory = memberTerritory == 0
            ? null
            : new IdNameDto(memberTerritory, member.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText());

        return new PartyMemberDto(
            index,
            member.Name.TextValue,
            member.ContentId.ToString(),
            Snapshots.EntityIdOrNull(member.EntityId),
            Snapshots.World(member.World),
            member.ClassJob.RowId != 0 ? Snapshots.Job(member.ClassJob) : null,
            member.Level,
            Snapshots.Pool(member.CurrentHP, member.MaxHP),
            member.MaxMP > 0 ? Snapshots.Pool(member.CurrentMP, member.MaxMP) : null,
            territory,
            sameTerritory,
            loaded,
            sameTerritory ? Snapshots.Vec(position) : null,
            sameTerritory ? map.ToMap(position) : null,
            sameTerritory ? GameMath.Round(GameMath.Distance3D(origin, position), 1) : null,
            isLeader,
            selfContentId != 0 && member.ContentId == selfContentId,
            allianceGroup);
    }

    private List<BuddyDto> Buddies(System.Numerics.Vector3 origin)
    {
        var result = new List<BuddyDto>();

        void Add(string kind, Dalamud.Game.ClientState.Buddy.IBuddyMember? buddy, string? fallbackName)
        {
            if (buddy == null || Snapshots.EntityIdOrNull(buddy.EntityId) == null)
            {
                return;
            }

            var obj = buddy.GameObject;
            var name = obj?.Name.TextValue;
            result.Add(new BuddyDto(
                kind,
                buddy.EntityId,
                string.IsNullOrEmpty(name) ? fallbackName : name,
                Snapshots.Pool(buddy.CurrentHP, buddy.MaxHP),
                obj != null ? GameMath.Round(GameMath.Distance3D(origin, obj.Position), 1) : null));
        }

        Add("chocobo", buddies.CompanionBuddy, null);
        var pet = buddies.PetBuddy;
        Add("pet", pet, pet?.PetData.ValueNullable?.Name.ExtractText());
        for (var i = 0; i < buddies.Length; i++)
        {
            Add("battleBuddy", buddies[i], null);
        }

        return result;
    }

    private static void Count(int[] composition, LClassJob? job)
    {
        var role = job is { } row ? Snapshots.RoleName(row) : "none";
        composition[role switch
        {
            "tank" => 0,
            "healer" => 1,
            "melee" => 2,
            "physicalRanged" => 3,
            "magicalRanged" => 4,
            _ => 5,
        }]++;
    }

    private unsafe void OnFrameworkUpdate(IFramework fw)
    {
        if (faulted)
        {
            return;
        }

        try
        {
            var now = Environment.TickCount64;
            if (now < nextPoll)
            {
                return;
            }

            nextPoll = now + PollIntervalMs;
            ulong signature = 1469598103934665603UL;
            var manager = CsGroupManager.Instance();
            if (manager != null)
            {
                ref var group = ref manager->MainGroup;
                signature = Mix(signature, group.MemberCount);
                signature = Mix(signature, group.PartyLeaderIndex);
                signature = Mix(signature, group.AllianceFlags);
                signature = Mix(signature, (ulong)group.PartyId);
                var count = Math.Min((int)group.MemberCount, group.PartyMembers.Length);
                for (var i = 0; i < count; i++)
                {
                    ref var member = ref group.PartyMembers[i];
                    signature = Mix(signature, member.ContentId ^ member.EntityId ^ ((ulong)member.ClassJob << 56));
                }
            }

            var proxy = CsInfoProxyCrossRealm.Instance();
            if (proxy != null)
            {
                signature = Mix(signature, proxy->IsInCrossRealmParty ? 1UL : 0UL);
                signature = Mix(signature, proxy->GroupCount);
                if (proxy->IsInCrossRealmParty)
                {
                    var groupCount = Math.Min((int)proxy->GroupCount, proxy->CrossRealmGroups.Length);
                    for (var g = 0; g < groupCount; g++)
                    {
                        ref var group = ref proxy->CrossRealmGroups[g];
                        var memberCount = Math.Min((int)group.GroupMemberCount, group.GroupMembers.Length);
                        for (var m = 0; m < memberCount; m++)
                        {
                            ref var cm = ref group.GroupMembers[m];
                            signature = Mix(signature, cm.ContentId ^ (cm.IsPartyLeader ? 0x8000_0000_0000_0000UL : 0));
                        }
                    }
                }
            }

            if (signature != lastSignature)
            {
                lastSignature = signature;
                notifier.ResourceUpdated(ResourceUri);
            }
        }
        catch (Exception ex)
        {
            faulted = true;
            log.Error(ex, "xiv-mcp: party change polling failed and was disabled");
        }
    }

    private static ulong Mix(ulong hash, ulong value) => (hash ^ value) * 0x100000001B3UL;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }
}

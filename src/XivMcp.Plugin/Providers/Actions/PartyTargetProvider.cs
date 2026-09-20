using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>target_party_member: one named party member per approved call.</summary>
[McpProvider("actions")]
public sealed class PartyTargetProvider
{
    private const uint InvalidEntityId = 0xE0000000;

    private readonly IPartyList partyList;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;

    /// <summary>Source of the on-screen rows; replaced in host tests, where the game's HUD agent does not exist.</summary>
    internal Func<List<(uint EntityId, string Name)>> HudRows { get; set; } = PartyHud.Rows;

    public PartyTargetProvider(IPartyList partyList, ITargetManager targetManager, IObjectTable objectTable)
    {
        this.partyList = partyList;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
    }

    [McpTool("target_party_member",
        Sources = ["dalamud:IPartyList", "dalamud:ITargetManager", "dalamud:IObjectTable", "client:AgentHUD"],
        ApprovalSummary = "Target the party member named {name} / in party list slot {slot} (only one of the two is given).",
        Title = "Target a party member",
        Description =
            "Targets one member of the user's own party, like clicking their row in the party list. Choose with exactly one of: name (case-insensitive; the full name, optionally \"Firstname Lastname@World\" to tell apart members with the same name, otherwise a unique part of the name) or slot (1-8, the row in the on-screen party list from the top; 1 is the user). " +
            "One named member per call: there is no lowest-HP, next-member, role or other filter, and the call never uses an action. Alliance members outside the party are not included (use set_target). " +
            "Fails with not_found when nobody matches or the slot is empty, invalid_arguments when the name matches several members (the message lists them), and unavailable when the member is in another zone or too far away to be targeted. " +
            "Returns slot, name, world, entityId, distance (yalms) and the party as the tool saw it.",
        Permission = ToolPermission.Action, Idempotent = true)]
    public PartyTargetResult TargetPartyMember(
        [McpParam("Party member name, optionally with @World.")] string? name = null,
        [McpParam("Row in the party list, 1-8 from the top.", Minimum = 1, Maximum = 8)] int? slot = null)
    {
        if (slot.HasValue == !string.IsNullOrWhiteSpace(name))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Pass exactly one of name or slot.");

        var party = ReadParty();
        var member = PartyTargeting.Resolve(party, name, slot);

        var obj = member.EntityId is 0 or InvalidEntityId ? null : objectTable.SearchByEntityId(member.EntityId);
        if (obj is null)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{PartyTargeting.FullName(member)} is not nearby (another zone, or too far away), so the game cannot target them.", retryable: true);
        if (!obj.IsTargetable)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{PartyTargeting.FullName(member)} cannot be targeted right now.", retryable: true);

        targetManager.Target = obj;

        var origin = objectTable.LocalPlayer?.Position;
        float? distance = origin is { } o ? MathF.Round(Vector3.Distance(o, obj.Position), 1) : null;
        return new PartyTargetResult(
            member.Slot,
            member.Name,
            member.World,
            member.EntityId,
            obj.GameObjectId.ToString(CultureInfo.InvariantCulture),
            distance,
            party.Select(m => new PartyRow(m.Slot, m.Name, m.World)).ToList());
    }

    /// <summary>
    /// The party in on-screen order. The HUD agent knows the order of the rows (the player can sort the list); IPartyList
    /// knows the worlds. Without the HUD agent the IPartyList order is used.
    /// </summary>
    private List<PartySlot> ReadParty()
    {
        var known = new Dictionary<uint, (string Name, string? World)>();
        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member is null || member.EntityId is 0 or InvalidEntityId)
                continue;
            var memberName = member.Name.TextValue;
            if (string.IsNullOrEmpty(memberName))
                continue;
            var world = member.World.ValueNullable?.Name.ExtractText();
            known[member.EntityId] = (memberName, string.IsNullOrEmpty(world) ? null : world);
        }

        var rows = new List<PartySlot>();
        foreach (var (entityId, hudName) in HudRows())
        {
            var number = rows.Count + 1;
            if (known.TryGetValue(entityId, out var info))
                rows.Add(new PartySlot(number, info.Name, info.World, entityId));
            else
                rows.Add(new PartySlot(number, hudName, null, entityId));
        }

        if (rows.Count == 0)
        {
            foreach (var (entityId, info) in known)
                rows.Add(new PartySlot(rows.Count + 1, info.Name, info.World, entityId));
        }

        return rows;
    }

    public sealed record PartyRow(int Slot, string Name, string? World);

    public sealed record PartyTargetResult(int Slot, string Name, string? World, uint EntityId, string GameObjectId, float? Distance, IReadOnlyList<PartyRow> Party);
}

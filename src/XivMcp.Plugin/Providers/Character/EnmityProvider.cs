using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Character;

internal sealed record EnmityRecord(uint EntityId, int Enmity, string? Name);

public sealed record EnemyListEntry(int Position, string? Name, uint EntityId, int EnmityPercent, bool? IsCurrentTarget);

public sealed record TargetEnmityEntry(int Rank, string? Name, uint EntityId, int Enmity, double PercentOfTop, bool? IsPlayer);

public sealed record EnmityResult(
    int EnemyCount,
    IReadOnlyList<EnemyListEntry> Enemies,
    uint? TargetEntityId,
    string? TargetName,
    IReadOnlyList<TargetEnmityEntry> TargetEnmity,
    string Note);

/// <summary>Pure shaping for get_enmity_list.</summary>
internal static class EnmityShaper
{
    public const int MaxEntries = 32;

    public const string Note =
        "enemies is the on-screen enemy list (enmityPercent is the player's own enmity on that enemy, 100 = the player has aggro). " +
        "targetEnmity is the enmity table of the current target as the party list draws it, highest first. A one-off snapshot for " +
        "questions like 'who has aggro'; it is not a feed for automating combat. Not verified in game.";

    public static EnmityResult Build(
        IEnumerable<EnmityRecord> haters,
        uint hateTargetId,
        IEnumerable<EnmityRecord> targetHate,
        uint currentTargetId,
        uint localEntityId,
        Func<uint, string?> nameOf)
    {
        var enemies = haters
            .Where(h => Valid(h.EntityId))
            .Take(MaxEntries)
            .Select((h, i) => new EnemyListEntry(
                i + 1,
                Clean(h.Name) ?? Clean(nameOf(h.EntityId)),
                h.EntityId,
                Math.Clamp(h.Enmity, 0, 100),
                currentTargetId != 0 && h.EntityId == currentTargetId ? true : null))
            .ToList();

        var table = targetHate.Where(h => Valid(h.EntityId)).OrderByDescending(h => h.Enmity).Take(MaxEntries).ToList();
        var top = table.Count > 0 ? Math.Max(1, table[0].Enmity) : 1;
        var ranked = table
            .Select((h, i) => new TargetEnmityEntry(
                i + 1,
                Clean(h.Name) ?? Clean(nameOf(h.EntityId)),
                h.EntityId,
                h.Enmity,
                Math.Round(Math.Max(0, h.Enmity) * 100.0 / top, 1),
                localEntityId != 0 && h.EntityId == localEntityId ? true : null))
            .ToList();

        var hasTarget = Valid(hateTargetId) && ranked.Count > 0;
        return new EnmityResult(
            enemies.Count,
            enemies,
            hasTarget ? hateTargetId : null,
            hasTarget ? Clean(nameOf(hateTargetId)) : null,
            hasTarget ? ranked : [],
            Note);
    }

    private static bool Valid(uint entityId) => entityId is not 0 and not Snapshots.InvalidEntityId;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The enemy list and the current target's enmity table, as the HUD shows them (framework thread).</summary>
[McpProvider("character")]
public sealed unsafe class EnmityProvider
{
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;

    public EnmityProvider(IObjectTable objects, ITargetManager targets)
    {
        this.objects = objects;
        this.targets = targets;
    }

    [McpTool("get_enmity_list",
        Sources = ["client:UIState", "dalamud:IObjectTable", "dalamud:ITargetManager"],
        Title = "Get enemy list and target enmity",
        Description =
            "The HUD's enmity information, as a one-off snapshot. enemies: the on-screen enemy list in display order {position, name, " +
            "entityId, enmityPercent (the player's own enmity on that enemy; 100 = it is attacking the player), isCurrentTarget}. " +
            "targetEnmity: for the current target, who is on its enmity table {rank, name, entityId, enmity, percentOfTop, isPlayer} " +
            "highest first, which is what the party list enmity bars show. Both are empty out of combat. Use for 'who has aggro' or " +
            "'what is attacking me'; use get_target for the target's HP, cast bar and statuses, and get_party for party members.",
        RequiresLogin = true)]
    public EnmityResult GetEnmityList()
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        var state = UIState.Instance();
        if (state == null)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Enmity data is not loaded yet; try again once the character is in the world.");

        var haters = new List<EnmityRecord>();
        var haterSpan = state->Hater.Haters;
        var haterCount = Math.Clamp(state->Hater.HaterCount, 0, haterSpan.Length);
        for (var i = 0; i < haterCount; i++)
        {
            ref var hater = ref haterSpan[i];
            haters.Add(new EnmityRecord(hater.EntityId, hater.Enmity, hater.NameString));
        }

        var hate = new List<EnmityRecord>();
        var hateSpan = state->Hate.HateInfo;
        var hateCount = Math.Clamp(state->Hate.HateArrayLength, 0, hateSpan.Length);
        for (var i = 0; i < hateCount; i++)
        {
            hate.Add(new EnmityRecord(hateSpan[i].EntityId, hateSpan[i].Enmity, null));
        }

        return EnmityShaper.Build(
            haters,
            state->Hate.HateTargetId,
            hate,
            targets.Target?.EntityId ?? 0,
            player.EntityId,
            id => objects.SearchByEntityId(id)?.Name.TextValue);
    }
}

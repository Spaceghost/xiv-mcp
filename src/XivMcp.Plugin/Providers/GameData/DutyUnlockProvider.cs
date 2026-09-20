using Lumina.Data;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>How a duty is unlocked, which roulettes it sits in, and the static roulette list.</summary>
[McpProvider("gamedata")]
public sealed class DutyUnlockProvider
{
    private const int MaxText = 300;

    private readonly GameDataIndex index;
    private readonly DutyDataProvider duties;
    private readonly Cached<List<RouletteMatch.Roulette>> englishRoulettes;

    public DutyUnlockProvider(IGameDataSource data)
    {
        index = GameDataIndex.For(data);
        duties = new DutyDataProvider(data);
        englishRoulettes = new Cached<List<RouletteMatch.Roulette>>(ReadEnglishRoulettes);
    }

    public sealed record QuestStep(
        uint QuestId,
        string Name,
        int Level,
        string? Genre,
        string? Issuer,
        MapLocation? IssuerLocation,
        string PreviousQuestsJoin,
        IReadOnlyList<QuestStepRef> PreviousQuests);

    public sealed record QuestStepRef(uint QuestId, string Name, int Level);

    public sealed record RouletteRef(uint? RouletteId, string Name);

    public sealed record DutyUnlock(
        uint Id,
        string Name,
        string? Type,
        int Level,
        int ItemLevelRequired,
        int LevelSync,
        int ItemLevelSync,
        string? Expansion,
        bool InDutyFinder,
        IReadOnlyList<QuestStep> UnlockQuests,
        IReadOnlyList<RouletteRef> Roulettes,
        string Note);

    public sealed record RouletteBonus(uint ItemId, string Name, int Amount);

    public sealed record RouletteInfo(
        uint Id,
        string Name,
        string? Category,
        string? DutyType,
        string? Description,
        int RequiredLevel,
        int ItemLevelRequired,
        int ItemLevelSync,
        int PartySize,
        int RewardTomeA,
        int RewardTomeB,
        int RewardTomeC,
        RouletteBonus? RoleBonus,
        bool InDutyFinder,
        bool Pvp,
        int? DutyCount);

    [McpTool("get_duty_unlock",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:ContentFinderCondition", "lumina:Quest", "lumina:Level", "lumina:ENpcResident", "lumina:ContentRoulette"],
        Title = "Get how a duty is unlocked",
        Description =
            "How to unlock one duty (ContentFinderCondition id from search_duties): required level and item level, level and item-level sync, " +
            "expansion, and unlockQuests: each quest the game data ties to the duty (its unlock criteria, or the quest whose reward opens the " +
            "instance) with quest level, journal genre, issuer NPC, issuer zone and map X/Y, and its prerequisite quests one level deep " +
            "(previousQuestsJoin says whether all or any are needed). Also roulettes: the Duty Roulettes that include the duty, with the " +
            "ContentRoulette id when it can be matched (see list_roulettes). unlockQuests is empty when the sheets do not name a quest: many " +
            "duties unlock through a quest script instead, so empty does not mean unlocked by default. " +
            "Use get_duty for party composition and description, get_quest for a quest's full details and rewards, and get_quest_status to " +
            "check whether the player has completed the quests.",
        GameThread = false,
        RequiresLogin = false)]
    public DutyUnlock GetDutyUnlock([McpParam("ContentFinderCondition id (from search_duties).", Minimum = 1)] uint contentFinderConditionId)
    {
        var id = contentFinderConditionId;
        if (index.Row<Sheets.ContentFinderCondition>(id) is not { } row || SheetJson.Text(row.Name).Length == 0)
            throw McpToolException.WithCode(McpErrorCodes.NotFound, $"Duty {id} not found. Use search_duties to look up duty ids.");

        var questIds = new List<uint>();
        foreach (var criteria in new[] { row.UnlockCriteria, row.UnlockCriteria2 })
        {
            if (criteria.RowId != 0 && criteria.Is<Sheets.Quest>() && !questIds.Contains(criteria.RowId)) questIds.Add(criteria.RowId);
        }

        if (duties.UnlockQuest(row) is { } known && !questIds.Contains(known.Id)) questIds.Add(known.Id);

        var roulettes = DutyDataProvider.RouletteColumns
            .Where(p => p.GetValue(row) is true)
            .Select(p => Roulette(p.Name))
            .ToList();

        return new DutyUnlock(
            id,
            GameDataIndex.Capitalize(SheetJson.Text(row.Name)),
            row.ContentType.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.ContentType.ValueNullable?.Name ?? default)) : null,
            row.ClassJobLevelRequired,
            row.ItemLevelRequired,
            row.ClassJobLevelSync,
            row.ItemLevelSync,
            index.Row<Sheets.ExVersion>(row.RequiredExVersion.RowId) is { } ex ? GameDataIndex.NullIfEmpty(SheetJson.Text(ex.Name)) : null,
            row.IsInDutyFinder,
            questIds.Select(Step).Where(s => s != null).Select(s => s!).ToList(),
            roulettes,
            "Prerequisites are one level deep: call get_quest on a previous quest to walk further back. Main scenario progress and " +
            "other script-only requirements are not in the sheets.");
    }

    [McpTool("list_roulettes",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:ContentRoulette", "lumina:ContentRouletteRoleBonus", "lumina:ContentFinderCondition"],
        Title = "List Duty Roulettes",
        Description =
            "Every Duty Roulette in the game data (ContentRoulette): id, name, category, dutyType, description, requiredLevel, itemLevelRequired, " +
            "itemLevelSync, partySize, rewardTomeA/B/C (the sheet's three tomestone reward amounts for the daily bonus; the sheet does not say " +
            "which tomestone each is), roleBonus (the adventurer-in-need item and amount), inDutyFinder, pvp, and dutyCount = how many duties are " +
            "flagged for it (only for roulettes that have a membership flag on duties). Static: it says nothing about today's bonus or what the " +
            "character has unlocked; use get_roulette_status for that while the game is running, and get_duty_unlock for one duty's roulettes. " +
            "Returns {total, offset, returned, truncated, results}.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<RouletteInfo> ListRoulettes(
        [McpParam("true = only roulettes shown in the Duty Finder.")] bool inDutyFinderOnly = true,
        [McpParam("Maximum results (1-100).", Minimum = 1, Maximum = 100)] int limit = 50,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var counts = DutyCounts();
        var all = new List<RouletteInfo>();
        foreach (var row in index.Sheet<Sheets.ContentRoulette>())
        {
            var name = SheetJson.Text(row.Name);
            if (row.RowId == 0 || name.Length == 0 || (inDutyFinderOnly && !row.IsInDutyFinder)) continue;
            var bonus = row.ContentRouletteRoleBonus.ValueNullable;
            var members = row.ContentMemberType.ValueNullable;
            all.Add(new RouletteInfo(
                row.RowId,
                name,
                GameDataIndex.NullIfEmpty(SheetJson.Text(row.Category)),
                GameDataIndex.NullIfEmpty(SheetJson.Text(row.DutyType)),
                Cap(SheetJson.Text(row.Description)),
                row.RequiredLevel,
                row.ItemLevelRequired,
                row.ItemLevelSync,
                members is { } m && m.MembersPerParty > 0 ? m.MembersPerParty * Math.Max(1, (int)m.PartyCount) : row.QueueMaxPlayers,
                row.RewardTomeA,
                row.RewardTomeB,
                row.RewardTomeC,
                bonus is { } b && b.ItemRewardType.RowId != 0 && b.RewardAmount > 0
                    ? new RouletteBonus(b.ItemRewardType.RowId, index.ItemName(b.ItemRewardType.RowId), b.RewardAmount)
                    : null,
                row.IsInDutyFinder,
                row.IsPvP,
                counts.TryGetValue(row.RowId, out var count) ? count : null));
        }

        var page = Page<RouletteInfo>.From(all, offset, TextSearch.ClampLimit(limit, 100));
        return new PagedResult<RouletteInfo>(page.Total, page.Offset, page.Items.Count, page.Truncated, page.Items);
    }

    // ------------------------------------------------------------------ helpers

    private QuestStep? Step(uint questId)
    {
        if (index.Row<Sheets.Quest>(questId) is not { } quest || SheetJson.Text(quest.Name).Length == 0) return null;
        string? issuer = null;
        if (quest.IssuerStart.RowId != 0)
        {
            if (quest.IssuerStart.TryGetValue<Sheets.ENpcResident>(out var npc)) issuer = GameDataIndex.Capitalize(SheetJson.Text(npc.Singular));
            else if (quest.IssuerStart.TryGetValue<Sheets.EObjName>(out var obj)) issuer = GameDataIndex.Capitalize(SheetJson.Text(obj.Singular));
        }

        var previous = new List<QuestStepRef>();
        foreach (var p in quest.PreviousQuest)
        {
            if (p.RowId == 0 || index.Row<Sheets.Quest>(p.RowId) is not { } prior) continue;
            previous.Add(new QuestStepRef(p.RowId, SheetJson.Text(prior.Name) is { Length: > 0 } n ? n : $"Quest #{p.RowId}", GameDataIndex.QuestLevel(prior)));
        }

        return new QuestStep(
            questId,
            SheetJson.Text(quest.Name),
            GameDataIndex.QuestLevel(quest),
            quest.JournalGenre.ValueNullable is { } genre ? GameDataIndex.NullIfEmpty(SheetJson.Text(genre.Name)) : null,
            GameDataIndex.NullIfEmpty(issuer),
            index.Location(quest.IssuerLocation.RowId),
            quest.PreviousQuestJoin == 2 ? "any" : "all",
            previous);
    }

    private RouletteRef Roulette(string column)
    {
        var id = RouletteMatch.Find(column, englishRoulettes.Value);
        var name = id is { } rid && index.Row<Sheets.ContentRoulette>(rid) is { } row ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.Name)) : null;
        return new RouletteRef(name != null ? id : null, name ?? DutyDataProvider.Humanize(column));
    }

    private List<RouletteMatch.Roulette> ReadEnglishRoulettes()
    {
        var list = new List<RouletteMatch.Roulette>();
        try
        {
            foreach (var row in index.Module.GetSheet<Sheets.ContentRoulette>(Language.English))
            {
                var name = SheetJson.Text(row.Name);
                if (row.RowId != 0 && name.Length > 0) list.Add(new RouletteMatch.Roulette(row.RowId, name));
            }
        }
        catch (Exception)
        {
            // No English sheet in this client: roulettes are then reported by their column name only.
        }

        return list;
    }

    private Dictionary<uint, int> DutyCounts()
    {
        var byColumn = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var column in DutyDataProvider.RouletteColumns)
        {
            if (RouletteMatch.Find(column.Name, englishRoulettes.Value) is { } id) byColumn[column.Name] = id;
        }

        var counts = byColumn.Values.Distinct().ToDictionary(id => id, _ => 0);
        foreach (var duty in index.Sheet<Sheets.ContentFinderCondition>())
        {
            if (SheetJson.Text(duty.Name).Length == 0) continue;
            foreach (var column in DutyDataProvider.RouletteColumns)
            {
                if (byColumn.TryGetValue(column.Name, out var id) && column.GetValue(duty) is true) counts[id]++;
            }
        }

        return counts;
    }

    private static string? Cap(string text) =>
        text.Length == 0 ? null : text.Length <= MaxText ? text : text[..MaxText] + "…";
}

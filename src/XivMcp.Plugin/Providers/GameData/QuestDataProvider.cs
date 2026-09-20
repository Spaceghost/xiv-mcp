using System.Globalization;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Quest data from the Quest sheet (static data; completion state is get_quest_status).</summary>
[McpProvider("gamedata")]
public sealed class QuestDataProvider
{
    private const uint QuestIdBase = 65536;

    private readonly GameDataIndex index;

    public QuestDataProvider(IGameDataSource data) => index = GameDataIndex.For(data);

    /// <summary>Accepts both full quest row ids (65536+) and short ids (the low 16 bits).</summary>
    internal static uint NormalizeQuestId(uint questId) => questId is > 0 and < QuestIdBase ? questId + QuestIdBase : questId;

    [McpTool("search_quests",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Quest"],
        Title = "Search quests",
        Description =
            "Searches quests by name (ranked exact > prefix > word > substring; a numeric query matches the quest id). " +
            "Returns {total, offset, returned, truncated, results:[{id, name, level, genre, expansion}]}; genre is the journal genre " +
            "(e.g. \"Seventh Astral Era\", \"Weaver Quests\"). Quest ids are Quest sheet row ids (65536+). " +
            "Use get_quest for issuer, location, prerequisites and rewards, and get_quest_status to check whether the player completed them.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<QuestSummary> SearchQuests(
        [McpParam("Quest name text (any case).")] string query,
        [McpParam("Maximum results (1-500).", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpToolException("query is required.");
        if (uint.TryParse(query.Trim(), out var numeric) && numeric is > 0 and < QuestIdBase) query = NormalizeQuestId(numeric).ToString(CultureInfo.InvariantCulture);
        var page = TextSearch.Search(index.Quests, q => q.Id, q => q.Lower, query, null, offset, limit);
        var results = page.Items.Select(q => new QuestSummary(q.Id, q.Name, q.Level, GenreName(q.Genre), ExpansionName(q.Expansion))).ToList();
        return new PagedResult<QuestSummary>(page.Total, page.Offset, results.Count, page.Truncated, results);
    }

    [McpTool("get_quest",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Quest", "lumina:Level", "lumina:ENpcResident"],
        Title = "Get quest details",
        Description =
            "Static details for one quest id: name, level, allowed classes/jobs, expansion, journal genre/category/section, place name, " +
            "issuer NPC with zone and map X/Y coordinates (when the issuer has a placement in game data), prerequisite quests " +
            "(previousQuestsJoin tells whether all or any are required), quests that lock it out, class/job unlocked, grand company or " +
            "beast tribe requirements, repeatability, and rewards (gil, currency, guaranteed and optional items with counts, emote, action, " +
            "general actions, unlocked instance content). Experience rewards are not computed. Accepts ids with or without the 65536 base.",
        GameThread = false,
        RequiresLogin = false)]
    public QuestDetail GetQuest([McpParam("Quest id (Quest sheet row id, e.g. 65575; the short form 39 is also accepted).")] uint questId)
    {
        questId = NormalizeQuestId(questId);
        if (index.Row<Sheets.Quest>(questId) is not { } row || SheetJson.Text(row.Name).Length == 0)
            throw new McpToolException($"Quest {questId} not found. Use search_quests to look up quest ids.");

        var genre = row.JournalGenre.ValueNullable;
        var category = genre?.JournalCategory.ValueNullable;
        var section = category?.JournalSection.ValueNullable;

        uint? issuerId = null;
        string? issuerName = null;
        var issuer = row.IssuerStart;
        if (issuer.RowId != 0)
        {
            issuerId = issuer.RowId;
            if (issuer.TryGetValue<Sheets.ENpcResident>(out var npc)) issuerName = GameDataIndex.Capitalize(SheetJson.Text(npc.Singular));
            else if (issuer.TryGetValue<Sheets.EObjName>(out var obj)) issuerName = GameDataIndex.Capitalize(SheetJson.Text(obj.Singular));
        }

        var previous = row.PreviousQuest.Where(p => p.RowId != 0)
            .Select(p => new NamedRef(p.RowId, index.QuestName(p.RowId) ?? $"Quest #{p.RowId}")).ToList();
        var locks = row.QuestLock.Where(p => p.RowId != 0)
            .Select(p => new NamedRef(p.RowId, index.QuestName(p.RowId) ?? $"Quest #{p.RowId}")).ToList();

        return new QuestDetail(
            questId,
            SheetJson.Text(row.Name),
            GameDataIndex.NullIfEmpty(SheetJson.Text(row.Id)),
            GameDataIndex.QuestLevel(row),
            index.ClassJobCategoryName(row.ClassJobCategory0.RowId),
            ExpansionName(row.Expansion.RowId),
            genre is { } g ? GameDataIndex.NullIfEmpty(SheetJson.Text(g.Name)) : null,
            category is { } c ? GameDataIndex.NullIfEmpty(SheetJson.Text(c.Name)) : null,
            section is { } s ? GameDataIndex.NullIfEmpty(SheetJson.Text(s.Name)) : null,
            row.PlaceName.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.PlaceName.ValueNullable?.Name ?? default)) : null,
            issuerId,
            GameDataIndex.NullIfEmpty(issuerName),
            index.Location(row.IssuerLocation.RowId),
            previous,
            row.PreviousQuestJoin == 2 ? "any" : "all",
            locks,
            row.ClassJobUnlock.RowId != 0 ? index.ClassJob(row.ClassJobUnlock.RowId)?.Name : null,
            row.GrandCompany.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.GrandCompany.ValueNullable?.Name ?? default)) : null,
            row.BeastTribe.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.BeastTribe.ValueNullable?.Name ?? default)) : null,
            row.IsRepeatable,
            Rewards(row));
    }

    private QuestRewards Rewards(Sheets.Quest row)
    {
        var items = new List<QuestItemReward>();
        for (var i = 0; i < row.Reward.Count; i++)
        {
            var reward = row.Reward[i];
            if (reward.RowId == 0 || !reward.Is<Sheets.Item>()) continue;
            var count = i < row.ItemCountReward.Count ? row.ItemCountReward[i] : 0;
            if (count == 0) continue;
            items.Add(new QuestItemReward(reward.RowId, index.ItemName(reward.RowId), count));
        }

        var optional = new List<QuestItemReward>();
        for (var i = 0; i < row.OptionalItemReward.Count; i++)
        {
            var reward = row.OptionalItemReward[i];
            if (reward.RowId == 0) continue;
            var count = i < row.OptionalItemCountReward.Count ? row.OptionalItemCountReward[i] : 0;
            var hq = i < row.OptionalItemIsHQReward.Count && row.OptionalItemIsHQReward[i];
            optional.Add(new QuestItemReward(reward.RowId, index.ItemName(reward.RowId), Math.Max(1, (int)count), hq ? true : null));
        }

        var generalActions = row.GeneralActionReward.Where(a => a.RowId != 0)
            .Select(a => SheetJson.Text(a.ValueNullable?.Name ?? default)).Where(n => n.Length > 0).ToList();

        string? instance = null;
        if (row.InstanceContentUnlock.RowId != 0 && row.InstanceContentUnlock.ValueNullable is { } ic)
            instance = ic.ContentFinderCondition.ValueNullable is { } cfc && SheetJson.Text(cfc.Name) is { Length: > 0 } n
                ? GameDataIndex.Capitalize(n)
                : $"InstanceContent #{row.InstanceContentUnlock.RowId}";

        return new QuestRewards(
            row.GilReward,
            row.CurrencyReward.RowId != 0 && row.CurrencyRewardCount > 0
                ? new QuestItemReward(row.CurrencyReward.RowId, index.ItemName(row.CurrencyReward.RowId), (int)row.CurrencyRewardCount)
                : null,
            items.Count > 0 ? items : null,
            optional.Count > 0 ? optional : null,
            row.EmoteReward.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.EmoteReward.ValueNullable?.Name ?? default)) : null,
            row.ActionReward.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.ActionReward.ValueNullable?.Name ?? default)) : null,
            generalActions.Count > 0 ? generalActions : null,
            instance,
            row.OtherReward.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.OtherReward.ValueNullable?.Name ?? default)) : null);
    }

    private string? GenreName(uint id) =>
        id != 0 && index.Row<Sheets.JournalGenre>(id) is { } g ? GameDataIndex.NullIfEmpty(SheetJson.Text(g.Name)) : null;

    private string? ExpansionName(uint id) =>
        index.Row<Sheets.ExVersion>(id) is { } e ? GameDataIndex.NullIfEmpty(SheetJson.Text(e.Name)) : null;
}

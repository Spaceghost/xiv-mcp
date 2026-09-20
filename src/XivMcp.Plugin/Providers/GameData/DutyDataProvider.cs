using System.Reflection;
using System.Text.RegularExpressions;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Duty (ContentFinderCondition) data: dungeons, trials, raids, PvP, etc.</summary>
[McpProvider("gamedata")]
public sealed partial class DutyDataProvider
{
    /// <summary>ContentFinderCondition bool columns that mark roulette membership.</summary>
    internal static readonly PropertyInfo[] RouletteColumns = typeof(Sheets.ContentFinderCondition)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(bool) && (p.Name.EndsWith("Roulette", StringComparison.Ordinal) || p.Name == "DailyFrontlineChallenge"))
        .ToArray();

    private readonly GameDataIndex index;

    public DutyDataProvider(IGameDataSource data) => index = GameDataIndex.For(data);

    [McpTool("search_duties",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:ContentFinderCondition"],
        Title = "Search duties",
        Description =
            "Searches duties from the Duty Finder data (ContentFinderCondition: dungeons, guildhests, trials, raids, alliance raids, PvP, " +
            "deep dungeons, variant/criterion, etc.) by name (ranked exact > prefix > word > substring; numeric query matches the id). " +
            "Filter by contentType (name such as \"Dungeons\", \"Trials\", \"Raids\", \"Ultimate Raids\" or its id) and required level range. " +
            "Query may be omitted when a filter is set. Returns {total, offset, returned, truncated, results:[{id, name, type, level, " +
            "itemLevelRequired, levelSync, itemLevelSync, partySize, highEnd, pvp}]}. Use get_duty for unlock quest, roulettes and composition.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<DutySummary> SearchDuties(
        [McpParam("Duty name text (any case).")] string? query = null,
        [McpParam("Content type name (partial ok, e.g. \"Dungeon\", \"Trial\", \"Raid\") or ContentType id.")] string? contentType = null,
        [McpParam("Minimum required class/job level.", Minimum = 0)] int? minLevel = null,
        [McpParam("Maximum required class/job level.", Minimum = 0)] int? maxLevel = null,
        [McpParam("Maximum results (1-500).", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var filters = new List<Func<GameDataIndex.DutyEntry, bool>>();
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var types = ResolveContentTypes(contentType);
            if (types.Count == 0) throw new McpToolException($"No content type matches \"{contentType}\". Examples: Dungeons, Trials, Raids, Guildhests, PvP.");
            filters.Add(d => types.Contains(d.ContentType));
        }

        if (minLevel is { } min) filters.Add(d => d.Level >= min);
        if (maxLevel is { } max) filters.Add(d => d.Level <= max);
        if (string.IsNullOrWhiteSpace(query) && filters.Count == 0)
            throw new McpToolException("Provide a query or at least one filter.");

        var page = string.IsNullOrWhiteSpace(query)
            ? Page<GameDataIndex.DutyEntry>.From(index.Duties.Where(d => filters.All(f => f(d))).OrderBy(d => d.Level).ThenBy(d => d.ItemLevel).ThenBy(d => d.Id).ToList(), offset, limit)
            : TextSearch.Search(index.Duties, d => d.Id, d => d.Lower, query, filters.Count == 0 ? null : d => filters.All(f => f(d)), offset, limit);

        var results = new List<DutySummary>();
        foreach (var entry in page.Items)
        {
            if (index.Row<Sheets.ContentFinderCondition>(entry.Id) is not { } row) continue;
            results.Add(new DutySummary(
                entry.Id,
                entry.Name,
                ContentTypeName(row.ContentType.RowId),
                row.ClassJobLevelRequired,
                row.ItemLevelRequired,
                row.ClassJobLevelSync,
                row.ItemLevelSync,
                PartySize(row),
                row.HighEndDuty,
                row.PvP));
        }

        return new PagedResult<DutySummary>(page.Total, page.Offset, results.Count, page.Truncated, results);
    }

    [McpTool("get_duty",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:ContentFinderCondition", "lumina:Quest"],
        Title = "Get duty details",
        Description =
            "Details for one duty (ContentFinderCondition id): name, description, content type, required level and item level, level/item-level sync, " +
            "party size and role composition (tanks/healers/dps per party, number of parties), high-end/PvP flags, undersized and explorer-mode " +
            "availability, whether it is in the Duty Finder, required expansion, allowed jobs, territory and zone, the quest that unlocks it " +
            "(when resolvable from game data), and the Duty Roulettes it belongs to.",
        GameThread = false,
        RequiresLogin = false)]
    public DutyDetail GetDuty([McpParam("ContentFinderCondition id (from search_duties).")] uint contentFinderConditionId)
    {
        var id = contentFinderConditionId;
        if (index.Row<Sheets.ContentFinderCondition>(id) is not { } row || SheetJson.Text(row.Name).Length == 0)
            throw new McpToolException($"Duty {id} not found. Use search_duties to look up duty ids.");

        var members = row.ContentMemberType.ValueNullable;
        var roulettes = RouletteColumns.Where(p => p.GetValue(row) is true).Select(p => Humanize(p.Name)).ToList();

        return new DutyDetail(
            id,
            GameDataIndex.Capitalize(SheetJson.Text(row.Name)),
            row.Transient.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.Transient.ValueNullable?.Description ?? default)) : null,
            row.ContentType.RowId != 0 ? new NamedRef(row.ContentType.RowId, ContentTypeName(row.ContentType.RowId) ?? $"#{row.ContentType.RowId}") : null,
            row.ClassJobLevelRequired,
            row.ItemLevelRequired,
            row.ClassJobLevelSync,
            row.ItemLevelSync,
            PartySize(row),
            members is { } m1 && m1.TanksPerParty > 0 ? m1.TanksPerParty : null,
            members is { } m2 && m2.HealersPerParty > 0 ? m2.HealersPerParty : null,
            members is { } m3 && m3.MeleesPerParty + m3.RangedPerParty > 0 ? m3.MeleesPerParty + m3.RangedPerParty : null,
            members is { } m4 && m4.PartyCount > 0 ? m4.PartyCount : null,
            row.HighEndDuty,
            row.PvP,
            row.AllowUndersized,
            row.AllowExplorerMode,
            row.IsInDutyFinder,
            index.Row<Sheets.ExVersion>(row.RequiredExVersion.RowId) is { } ex ? GameDataIndex.NullIfEmpty(SheetJson.Text(ex.Name)) : null,
            index.ClassJobCategoryName(row.AcceptClassJobCategory.RowId),
            row.TerritoryType.RowId,
            index.TerritoryName(row.TerritoryType.RowId),
            UnlockQuest(row),
            roulettes);
    }

    [McpResourceTemplate("ffxiv://duty/{dutyId}",
        Name = "Duty",
        Description = "Game-data record for a duty (ContentFinderCondition id; same content as the get_duty tool).",
        GameThread = false,
        RequiresLogin = false)]
    public DutyDetail DutyResource(uint dutyId) => GetDuty(dutyId);

    internal NamedRef? UnlockQuest(Sheets.ContentFinderCondition row)
    {
        foreach (var criteria in new[] { row.UnlockCriteria, row.UnlockCriteria2 })
        {
            if (criteria.RowId != 0 && criteria.Is<Sheets.Quest>() && index.QuestName(criteria.RowId) is { } name)
                return new NamedRef(criteria.RowId, name);
        }

        if (row.Content.RowId != 0 && row.Content.Is<Sheets.InstanceContent>() &&
            index.InstanceUnlockQuests.TryGetValue(row.Content.RowId, out var questId) && index.QuestName(questId) is { } questName)
            return new NamedRef(questId, questName);

        return null;
    }

    private static int PartySize(Sheets.ContentFinderCondition row)
    {
        if (row.ContentMemberType.ValueNullable is { } m && m.MembersPerParty > 0)
            return m.MembersPerParty * Math.Max(1, (int)m.PartyCount);
        return row.QueueMaxPlayers;
    }

    private string? ContentTypeName(uint id) =>
        id != 0 && index.Row<Sheets.ContentType>(id) is { } t ? GameDataIndex.NullIfEmpty(SheetJson.Text(t.Name)) : null;

    private HashSet<uint> ResolveContentTypes(string text)
    {
        var t = text.Trim();
        var sheet = index.Sheet<Sheets.ContentType>();
        if (uint.TryParse(t, out var id)) return sheet.HasRow(id) ? [id] : [];
        var exact = new HashSet<uint>();
        var partial = new HashSet<uint>();
        foreach (var row in sheet)
        {
            var name = SheetJson.Text(row.Name);
            if (name.Length == 0) continue;
            if (name.Equals(t, StringComparison.OrdinalIgnoreCase) || name.Equals(t + "s", StringComparison.OrdinalIgnoreCase)) exact.Add(row.RowId);
            else if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) partial.Add(row.RowId);
        }

        return exact.Count > 0 ? exact : partial;
    }

    internal static string Humanize(string column)
    {
        var name = column.EndsWith("Roulette", StringComparison.Ordinal) ? column[..^"Roulette".Length] : column;
        return CamelBoundary().Replace(name, " $1").Trim();
    }

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
}

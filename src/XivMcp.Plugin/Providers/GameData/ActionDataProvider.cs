using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Combat/crafting/gathering action data (Action + ActionTransient sheets).</summary>
[McpProvider("gamedata")]
public sealed class ActionDataProvider
{
    private readonly GameDataIndex index;

    public ActionDataProvider(IGameDataSource data) => index = GameDataIndex.For(data);

    [McpTool("search_actions",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Action"],
        Title = "Search actions",
        Description =
            "Searches actions players can learn (weaponskills, spells, abilities, role actions, gathering abilities, PvP actions; from the Action sheet — " +
            "crafting actions such as Basic Synthesis live in the CraftAction sheet, see get_sheet_row) by name, " +
            "ranked exact > prefix > word > substring; a numeric query matches the action id. classJob (abbreviation like DRG, SGE, WHM or a name) keeps " +
            "actions that class/job can use, including role actions and its base class's actions. PvP actions are excluded unless includePvp is true. " +
            "Returns {total, offset, returned, truncated, results:[{id, name, classJob, level, category, isRoleAction, isPvp}]} sorted by relevance " +
            "(or by level when no query is given). Use get_action for description, cast/recast, range and cost.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<ActionSummary> SearchActions(
        [McpParam("Action name text (any case). Optional when classJob is given.")] string? query = null,
        [McpParam("Class/job abbreviation or name, e.g. \"NIN\", \"Red Mage\".")] string? classJob = null,
        [McpParam("Include PvP-only actions.")] bool includePvp = false,
        [McpParam("Maximum results (1-500).", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        GameDataIndex.ClassJobEntry? job = null;
        if (!string.IsNullOrWhiteSpace(classJob))
        {
            job = index.FindClassJob(classJob)
                  ?? throw new McpToolException($"Unknown class/job \"{classJob}\". Use an abbreviation such as PLD, BLM, SCH or VPR.");
        }

        if (string.IsNullOrWhiteSpace(query) && job == null)
            throw new McpToolException("Provide a query or classJob.");

        var categoryCache = new Dictionary<uint, bool>();
        bool Filter(GameDataIndex.ActionEntry a)
        {
            if (!includePvp && a.IsPvp) return false;
            if (job == null) return true;
            // Category 1 ("All Classes") holds content-specific actions (duty/field actions), not class kits.
            if (a.ClassJobCategory == 1 && a.ClassJob == 0) return false;
            if (a.ClassJob == job.Id || (job.ParentId != 0 && a.ClassJob == job.ParentId)) return true;
            if (a.ClassJobCategory == 0) return false;
            if (!categoryCache.TryGetValue(a.ClassJobCategory, out var ok))
                categoryCache[a.ClassJobCategory] = ok = index.CategoryIncludes(a.ClassJobCategory, job);
            return ok;
        }

        // Without a query every match ranks equally; list in level order instead of name length.
        var page = string.IsNullOrWhiteSpace(query)
            ? Page<GameDataIndex.ActionEntry>.From(index.Actions.Where(Filter).OrderBy(a => a.Level).ThenBy(a => a.Id).ToList(), offset, limit)
            : TextSearch.Search(index.Actions, a => a.Id, a => a.Lower, query, Filter, offset, limit);

        var results = page.Items.Select(a => new ActionSummary(
                a.Id,
                a.Name,
                (a.ClassJob != 0 ? index.ClassJob(a.ClassJob)?.Abbreviation : null) ?? index.ClassJobCategoryName(a.ClassJobCategory),
                a.Level,
                a.Category != 0 && index.Row<Sheets.ActionCategory>(a.Category) is { } c ? GameDataIndex.NullIfEmpty(SheetJson.Text(c.Name)) : null,
                a.IsRole,
                a.IsPvp))
            .ToList();
        return new PagedResult<ActionSummary>(page.Total, page.Offset, results.Count, page.Truncated, results);
    }

    [McpTool("get_action",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Action"],
        Title = "Get action details",
        Description =
            "Details for one action id: name, tooltip description (plain text; dynamic values such as potency may appear as placeholders), icon, " +
            "class/job and which classes/jobs can use it, level acquired, category (Spell, Weaponskill, Ability...), cast and recast time in seconds, " +
            "range and effect radius in yalms, cast type, primary cost type/value (raw sheet values), cooldown group ids (actions sharing a group share " +
            "a recast timer; group 58 is the global cooldown), max charges, role/PvP/player flags, the combo action it follows, and targeting flags.",
        GameThread = false,
        RequiresLogin = false)]
    public ActionDetail GetAction([McpParam("Action id (row id in the Action sheet).")] uint actionId)
    {
        if (index.Row<Sheets.Action>(actionId) is not { } row || SheetJson.Text(row.Name).Length == 0)
            throw new McpToolException($"Action {actionId} not found. Use search_actions to look up action ids.");

        var description = index.Row<Sheets.ActionTransient>(actionId) is { } t ? GameDataIndex.NullIfEmpty(SheetJson.Text(t.Description)) : null;
        var job = row.ClassJob.RowId is not (0 or uint.MaxValue) ? index.ClassJob(row.ClassJob.RowId) : null;
        var combo = row.ActionCombo.RowId != 0 && row.ActionCombo.ValueNullable is { } comboRow
            ? new NamedRef(row.ActionCombo.RowId, SheetJson.Text(comboRow.Name))
            : null;

        return new ActionDetail(
            actionId,
            SheetJson.Text(row.Name),
            description,
            row.Icon,
            job != null ? new NamedRef(job.Id, job.Name) : null,
            job?.Abbreviation,
            index.ClassJobCategoryName(row.ClassJobCategory.RowId),
            row.ClassJobLevel,
            row.ActionCategory.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.ActionCategory.ValueNullable?.Name ?? default)) : null,
            row.Cast100ms / 10.0,
            row.Recast100ms / 10.0,
            row.Range,
            row.EffectRange,
            row.CastType,
            row.PrimaryCostType,
            row.PrimaryCostValue,
            row.CooldownGroup,
            row.AdditionalCooldownGroup,
            row.MaxCharges,
            row.IsRoleAction,
            row.IsPvP,
            row.IsPlayerAction,
            combo,
            row.CanTargetSelf,
            row.CanTargetParty,
            row.CanTargetHostile,
            row.TargetArea);
    }
}

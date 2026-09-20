using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Actions;

namespace XivMcp.Plugin.Providers.Ui;

/// <summary>open_game_window: opens one of the game's own windows for the player. It never clicks inside one.</summary>
[McpProvider("ui")]
public sealed class GameWindowProvider
{
    private readonly IDataManager dataManager;
    private readonly ICondition condition;

    public GameWindowProvider(IDataManager dataManager, ICondition condition)
    {
        this.dataManager = dataManager;
        this.condition = condition;
    }

    [McpTool("open_game_window",
        Sources = ["client:AgentModule", "client:AgentMap", "client:AgentQuestJournal", "client:AgentRecipeNote", "client:AgentGatheringNote", "client:AgentContentsFinder", "client:AgentAchievement", "lumina:Map", "lumina:Quest", "lumina:Recipe", "lumina:Item", "lumina:GatheringItem", "lumina:ContentFinderCondition", "lumina:Achievement"],
        ApprovalSummary = "Open the game's {kind} window (on id {id}, territory {territoryId}).",
        RequiresApproval = true,
        Title = "Open a game window",
        Description =
            "Opens one of the game's own windows so the player can look at it; it never clicks, selects, registers, crafts or buys anything inside the window. " +
            "kind: map (id = Map row id, or territoryId = zone whose main map to show; neither = current map), journal (id = Quest row id to show that quest), recipe (Crafting Log; id = Recipe row id), recipeSearch (Crafting Log searched for an item; id = Item row id, required), gatheringLog (id = Item row id of a gatherable), teleport, character, armoury, inventory, dutyFinder (id = ContentFinderCondition row id to show that duty; it is not joined), achievements (id = Achievement row id), currency, gearsets, macros. " +
            "Ids are checked against the game data first (not_found). Refused while in combat, casting, crafting, gathering, in a cutscene or event, or changing zones. If the window is already open it is brought to the front (alreadyOpen=true) instead of being toggled shut. " +
            "The game decides what the window shows: a recipe or duty the character has not unlocked may open the window without the entry. Returns kind, id and the client function used. For a flag on the map use set_map_flag; for item data use the read tools (there is no standalone item window to open).",
        Permission = ToolPermission.Ui, Idempotent = true)]
    public OpenWindowResult OpenGameWindow(
        [McpParam("Which window to open.")] GameWindowKind kind,
        [McpParam("Optional row id whose meaning depends on kind (see the description).", Minimum = 1)] uint? id = null,
        [McpParam("Only for kind=map: TerritoryType row id whose main map to open.", Minimum = 1)] uint? territoryId = null)
    {
        var spec = GameWindows.Validate(kind, id, territoryId);

        uint? mapTerritory = null;
        if (territoryId is { } territory)
        {
            if (!dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var territoryRow) || territoryRow.Map.RowId == 0)
                throw McpToolException.WithCode(McpErrorCodes.NotFound, $"Territory {territory} not found, or it has no map.");
            id = territoryRow.Map.RowId;
            mapTerritory = territory;
        }
        else if (id is { } rowId)
        {
            mapTerritory = EnsureRowExists(spec.IdKind, rowId);
        }

        PlayerGuards.EnsureNotBusy(condition, "open a game window");

        var alreadyOpen = GameWindowOpener.Open(kind, id, mapTerritory);
        return new OpenWindowResult(GameWindows.Name(kind), id, id.HasValue ? spec.IdApi! : spec.OpenApi, alreadyOpen ? true : null);
    }

    /// <summary>Throws not_found unless the row exists; for maps returns the map's territory.</summary>
    private uint? EnsureRowExists(GameWindowIdKind idKind, uint id)
    {
        uint? territory = null;
        var found = idKind switch
        {
            GameWindowIdKind.Map => MapExists(id, out territory),
            GameWindowIdKind.Quest => dataManager.GetExcelSheet<Quest>().TryGetRow(id, out var quest) && !quest.Name.IsEmpty,
            GameWindowIdKind.Recipe => dataManager.GetExcelSheet<Recipe>().TryGetRow(id, out var recipe) && recipe.ItemResult.RowId != 0,
            GameWindowIdKind.Item => dataManager.GetExcelSheet<Item>().TryGetRow(id, out var item) && !item.Name.IsEmpty,
            GameWindowIdKind.GatherableItem => dataManager.GetExcelSheet<GatheringItem>().Any(g => g.Item.RowId == id),
            GameWindowIdKind.Duty => dataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(id, out var duty) && !duty.Name.IsEmpty,
            GameWindowIdKind.Achievement => dataManager.GetExcelSheet<Achievement>().TryGetRow(id, out var achievement) && !achievement.Name.IsEmpty,
            _ => false,
        };
        if (!found)
            throw McpToolException.WithCode(McpErrorCodes.NotFound, $"id {id} is not a valid {GameWindows.IdMeaning(idKind)}. Look it up with the game data search tools first.");
        return territory;
    }

    private bool MapExists(uint id, out uint? territory)
    {
        territory = null;
        if (!dataManager.GetExcelSheet<Map>().TryGetRow(id, out var map))
            return false;
        territory = map.TerritoryType.RowId;
        return true;
    }

    public sealed record OpenWindowResult(string Kind, uint? Id, string Api, bool? AlreadyOpen);
}

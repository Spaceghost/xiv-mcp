using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Ui;

/// <summary>The game windows open_game_window can open.</summary>
public enum GameWindowKind
{
    Map,
    Journal,
    Recipe,
    RecipeSearch,
    GatheringLog,
    Teleport,
    Character,
    Armoury,
    Inventory,
    DutyFinder,
    Achievements,
    Currency,
    Gearsets,
    Macros,
}

/// <summary>What the optional id argument of open_game_window means for a window.</summary>
public enum GameWindowIdKind
{
    /// <summary>The window takes no id.</summary>
    None,

    /// <summary>Map sheet row id.</summary>
    Map,

    /// <summary>Quest sheet row id (65536 and up, as in the quest tools).</summary>
    Quest,

    /// <summary>Recipe sheet row id.</summary>
    Recipe,

    /// <summary>Item sheet row id.</summary>
    Item,

    /// <summary>Item sheet row id of something that can be gathered.</summary>
    GatherableItem,

    /// <summary>ContentFinderCondition sheet row id.</summary>
    Duty,

    /// <summary>Achievement sheet row id.</summary>
    Achievement,
}

/// <param name="Kind">The window.</param>
/// <param name="Agent">Name of the client's AgentId member that owns the window.</param>
/// <param name="OpenApi">The client function used when no id is given.</param>
/// <param name="IdKind">What id means for this window.</param>
/// <param name="IdRequired">Whether the window cannot be opened without an id.</param>
/// <param name="IdApi">The client function used when an id is given.</param>
public sealed record GameWindowSpec(GameWindowKind Kind, string Agent, string OpenApi, GameWindowIdKind IdKind, bool IdRequired, string? IdApi);

/// <summary>The window kind → client API table and the argument rules of open_game_window (pure; unit tested on the host).</summary>
public static class GameWindows
{
    private const string Show = "AgentInterface.Show";

    public static readonly IReadOnlyList<GameWindowSpec> All =
    [
        new(GameWindowKind.Map, "Map", Show, GameWindowIdKind.Map, false, "AgentMap.OpenMapByMapId"),
        new(GameWindowKind.Journal, "QuestJournal", Show, GameWindowIdKind.Quest, false, "AgentQuestJournal.OpenForQuest"),
        new(GameWindowKind.Recipe, "RecipeNote", Show, GameWindowIdKind.Recipe, false, "AgentRecipeNote.OpenRecipeByRecipeId"),
        new(GameWindowKind.RecipeSearch, "RecipeNote", Show, GameWindowIdKind.Item, true, "AgentRecipeNote.SearchRecipeByItemId"),
        new(GameWindowKind.GatheringLog, "GatheringNote", Show, GameWindowIdKind.GatherableItem, false, "AgentGatheringNote.OpenGatherableByItemId"),
        new(GameWindowKind.Teleport, "Teleport", Show, GameWindowIdKind.None, false, null),
        new(GameWindowKind.Character, "Status", Show, GameWindowIdKind.None, false, null),
        new(GameWindowKind.Armoury, "ArmouryBoard", Show, GameWindowIdKind.None, false, null),
        new(GameWindowKind.Inventory, "Inventory", Show, GameWindowIdKind.None, false, null),
        new(GameWindowKind.DutyFinder, "ContentsFinder", Show, GameWindowIdKind.Duty, false, "AgentContentsFinder.OpenRegularDuty"),
        new(GameWindowKind.Achievements, "Achievement", Show, GameWindowIdKind.Achievement, false, "AgentAchievement.OpenById"),
        new(GameWindowKind.Currency, "Currency", Show, GameWindowIdKind.None, false, null),
        new(GameWindowKind.Gearsets, "GearSet", Show, GameWindowIdKind.None, false, null),
        new(GameWindowKind.Macros, "Macro", Show, GameWindowIdKind.None, false, null),
    ];

    public static GameWindowSpec Spec(GameWindowKind kind) =>
        All.FirstOrDefault(s => s.Kind == kind)
        ?? throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"Unknown window '{kind}'.");

    /// <summary>Checks the id / territoryId combination for a window and returns its row of the table.</summary>
    public static GameWindowSpec Validate(GameWindowKind kind, uint? id, uint? territoryId)
    {
        var spec = Spec(kind);
        var name = Name(kind);
        if (territoryId.HasValue && kind != GameWindowKind.Map)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"territoryId is only used with kind=map; {name} does not take it.");
        if (territoryId.HasValue && id.HasValue)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Pass either id (a Map row id) or territoryId (the zone whose main map to open), not both.");
        if (territoryId == 0)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "territoryId 0 is not a zone.");
        if (id.HasValue && spec.IdKind == GameWindowIdKind.None)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"{name} does not take an id; it just opens.");
        if (id == 0)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "id 0 is not a valid row id.");
        if (!id.HasValue && spec.IdRequired)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"{name} needs id ({IdMeaning(spec.IdKind)}).");
        if (spec.IdKind == GameWindowIdKind.GatherableItem && id > ushort.MaxValue)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"Item {id} cannot be shown in the Gathering Log (the client takes a 16-bit item id there).");
        return spec;
    }

    /// <summary>The quest number the client uses internally: the Quest row id without its 0x10000 base.</summary>
    public static uint ClientQuestId(uint questRowId) => questRowId & 0xFFFF;

    public static string Name(GameWindowKind kind)
    {
        var text = kind.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    public static string IdMeaning(GameWindowIdKind kind) => kind switch
    {
        GameWindowIdKind.Map => "Map row id",
        GameWindowIdKind.Quest => "Quest row id",
        GameWindowIdKind.Recipe => "Recipe row id",
        GameWindowIdKind.Item => "Item row id",
        GameWindowIdKind.GatherableItem => "Item row id of a gatherable item",
        GameWindowIdKind.Duty => "ContentFinderCondition row id",
        GameWindowIdKind.Achievement => "Achievement row id",
        _ => "none",
    };
}

namespace XivMcp.Plugin.Providers.GameData;

// Output DTOs for get_recipe_tree, get_item_sources and get_item_uses.

/// <summary>One paged list inside a larger answer: total is the full count, truncated tells whether more remain past this page.</summary>
public sealed record SourceSection<T>(int Total, bool Truncated, IReadOnlyList<T> Results);

public sealed record TradeInUse(
    string? Shop,
    uint ShopId,
    uint Amount,
    IReadOnlyList<ExchangeCost> Gives,
    IReadOnlyList<ExchangeCost> OtherCosts,
    IReadOnlyList<string> Npcs);

public sealed record QuestRewardSource(uint QuestId, string Name, int Level, int Count, bool Optional);

public sealed record AchievementSource(uint AchievementId, string Name, string? Description);

public sealed record GcSealSource(uint Seals, uint RequiredRankId);

public sealed record VentureSource(uint RetainerTaskId, int RetainerLevel, string? Jobs, int VentureCost, int MaxMinutes, IReadOnlyList<int> Quantities);

public sealed record ItemSourcesResult(
    uint ItemId,
    string Name,
    IReadOnlyList<string> Kinds,
    uint? GilPrice,
    IReadOnlyList<RecipeRef> Recipes,
    IReadOnlyList<GatheringSource> Gathering,
    SourceSection<VendorSource> Vendors,
    SourceSection<ExchangeSource> Exchanges,
    GcSealSource? GcSeals,
    SourceSection<VentureSource> Ventures,
    SourceSection<QuestRewardSource> Quests,
    SourceSection<AchievementSource> Achievements,
    string NotCovered);

public sealed record LeveUse(uint LeveId, string Name, int Level, string? Jobs, int Count, int Repeats);

public sealed record GcSupplyUse(int Level, string Job, int Count);

public sealed record ItemUsesResult(
    uint ItemId,
    string Name,
    SourceSection<RecipeRef> Recipes,
    SourceSection<TradeInUse> TradeIns,
    SourceSection<LeveUse> Leves,
    IReadOnlyList<GcSupplyUse> GcSupply,
    bool Desynthesizable,
    bool AetherialReducible,
    bool IsCollectable,
    bool IsCrystal,
    bool IsMarketable,
    bool IsUntradable,
    uint VendorSellPrice,
    string NotCovered);

// ---- recipe tree ----

public sealed record RecipeTreeNode(
    uint ItemId,
    string Name,
    int Quantity,
    bool IsCrystal,
    uint? RecipeId = null,
    string? CraftType = null,
    int? Level = null,
    int? Stars = null,
    int? Crafts = null,
    int? Yield = null,
    string? NotExpanded = null,
    IReadOnlyList<RecipeTreeNode>? Ingredients = null);

public sealed record RecipeTreeCraft(
    uint ItemId,
    string Name,
    uint RecipeId,
    string CraftType,
    int Level,
    int? Stars,
    int Crafts,
    int Needed,
    int Surplus);

public sealed record CrafterRequirement(string CraftType, int Level, int? Stars);

public sealed record GatheringHint(string? Type, int Level, string? Zone, string? Place, string? Timed, int TotalPoints);

public sealed record RawMaterial(
    uint ItemId,
    string Name,
    int Quantity,
    IReadOnlyList<string> Sources,
    uint? GilPriceEach = null,
    long? GilTotal = null,
    VendorSource? Vendor = null,
    GatheringHint? Gathering = null,
    ExchangeSource? Exchange = null,
    uint? GcSealsEach = null,
    uint? CraftableRecipeId = null);

public sealed record RecipeTreeResult(
    uint RecipeId,
    uint ItemId,
    string ItemName,
    string CraftType,
    int Level,
    int? Stars,
    int Yield,
    int Quantity,
    int Crafts,
    int Surplus,
    int MaxDepth,
    int NodeCount,
    bool DepthLimited,
    bool NodeCapHit,
    bool HasCycle,
    IReadOnlyList<RecipeTreeNode>? Tree,
    IReadOnlyList<RecipeTreeCraft> CraftOrder,
    IReadOnlyList<CrafterRequirement> CrafterRequirements,
    IReadOnlyList<RawMaterial> RawMaterials,
    IReadOnlyList<MaterialLine> Crystals,
    long GilForVendorMaterials,
    IReadOnlyList<RecipeRef>? AlternativeRecipes,
    string Note);

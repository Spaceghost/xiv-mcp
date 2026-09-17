using System.Text.Json.Nodes;

namespace XivMcp.Plugin.Providers.GameData;

// Output DTOs for the game-data (Lumina) tools. Serialized camelCase with nulls omitted.

public sealed record NamedRef(uint Id, string Name);

public sealed record MapLocation(
    uint TerritoryId,
    string? Zone,
    uint MapId,
    double? X,
    double? Y,
    string? Region);

public sealed record PagedResult<T>(
    int Total,
    int Offset,
    int Returned,
    bool Truncated,
    IReadOnlyList<T> Results,
    string? Note = null);

// ---- items ----

public sealed record ItemSummary(
    uint Id,
    string Name,
    string? Category,
    int ItemLevel,
    int EquipLevel,
    string? EquipSlot,
    string? Jobs,
    int Rarity,
    bool IsMarketable,
    bool IsCraftable,
    bool IsGatherable,
    bool IsUntradable,
    uint Icon);

public sealed record ItemStat(string Name, int Value, int? HqValue);

public sealed record VendorSource(
    string Npc,
    uint NpcId,
    string? Shop,
    uint ShopId,
    uint PriceGil,
    MapLocation? Location);

public sealed record ExchangeCost(uint ItemId, string Name, uint Count);

public sealed record ExchangeSource(
    string? Shop,
    uint ShopId,
    uint ReceiveCount,
    IReadOnlyList<ExchangeCost> Costs,
    IReadOnlyList<string> Npcs);

public sealed record RecipeRef(
    uint RecipeId,
    uint ItemId,
    string ItemName,
    string CraftType,
    int Level,
    int? Stars = null,
    int? Quantity = null,
    int? Yield = null);

public sealed record GatheringPointRef(string? Zone, string? Place, int Level, string? Type, uint TerritoryId);

public sealed record GatheringSource(
    uint GatheringItemId,
    int ItemLevel,
    int Stars,
    bool Hidden,
    int PerceptionRequired,
    int TotalPoints,
    IReadOnlyList<GatheringPointRef> Points);

public sealed record ItemDetail(
    uint Id,
    string Name,
    string? Description,
    uint Icon,
    NamedRef? Category,
    NamedRef? MarketCategory,
    int ItemLevel,
    int EquipLevel,
    NamedRef? Jobs,
    IReadOnlyList<string>? EquipSlots,
    int Rarity,
    uint StackSize,
    bool IsUnique,
    bool IsUntradable,
    bool IsMarketable,
    bool CanBeHq,
    bool IsCollectable,
    bool IsGlamourous,
    bool IsCrestWorthy,
    int DyeCount,
    int MateriaSlots,
    bool AdvancedMeldingPermitted,
    uint VendorBuyPrice,
    uint VendorSellPrice,
    bool Desynthesizable,
    bool AetherialReducible,
    string? RepairClass,
    string? ItemSeries,
    int? PhysicalDamage,
    int? MagicDamage,
    double? DelaySeconds,
    int? Defense,
    int? MagicDefense,
    int? Block,
    int? BlockRate,
    IReadOnlyList<ItemStat>? Stats,
    string? SpecialBonus,
    IReadOnlyList<VendorSource>? Vendors,
    int VendorTotal,
    IReadOnlyList<ExchangeSource>? Exchanges,
    int ExchangeTotal,
    IReadOnlyList<RecipeRef>? CraftedBy,
    IReadOnlyList<RecipeRef>? UsedIn,
    int UsedInTotal,
    IReadOnlyList<GatheringSource>? Gathering);

// ---- recipes ----

public sealed record RecipeSummary(
    uint RecipeId,
    uint ItemId,
    string ItemName,
    string CraftType,
    int Level,
    int Stars,
    int Yield,
    bool IsExpert,
    bool RequiresSpecialist,
    string? MasterBook);

public sealed record IngredientNode(
    uint ItemId,
    string Name,
    int Quantity,
    bool IsCrystal,
    uint? RecipeId = null,
    string? CraftType = null,
    int? Crafts = null,
    int? Yield = null,
    IReadOnlyList<IngredientNode>? Ingredients = null);

public sealed record MaterialLine(uint ItemId, string Name, int Quantity, bool IsCrystal);

public sealed record CraftStep(uint ItemId, string Name, uint RecipeId, string CraftType, int Level, int Crafts, int Quantity);

public sealed record RecipeDetail(
    uint RecipeId,
    uint ItemId,
    string ItemName,
    int Yield,
    string CraftType,
    int Level,
    uint RecipeLevel,
    int Stars,
    int Durability,
    int Difficulty,
    int Quality,
    int RequiredCraftsmanship,
    int RequiredControl,
    int SuggestedCraftsmanship,
    bool CanHq,
    bool CanQuickSynth,
    bool IsExpert,
    bool RequiresSpecialist,
    string? MasterBook,
    uint? MasterBookItemId,
    string? RequiredItem,
    string? RequiredStatus,
    int Depth,
    IReadOnlyList<IngredientNode> Ingredients,
    IReadOnlyList<MaterialLine> RawMaterials,
    IReadOnlyList<CraftStep> IntermediateCrafts,
    IReadOnlyList<RecipeSummary>? AlternativeRecipes);

// ---- actions ----

public sealed record ActionSummary(
    uint Id,
    string Name,
    string? ClassJob,
    int Level,
    string? Category,
    bool IsRoleAction,
    bool IsPvp);

public sealed record ActionDetail(
    uint Id,
    string Name,
    string? Description,
    uint Icon,
    NamedRef? ClassJob,
    string? ClassJobAbbreviation,
    string? UsableBy,
    int Level,
    string? Category,
    double CastSeconds,
    double RecastSeconds,
    int Range,
    int Radius,
    int CastType,
    int PrimaryCostType,
    int PrimaryCostValue,
    int CooldownGroup,
    int AdditionalCooldownGroup,
    int MaxCharges,
    bool IsRoleAction,
    bool IsPvp,
    bool IsPlayerAction,
    NamedRef? ComboFrom,
    bool CanTargetSelf,
    bool CanTargetParty,
    bool CanTargetHostile,
    bool TargetsArea);

// ---- quests ----

public sealed record QuestSummary(uint Id, string Name, int Level, string? Genre, string? Expansion);

public sealed record QuestItemReward(uint ItemId, string Name, int Count, bool? Hq = null);

public sealed record QuestRewards(
    uint Gil,
    QuestItemReward? Currency,
    IReadOnlyList<QuestItemReward>? Items,
    IReadOnlyList<QuestItemReward>? OptionalItems,
    string? Emote,
    string? Action,
    IReadOnlyList<string>? GeneralActions,
    string? InstanceContentUnlock,
    string? Other);

public sealed record QuestDetail(
    uint Id,
    string Name,
    string? InternalId,
    int Level,
    string? ClassJobs,
    string? Expansion,
    string? Genre,
    string? Category,
    string? Section,
    string? PlaceName,
    uint? IssuerNpcId,
    string? Issuer,
    MapLocation? IssuerLocation,
    IReadOnlyList<NamedRef> PreviousQuests,
    string PreviousQuestsJoin,
    IReadOnlyList<NamedRef> LockedBy,
    string? ClassJobUnlock,
    string? GrandCompany,
    string? BeastTribe,
    bool IsRepeatable,
    QuestRewards Rewards);

// ---- duties ----

public sealed record DutySummary(
    uint Id,
    string Name,
    string? Type,
    int Level,
    int ItemLevelRequired,
    int LevelSync,
    int ItemLevelSync,
    int PartySize,
    bool HighEnd,
    bool Pvp);

public sealed record DutyDetail(
    uint Id,
    string Name,
    string? Description,
    NamedRef? Type,
    int Level,
    int ItemLevelRequired,
    int LevelSync,
    int ItemLevelSync,
    int PartySize,
    int? Tanks,
    int? Healers,
    int? Dps,
    int? Parties,
    bool HighEnd,
    bool Pvp,
    bool AllowUndersized,
    bool AllowExplorerMode,
    bool InDutyFinder,
    string? Expansion,
    string? Jobs,
    uint TerritoryId,
    string? Zone,
    NamedRef? UnlockQuest,
    IReadOnlyList<string> Roulettes);

// ---- sheets ----

/// <summary>Columns are "Name: type" strings, e.g. "ClassJob: RowRef&lt;ClassJob&gt;".</summary>
public sealed record SheetInfo(string Name, uint? RowCount, bool? Subrows, IReadOnlyList<string>? Columns, bool Typed);

public sealed record SheetRowResult(string Sheet, uint RowId, ushort? SubrowId, int? SubrowCount, JsonObject Row);

public sealed record SheetMatch(uint RowId, ushort? SubrowId, string? Label, JsonNode? Value);

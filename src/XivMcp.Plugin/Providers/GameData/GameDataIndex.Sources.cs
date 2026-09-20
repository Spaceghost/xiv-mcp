using System.Collections.Concurrent;
using System.Collections.Frozen;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

// Reverse lookups behind get_recipe_tree, get_item_sources, get_item_uses and the zone tools. Each is built on first
// use (never by the warmup thread) and shared like the indexes in GameDataIndex.cs.
internal sealed partial class GameDataIndex
{
    private static Cached<T> Ensure<T>(ref Cached<T>? slot, Func<T> build)
        where T : class =>
        LazyInitializer.EnsureInitialized(ref slot, () => new Cached<T>(build));

    // ------------------------------------------------------------------ recipes as plain records

    private readonly ConcurrentDictionary<uint, RecipeTree.Recipe?> treeRecipes = new();

    /// <summary>A Recipe row as the plain record <see cref="RecipeTree"/> works on; null when the row makes nothing.</summary>
    public RecipeTree.Recipe? TreeRecipe(uint recipeId) => treeRecipes.GetOrAdd(recipeId, id =>
    {
        if (Row<Sheets.Recipe>(id) is not { } row || row.ItemResult.RowId == 0) return null;
        var inputs = new List<RecipeTree.Input>(8);
        for (var i = 0; i < row.Ingredient.Count; i++)
        {
            var item = row.Ingredient[i].RowId;
            var amount = row.AmountIngredient[i];
            if (item != 0 && amount > 0) inputs.Add(new RecipeTree.Input(item, amount));
        }

        var level = row.RecipeLevelTable.ValueNullable;
        return new RecipeTree.Recipe(id, row.ItemResult.RowId, Math.Max(1, (int)row.AmountResult), row.CraftType.RowId,
            level?.ClassJobLevel ?? 0, level?.Stars ?? 0, inputs);
    });

    /// <summary>The first recipe that makes an item (the one get_recipe uses), or null.</summary>
    public RecipeTree.Recipe? TreeRecipeForItem(uint itemId) =>
        Recipes.ByResult.TryGetValue(itemId, out var ids) && ids.Length > 0 ? TreeRecipe(ids[0]) : null;

    // ------------------------------------------------------------------ currencies

    private Cached<FrozenDictionary<uint, uint>>? tomestoneItems;

    /// <summary>
    /// SpecialShop cost ids below 10 are currency slots rather than item ids. For UseCurrencyType 2 and 4 they index
    /// TomestonesItem.Tomestones (1 = Poetics, 3 = the weekly-capped tomestone, ...; verified against the Poetics and
    /// Mnemonics exchanges). For UseCurrencyType 16 they are special-currency ids (scrips) that only the running client
    /// maps to items, so they are reported unresolved (itemId 0).
    /// </summary>
    public (uint ItemId, string Name) ResolveCurrency(uint id, byte useCurrencyType)
    {
        if (id >= 10) return (id, ItemName(id));
        if (useCurrencyType is 2 or 4)
        {
            var map = Ensure(ref tomestoneItems, () =>
            {
                var built = new Dictionary<uint, uint>();
                foreach (var t in Sheet<Sheets.TomestonesItem>())
                {
                    if (t.Tomestones.RowId != 0 && t.Item.RowId != 0) built.TryAdd(t.Tomestones.RowId, t.Item.RowId);
                }

                return built.ToFrozenDictionary();
            }).Value;
            if (map.TryGetValue(id, out var item)) return (item, ItemName(item));
        }

        if (useCurrencyType == 16) return (0, $"Special currency #{id} (scrip-type currency; not resolvable from game data)");
        return (id, ItemName(id));
    }

    private Cached<FrozenDictionary<uint, uint[]>>? specialShopsByCost;

    /// <summary>Item id → SpecialShop ids that take it as payment (tomestone slots resolved to their items).</summary>
    public FrozenDictionary<uint, uint[]> SpecialShopsByCost => Ensure(ref specialShopsByCost, () =>
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var shop in Sheet<Sheets.SpecialShop>())
        {
            foreach (var entry in shop.Item)
            {
                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.ItemCost.RowId == 0 || cost.CurrencyCost == 0) continue;
                    var (item, _) = ResolveCurrency(cost.ItemCost.RowId, shop.UseCurrencyType);
                    if (item != 0) Add(map, item, shop.RowId);
                }
            }
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.Distinct().ToArray());
    }).Value;

    // ------------------------------------------------------------------ rewards

    private Cached<FrozenDictionary<uint, uint[]>>? questsByRewardItem;

    /// <summary>Item id → quests that hand it out (guaranteed or as one of the optional rewards).</summary>
    public FrozenDictionary<uint, uint[]> QuestsByRewardItem => Ensure(ref questsByRewardItem, () =>
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var row in Sheet<Sheets.Quest>())
        {
            if (SheetJson.Text(row.Name).Length == 0) continue;
            for (var i = 0; i < row.Reward.Count; i++)
            {
                var reward = row.Reward[i];
                if (reward.RowId != 0 && reward.Is<Sheets.Item>() && i < row.ItemCountReward.Count && row.ItemCountReward[i] > 0)
                    Add(map, reward.RowId, row.RowId);
            }

            foreach (var reward in row.OptionalItemReward)
            {
                if (reward.RowId != 0) Add(map, reward.RowId, row.RowId);
            }
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.Distinct().ToArray());
    }).Value;

    private Cached<FrozenDictionary<uint, uint[]>>? achievementsByItem;

    /// <summary>Item id → achievements that reward it.</summary>
    public FrozenDictionary<uint, uint[]> AchievementsByItem => Ensure(ref achievementsByItem, () =>
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var row in Sheet<Sheets.Achievement>())
        {
            if (row.Item.RowId != 0 && SheetJson.Text(row.Name).Length > 0) Add(map, row.Item.RowId, row.RowId);
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.ToArray());
    }).Value;

    internal sealed record GcSealOffer(uint Cost, uint RequiredRankId);

    private Cached<FrozenDictionary<uint, GcSealOffer>>? gcSealShop;

    /// <summary>Item id → cheapest Grand Company seal price (GCScripShopItem).</summary>
    public FrozenDictionary<uint, GcSealOffer> GcSealShop => Ensure(ref gcSealShop, () =>
    {
        var map = new Dictionary<uint, GcSealOffer>();
        foreach (var shop in SubrowSheet<Sheets.GCScripShopItem>())
        {
            foreach (var entry in shop)
            {
                if (entry.Item.RowId == 0 || entry.CostGCSeals == 0) continue;
                if (map.TryGetValue(entry.Item.RowId, out var existing) && existing.Cost <= entry.CostGCSeals) continue;
                map[entry.Item.RowId] = new GcSealOffer(entry.CostGCSeals, entry.RequiredGrandCompanyRank.RowId);
            }
        }

        return map.ToFrozenDictionary();
    }).Value;

    private Cached<FrozenDictionary<uint, uint[]>>? venturesByItem;

    /// <summary>Item id → RetainerTask ids of the targeted (non-random) ventures that fetch it.</summary>
    public FrozenDictionary<uint, uint[]> VenturesByItem => Ensure(ref venturesByItem, () =>
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var task in Sheet<Sheets.RetainerTask>())
        {
            if (task.IsRandom || task.Task.RowId == 0 || !task.Task.Is<Sheets.RetainerTaskNormal>()) continue;
            if (Row<Sheets.RetainerTaskNormal>(task.Task.RowId) is { } normal && normal.Item.RowId != 0)
                Add(map, normal.Item.RowId, task.RowId);
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.ToArray());
    }).Value;

    // ------------------------------------------------------------------ turn-ins

    private Cached<FrozenDictionary<uint, uint[]>>? craftLevesByItem;

    /// <summary>Item id → CraftLeve ids that ask for it.</summary>
    public FrozenDictionary<uint, uint[]> CraftLevesByItem => Ensure(ref craftLevesByItem, () =>
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var row in Sheet<Sheets.CraftLeve>())
        {
            if (row.Leve.RowId == 0) continue;
            for (var i = 0; i < row.Item.Count; i++)
            {
                if (row.Item[i].RowId != 0 && i < row.ItemCount.Count && row.ItemCount[i] > 0) Add(map, row.Item[i].RowId, row.RowId);
            }
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.Distinct().ToArray());
    }).Value;

    internal sealed record GcSupplyEntry(int Level, uint ClassJobId, int Count);

    private Cached<FrozenDictionary<uint, GcSupplyEntry[]>>? gcSupplyByItem;

    /// <summary>
    /// Item id → Grand Company supply/provisioning missions that can ask for it. GCSupplyDuty rows are class levels and
    /// each row holds 11 slots in ClassJob order from Carpenter (8) to Fisher (18).
    /// </summary>
    public FrozenDictionary<uint, GcSupplyEntry[]> GcSupplyByItem => Ensure(ref gcSupplyByItem, () =>
    {
        var map = new Dictionary<uint, List<GcSupplyEntry>>();
        foreach (var row in Sheet<Sheets.GCSupplyDuty>())
        {
            for (var slot = 0; slot < row.SupplyData.Count; slot++)
            {
                var data = row.SupplyData[slot];
                for (var i = 0; i < data.Item.Count; i++)
                {
                    if (data.Item[i].RowId == 0) continue;
                    var count = i < data.ItemCount.Count ? data.ItemCount[i] : 0;
                    Add(map, data.Item[i].RowId, new GcSupplyEntry((int)row.RowId, (uint)(8 + slot), count));
                }
            }
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.ToArray());
    }).Value;

    // ------------------------------------------------------------------ gathering

    /// <summary>"unspoiled" (rare pop table), "ephemeral" (only exists during set bells) or null for an ordinary node.</summary>
    public string? GatheringTimedKind(uint gatheringPointId)
    {
        if (Row<Sheets.GatheringPointTransient>(gatheringPointId) is not { } transient) return null;
        if (transient.GatheringRarePopTimeTable.RowId != 0) return "unspoiled";
        return GatheringWindows.PackedToSeconds(transient.EphemeralStartTime) is { } start &&
               GatheringWindows.PackedToSeconds(transient.EphemeralEndTime) is { } end && start != end
            ? "ephemeral"
            : null;
    }

    // ------------------------------------------------------------------ zones

    internal sealed record ZoneEntry(
        uint Id,
        string Name,
        string Lower,
        string? Region,
        uint IntendedUse,
        uint DutyId,
        bool IsPvp,
        bool HasWeather,
        uint Expansion);

    private Cached<ZoneEntry[]>? zones;

    /// <summary>Every TerritoryType row that has a place name.</summary>
    public IReadOnlyList<ZoneEntry> Zones => Ensure(ref zones, () =>
    {
        var list = new List<ZoneEntry>(1200);
        foreach (var row in Sheet<Sheets.TerritoryType>())
        {
            var name = SheetJson.Text(row.PlaceName.ValueNullable?.Name ?? default);
            if (name.Length == 0) continue;
            list.Add(new ZoneEntry(
                row.RowId,
                name,
                name.ToLowerInvariant(),
                NullIfEmpty(SheetJson.Text(row.PlaceNameRegion.ValueNullable?.Name ?? default)),
                row.TerritoryIntendedUse.RowId,
                row.ContentFinderCondition.RowId,
                row.IsPvpZone,
                row.WeatherRate.RowId != 0,
                row.ExVersion.RowId));
        }

        return list.ToArray();
    }).Value;

    private Cached<FrozenDictionary<uint, uint[]>>? mapsByTerritory;

    /// <summary>TerritoryType id → Map ids drawn for it (several for multi-floor zones).</summary>
    public FrozenDictionary<uint, uint[]> MapsByTerritory => Ensure(ref mapsByTerritory, () =>
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var row in Sheet<Sheets.Map>())
        {
            if (row.TerritoryType.RowId != 0) Add(map, row.TerritoryType.RowId, row.RowId);
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.ToArray());
    }).Value;

    private Cached<FrozenDictionary<uint, uint[]>>? aetherytesByTerritory;

    /// <summary>TerritoryType id → Aetheryte ids (aetherytes and aethernet shards) placed in it.</summary>
    public FrozenDictionary<uint, uint[]> AetherytesByTerritory => Ensure(ref aetherytesByTerritory, () =>
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var row in Sheet<Sheets.Aetheryte>())
        {
            if (row.RowId != 0 && row.Territory.RowId != 0 && !row.Invisible) Add(map, row.Territory.RowId, row.RowId);
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.ToArray());
    }).Value;
}

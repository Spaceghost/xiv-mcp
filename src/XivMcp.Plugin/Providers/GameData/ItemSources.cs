using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Where an item comes from and where it goes, read from the shared indexes. One implementation behind get_item,
/// get_item_sources, get_item_uses and the raw-material annotations of get_recipe_tree.
/// </summary>
internal sealed class ItemSources(GameDataIndex index)
{
    /// <summary>Gil vendors selling the item: those with a known map location first. Returns the page and the total.</summary>
    public (List<VendorSource> Vendors, int Total) Vendors(Sheets.Item row, int offset, int limit)
    {
        if (!index.GilShopsByItem.TryGetValue(row.RowId, out var shops)) return ([], 0);
        var pairs = new List<(uint Shop, uint Npc)>();
        var seenNpcs = new HashSet<uint>();
        foreach (var shopId in shops)
        {
            if (!index.NpcsByShop.TryGetValue(shopId, out var npcs) || npcs.Length == 0)
            {
                pairs.Add((shopId, 0));
                continue;
            }

            foreach (var npc in npcs)
            {
                if (seenNpcs.Add(npc)) pairs.Add((shopId, npc));
            }
        }

        // Vendors with a known map location first, then by NPC id; build DTOs only for the returned slice.
        var located = pairs
            .Select(p => (p.Shop, p.Npc, Location: p.Npc != 0 ? index.NpcLocation(p.Npc) : null))
            .OrderBy(p => p.Location == null)
            .ThenBy(p => p.Npc == 0)
            .ThenBy(p => p.Npc)
            .Skip(offset)
            .Take(limit);
        var list = located.Select(p => new VendorSource(
                p.Npc != 0 ? index.NpcName(p.Npc) : "(no resolvable NPC)",
                p.Npc,
                index.Row<Sheets.GilShop>(p.Shop) is { } shop ? GameDataIndex.NullIfEmpty(SheetJson.Text(shop.Name)) : null,
                p.Shop,
                row.PriceMid,
                p.Location))
            .ToList();
        return (list, pairs.Count);
    }

    /// <summary>True when some gil shop stocks the item (its price is then Item.PriceMid).</summary>
    public bool SoldForGil(uint itemId) => index.GilShopsByItem.ContainsKey(itemId);

    /// <summary>Special-shop (currency/item exchange) entries that hand the item out.</summary>
    public (List<ExchangeSource> Exchanges, int Total) Exchanges(uint itemId, int offset, int limit) =>
        ShopEntries(itemId, index.SpecialShopsByItem, receives: true, offset, limit, (shop, shopId, entry, npcs) =>
        {
            uint count = 0;
            foreach (var receive in entry.ReceiveItems)
            {
                if (receive.Item.RowId != itemId) continue;
                count = receive.ReceiveCount;
                break;
            }

            return new ExchangeSource(GameDataIndex.NullIfEmpty(SheetJson.Text(shop.Name)), shopId, count, Costs(shop, entry), npcs);
        });

    /// <summary>Special-shop entries that take the item as payment, with what they give in return.</summary>
    public (List<TradeInUse> TradeIns, int Total) TradeIns(uint itemId, int offset, int limit) =>
        ShopEntries(itemId, index.SpecialShopsByCost, receives: false, offset, limit, (shop, shopId, entry, npcs) =>
        {
            var gives = new List<ExchangeCost>();
            foreach (var receive in entry.ReceiveItems)
            {
                if (receive.Item.RowId != 0) gives.Add(new ExchangeCost(receive.Item.RowId, index.ItemName(receive.Item.RowId), receive.ReceiveCount));
            }

            var costs = Costs(shop, entry);
            var amount = costs.FirstOrDefault(c => c.ItemId == itemId)?.Count ?? 0;
            return new TradeInUse(GameDataIndex.NullIfEmpty(SheetJson.Text(shop.Name)), shopId, amount, gives, costs.Where(c => c.ItemId != itemId).ToList(), npcs);
        });

    private (List<T> Entries, int Total) ShopEntries<T>(
        uint itemId,
        IReadOnlyDictionary<uint, uint[]> shopsByItem,
        bool receives,
        int offset,
        int limit,
        Func<Sheets.SpecialShop, uint, Sheets.SpecialShop.ItemStruct, List<string>, T> build)
    {
        var list = new List<T>();
        if (!shopsByItem.TryGetValue(itemId, out var shops)) return (list, 0);
        var total = 0;
        foreach (var shopId in shops)
        {
            if (index.Row<Sheets.SpecialShop>(shopId) is not { } shop) continue;
            List<string>? npcs = null;
            foreach (var entry in shop.Item)
            {
                if (!(receives ? Receives(entry, itemId) : Takes(shop, entry, itemId))) continue;
                total++;
                if (total <= offset || list.Count >= limit) continue;
                npcs ??= ShopNpcs(shopId);
                list.Add(build(shop, shopId, entry, npcs));
            }
        }

        return (list, total);
    }

    private static bool Receives(Sheets.SpecialShop.ItemStruct entry, uint itemId)
    {
        foreach (var receive in entry.ReceiveItems)
        {
            if (receive.Item.RowId == itemId) return true;
        }

        return false;
    }

    private bool Takes(Sheets.SpecialShop shop, Sheets.SpecialShop.ItemStruct entry, uint itemId)
    {
        foreach (var cost in entry.ItemCosts)
        {
            if (cost.ItemCost.RowId == 0 || cost.CurrencyCost == 0) continue;
            if (index.ResolveCurrency(cost.ItemCost.RowId, shop.UseCurrencyType).ItemId == itemId) return true;
        }

        return false;
    }

    private List<ExchangeCost> Costs(Sheets.SpecialShop shop, Sheets.SpecialShop.ItemStruct entry)
    {
        var costs = new List<ExchangeCost>();
        foreach (var cost in entry.ItemCosts)
        {
            if (cost.ItemCost.RowId == 0 || cost.CurrencyCost == 0) continue;
            var (costItem, costName) = index.ResolveCurrency(cost.ItemCost.RowId, shop.UseCurrencyType);
            costs.Add(new ExchangeCost(costItem, costName, cost.CurrencyCost));
        }

        return costs;
    }

    private List<string> ShopNpcs(uint shopId) => index.NpcsByShop.TryGetValue(shopId, out var ids)
        ? ids.Take(3).Select(n => index.NpcLocation(n) is { Zone: { } zone } loc ? $"{index.NpcName(n)} ({zone} {loc.X:0.0}, {loc.Y:0.0})" : index.NpcName(n)).ToList()
        : [];

    public List<RecipeRef> CraftedBy(uint itemId)
    {
        var list = new List<RecipeRef>();
        if (!index.Recipes.ByResult.TryGetValue(itemId, out var recipeIds)) return list;
        foreach (var id in recipeIds)
        {
            if (index.Row<Sheets.Recipe>(id) is not { } recipe) continue;
            var level = recipe.RecipeLevelTable.ValueNullable;
            list.Add(new RecipeRef(id, itemId, index.ItemName(itemId), index.CraftTypeName(recipe.CraftType.RowId),
                level?.ClassJobLevel ?? 0, StarsOrNull(level?.Stars), null, YieldOrNull(recipe.AmountResult)));
        }

        return list;
    }

    internal static int? StarsOrNull(byte? stars) => stars is > 0 ? stars : null;

    internal static int? YieldOrNull(byte amount) => amount > 1 ? amount : null;

    /// <summary>Recipes that consume the item; quantity is the amount one synth uses.</summary>
    public (List<RecipeRef> Recipes, int Total) UsedIn(uint itemId, int offset, int limit)
    {
        var list = new List<RecipeRef>();
        if (!index.Recipes.ByIngredient.TryGetValue(itemId, out var uses)) return (list, 0);
        foreach (var (recipeId, amount) in uses.Skip(offset).Take(limit))
        {
            if (index.Row<Sheets.Recipe>(recipeId) is not { } recipe) continue;
            var level = recipe.RecipeLevelTable.ValueNullable;
            list.Add(new RecipeRef(recipeId, recipe.ItemResult.RowId, index.ItemName(recipe.ItemResult.RowId),
                index.CraftTypeName(recipe.CraftType.RowId), level?.ClassJobLevel ?? 0, StarsOrNull(level?.Stars), amount, YieldOrNull(recipe.AmountResult)));
        }

        return (list, uses.Length);
    }

    public List<GatheringSource> Gathering(uint itemId, int maxPoints)
    {
        var list = new List<GatheringSource>();
        if (!index.Gathering.ByItem.TryGetValue(itemId, out var gatheringItems)) return list;
        foreach (var gid in gatheringItems)
        {
            if (index.Row<Sheets.GatheringItem>(gid) is not { } gi) continue;
            var levelRow = gi.GatheringItemLevel.ValueNullable;
            var points = new List<GatheringPointRef>();
            var seen = new HashSet<(uint, uint)>();
            var totalPoints = 0;
            if (index.Gathering.BasesByGatheringItem.TryGetValue(gid, out var bases))
            {
                foreach (var baseId in bases)
                {
                    if (index.Row<Sheets.GatheringPointBase>(baseId) is not { } pointBase) continue;
                    var type = GameDataIndex.NullIfEmpty(SheetJson.Text(pointBase.GatheringType.ValueNullable?.Name ?? default));
                    if (!index.Gathering.PointsByBase.TryGetValue(baseId, out var pointIds)) continue;
                    foreach (var pid in pointIds)
                    {
                        if (index.Row<Sheets.GatheringPoint>(pid) is not { } point) continue;
                        if (point.TerritoryType.RowId == 0) continue;
                        if (!seen.Add((point.TerritoryType.RowId, point.PlaceName.RowId))) continue;
                        totalPoints++;
                        if (points.Count >= maxPoints) continue;
                        points.Add(new GatheringPointRef(
                            index.TerritoryName(point.TerritoryType.RowId),
                            GameDataIndex.NullIfEmpty(SheetJson.Text(point.PlaceName.ValueNullable?.Name ?? default)),
                            pointBase.GatheringLevel,
                            type,
                            point.TerritoryType.RowId,
                            index.GatheringTimedKind(pid)));
                    }
                }
            }

            list.Add(new GatheringSource(gid, levelRow?.GatheringItemLevel ?? 0, levelRow?.Stars ?? 0, gi.IsHidden, gi.PerceptionReq, totalPoints, points));
        }

        return list;
    }
}

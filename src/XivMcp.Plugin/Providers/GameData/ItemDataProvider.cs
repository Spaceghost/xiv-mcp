using System.Collections.Concurrent;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Static item data from the game's Excel sheets (no login needed, never touches game memory).</summary>
[McpProvider("gamedata")]
public sealed class ItemDataProvider
{
    private const int MaxVendors = 10;
    private const int MaxExchanges = 10;
    private const int MaxUsedIn = 25;
    private const int MaxGatheringPoints = 8;

    private static readonly string[] SlotColumns =
        ["MainHand", "OffHand", "Head", "Body", "Gloves", "Waist", "Legs", "Feet", "Ears", "Neck", "Wrists", "FingerL", "FingerR", "SoulCrystal"];

    private readonly GameDataIndex index;
    private readonly ItemSources sources;
    private readonly ConcurrentDictionary<uint, (string? Label, string[] Slots)> slotCache = new();

    public ItemDataProvider(IGameDataSource data)
    {
        index = GameDataIndex.For(data);
        sources = new ItemSources(index);
    }

    [McpTool("search_items",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Item"],
        Title = "Search game items",
        Description =
            "Searches every item in the game data (not the player's inventory; use find_owned_items for that) by name in the client language, " +
            "ranked exact match > prefix > word prefix > substring > all words present; a numeric query also matches the item id. " +
            "Optional filters narrow results: itemUiCategory (category name like \"Two-handed Axe\", \"Ingredient\", \"Materia\", or its id), " +
            "equipSlot (mainHand, offHand, head, body, hands, waist, legs, feet, ears, neck, wrists, ring, soulCrystal), classJob (abbreviation like WAR/BLM/CRP, " +
            "matching items that job can equip), item level range, and isMarketable/isCraftable/isGatherable/isUntradable. " +
            "Query may be omitted when filters are given (results are then ordered by item level, highest first). Returns {total, offset, returned, truncated, results:[{id, name, category, itemLevel, equipLevel, " +
            "equipSlot, jobs, rarity, isMarketable, isCraftable, isGatherable, isUntradable, icon}]}. Use get_item with an id for full details.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<ItemSummary> SearchItems(
        [McpParam("Name text to search for (any case). Optional when at least one filter is set.")] string? query = null,
        [McpParam("ItemUICategory name (exact or partial, e.g. \"Ring\", \"Seafood\") or numeric id.")] string? itemUiCategory = null,
        [McpParam("Equipment slot: mainHand, offHand, head, body, hands, waist, legs, feet, ears, neck, wrists, ring, soulCrystal.")] string? equipSlot = null,
        [McpParam("Class/job abbreviation (e.g. PLD, WHM, ALC, MIN) or name; keeps only items that class/job can equip.")] string? classJob = null,
        [McpParam("Minimum item level (inclusive).", Minimum = 0)] int? minItemLevel = null,
        [McpParam("Maximum item level (inclusive).", Minimum = 0)] int? maxItemLevel = null,
        [McpParam("true = only items sellable on the market board; false = only non-marketable.")] bool? isMarketable = null,
        [McpParam("true = only items some recipe produces.")] bool? isCraftable = null,
        [McpParam("true = only items obtainable from mining/logging/harvesting/quarrying nodes.")] bool? isGatherable = null,
        [McpParam("true = only untradable items; false = only tradable.")] bool? isUntradable = null,
        [McpParam("Maximum results (1-500).", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var filters = new List<Func<GameDataIndex.ItemEntry, bool>>();

        if (!string.IsNullOrWhiteSpace(itemUiCategory))
        {
            var categories = ResolveUiCategories(itemUiCategory);
            if (categories.Count == 0)
                throw new McpToolException($"No item category matches \"{itemUiCategory}\". Try a category shown in get_item output, e.g. \"Ring\" or \"Ingredient\".");
            filters.Add(e => categories.Contains(e.UiCategory));
        }

        if (!string.IsNullOrWhiteSpace(equipSlot))
        {
            var columns = SlotColumnsFor(equipSlot)
                          ?? throw new McpToolException($"Unknown equipSlot \"{equipSlot}\". Use mainHand, offHand, head, body, hands, waist, legs, feet, ears, neck, wrists, ring or soulCrystal.");
            filters.Add(e => e.EquipSlotCategory != 0 && SlotsOf(e.EquipSlotCategory).Slots.Any(columns.Contains));
        }

        if (!string.IsNullOrWhiteSpace(classJob))
        {
            var job = index.FindClassJob(classJob)
                      ?? throw new McpToolException($"Unknown class/job \"{classJob}\". Use an abbreviation such as PLD, DRG, SCH, CRP or FSH.");
            var cache = new Dictionary<uint, bool>();
            filters.Add(e =>
            {
                if (e.ClassJobCategory == 0) return false;
                if (!cache.TryGetValue(e.ClassJobCategory, out var ok))
                    cache[e.ClassJobCategory] = ok = index.CategoryIncludes(e.ClassJobCategory, job);
                return ok;
            });
        }

        if (minItemLevel is { } min) filters.Add(e => e.ItemLevel >= min);
        if (maxItemLevel is { } max) filters.Add(e => e.ItemLevel <= max);
        if (isMarketable is { } m) filters.Add(e => e.IsMarketable == m);
        if (isUntradable is { } u) filters.Add(e => e.Untradable == u);
        if (isCraftable is { } c) filters.Add(e => index.IsCraftable(e.Id) == c);
        if (isGatherable is { } g) filters.Add(e => index.IsGatherable(e.Id) == g);

        if (string.IsNullOrWhiteSpace(query) && filters.Count == 0)
            throw new McpToolException("Provide a query or at least one filter.");

        // With only filters, list highest item level first instead of by name length.
        var page = string.IsNullOrWhiteSpace(query)
            ? Page<GameDataIndex.ItemEntry>.From(index.Items.Where(e => filters.All(f => f(e))).OrderByDescending(e => e.ItemLevel).ThenByDescending(e => e.Id).ToList(), offset, limit)
            : TextSearch.Search(index.Items, e => e.Id, e => e.Lower, query, filters.Count == 0 ? null : e => filters.All(f => f(e)), offset, limit);

        var results = page.Items.Select(Summarize).ToList();
        return new PagedResult<ItemSummary>(page.Total, page.Offset, results.Count, page.Truncated, results);
    }

    [McpTool("get_item",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Item", "lumina:GilShopItem", "lumina:SpecialShop", "lumina:Recipe", "lumina:GatheringItem"],
        Title = "Get item details",
        Description =
            "Full game-data record for one item id: name, description, icon, UI and market categories, item level, equip level and jobs, equip slots, " +
            "rarity, stack size, flags (unique, untradable, marketable, HQ-able, collectable, glamour, dyeable, materia slots), vendor buy price (only when a gil shop sells it) and sell price, " +
            "desynthesis/aetherial reduction flags, weapon/armor base stats and bonus stats (with HQ values), " +
            "gil vendors (NPC, shop, zone and map X/Y; first 10), currency exchanges (shop, costs such as tomestones or scrips; first 10), " +
            "recipes that craft it, recipes that use it (first 25 with total), and gathering sources (level, stars, zones). " +
            "Use search_items to find the id; get_recipe for full crafting trees. Vendor locations use the NPC's first placement and may be missing for event NPCs.",
        GameThread = false,
        RequiresLogin = false)]
    public ItemDetail GetItem([McpParam("Item id (row id in the Item sheet). HQ ids (1,000,000+) are accepted.")] uint itemId)
    {
        if (itemId > 1_000_000 && itemId < 2_000_000) itemId -= 1_000_000;
        if (index.Row<Sheets.Item>(itemId) is not { } row || SheetJson.Text(row.Name).Length == 0)
            throw new McpToolException($"Item {itemId} not found. Use search_items to look up item ids.");

        var isEquipment = row.EquipSlotCategory.RowId != 0;
        var slots = isEquipment ? SlotsOf(row.EquipSlotCategory.RowId) : default;

        var (vendors, vendorTotal) = sources.Vendors(row, 0, MaxVendors);
        var (exchanges, exchangeTotal) = sources.Exchanges(itemId, 0, MaxExchanges);
        var craftedBy = sources.CraftedBy(itemId);
        var (usedIn, usedInTotal) = sources.UsedIn(itemId, 0, MaxUsedIn);
        var gathering = sources.Gathering(itemId, MaxGatheringPoints);

        return new ItemDetail(
            itemId,
            SheetJson.Text(row.Name),
            GameDataIndex.NullIfEmpty(SheetJson.Text(row.Description)),
            row.Icon,
            Named(row.ItemUICategory.RowId, row.ItemUICategory.ValueNullable?.Name),
            Named(row.ItemSearchCategory.RowId, row.ItemSearchCategory.ValueNullable?.Name),
            (int)row.LevelItem.RowId,
            row.LevelEquip,
            Named(row.ClassJobCategory.RowId, row.ClassJobCategory.ValueNullable?.Name),
            isEquipment ? slots.Slots : null,
            row.Rarity,
            row.StackSize,
            row.IsUnique,
            row.IsUntradable,
            row.ItemSearchCategory.RowId != 0,
            row.CanBeHq,
            row.IsCollectable,
            row.IsGlamorous,
            row.IsCrestWorthy,
            row.DyeCount,
            row.MateriaSlotCount,
            row.IsAdvancedMeldingPermitted,
            vendorTotal > 0 ? row.PriceMid : null,
            row.PriceLow,
            row.Desynth > 0,
            row.AetherialReduce > 0,
            row.ClassJobRepair.RowId != 0 ? index.ClassJob(row.ClassJobRepair.RowId)?.Name : null,
            row.ItemSeries.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.ItemSeries.ValueNullable?.Name ?? default)) : null,
            isEquipment && row.DamagePhys > 0 ? row.DamagePhys : null,
            isEquipment && row.DamageMag > 0 ? row.DamageMag : null,
            isEquipment && row.Delayms > 0 ? row.Delayms / 1000.0 : null,
            isEquipment && row.DefensePhys > 0 ? row.DefensePhys : null,
            isEquipment && row.DefenseMag > 0 ? row.DefenseMag : null,
            isEquipment && row.Block > 0 ? row.Block : null,
            isEquipment && row.BlockRate > 0 ? row.BlockRate : null,
            Stats(row),
            !row.CanBeHq && row.ItemSpecialBonus.RowId != 0
                ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.ItemSpecialBonus.ValueNullable?.Name ?? default))
                : null,
            vendors.Count > 0 ? vendors : null,
            vendorTotal,
            exchanges.Count > 0 ? exchanges : null,
            exchangeTotal,
            craftedBy.Count > 0 ? craftedBy : null,
            usedIn.Count > 0 ? usedIn : null,
            usedInTotal,
            gathering.Count > 0 ? gathering : null);
    }

    [McpResourceTemplate("ffxiv://item/{itemId}",
        Name = "Game item",
        Description = "Game-data record for an item id (same content as the get_item tool).",
        GameThread = false,
        RequiresLogin = false)]
    public ItemDetail ItemResource(uint itemId) => GetItem(itemId);

    // ------------------------------------------------------------------ helpers

    internal ItemSummary Summarize(GameDataIndex.ItemEntry e) => new(
        e.Id,
        e.Name,
        e.UiCategory != 0 && index.Row<Sheets.ItemUICategory>(e.UiCategory) is { } cat ? GameDataIndex.NullIfEmpty(SheetJson.Text(cat.Name)) : null,
        e.ItemLevel,
        e.EquipLevel,
        e.EquipSlotCategory != 0 ? SlotsOf(e.EquipSlotCategory).Label : null,
        index.ClassJobCategoryName(e.ClassJobCategory),
        e.Rarity,
        e.IsMarketable,
        index.IsCraftable(e.Id),
        index.IsGatherable(e.Id),
        e.Untradable,
        e.Icon);

    private static NamedRef? Named(uint id, Lumina.Text.ReadOnly.ReadOnlySeString? name)
    {
        if (id == 0) return null;
        var text = name is { } n ? SheetJson.Text(n) : "";
        return new NamedRef(id, text.Length > 0 ? text : $"#{id}");
    }

    private HashSet<uint> ResolveUiCategories(string text)
    {
        var t = text.Trim();
        var sheet = index.Sheet<Sheets.ItemUICategory>();
        if (uint.TryParse(t, out var id)) return sheet.HasRow(id) ? [id] : [];
        var exact = new HashSet<uint>();
        var partial = new HashSet<uint>();
        foreach (var row in sheet)
        {
            var name = SheetJson.Text(row.Name);
            if (name.Length == 0) continue;
            if (name.Equals(t, StringComparison.OrdinalIgnoreCase)) exact.Add(row.RowId);
            else if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) partial.Add(row.RowId);
        }

        return exact.Count > 0 ? exact : partial;
    }

    private static HashSet<string>? SlotColumnsFor(string slot) =>
        slot.Trim().Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant() switch
        {
            "mainhand" or "weapon" or "main" or "primary" or "tool" => ["MainHand"],
            "offhand" or "off" or "shield" or "secondary" => ["OffHand"],
            "head" or "hat" or "helm" => ["Head"],
            "body" or "chest" => ["Body"],
            "hands" or "gloves" or "hand" => ["Gloves"],
            "waist" or "belt" => ["Waist"],
            "legs" or "pants" => ["Legs"],
            "feet" or "shoes" or "boots" => ["Feet"],
            "ears" or "earrings" or "earring" => ["Ears"],
            "neck" or "necklace" => ["Neck"],
            "wrists" or "wrist" or "bracelet" or "bracelets" => ["Wrists"],
            "ring" or "rings" or "finger" or "fingerl" or "fingerr" => ["FingerL", "FingerR"],
            "soulcrystal" or "soul" or "crystal" => ["SoulCrystal"],
            _ => null,
        };

    /// <summary>EquipSlotCategory → (label like "Body (also occupies Head)", slot names it equips into).</summary>
    internal (string? Label, string[] Slots) SlotsOf(uint equipSlotCategory) => slotCache.GetOrAdd(equipSlotCategory, id =>
    {
        if (index.Row<Sheets.EquipSlotCategory>(id) is not { } row) return (null, []);
        var values = SlotColumns.Select(c => (Name: c, Value: (sbyte)typeof(Sheets.EquipSlotCategory).GetProperty(c)!.GetValue(row)!)).ToArray();
        var equips = values.Where(v => v.Value > 0).Select(v => v.Name).ToArray();
        var blocks = values.Where(v => v.Value < 0).Select(v => v.Name).ToArray();
        string? label = equips.Length switch
        {
            0 => null,
            2 when equips.Contains("FingerL") && equips.Contains("FingerR") => "Ring",
            _ => string.Join("/", equips),
        };
        if (label != null && blocks.Length > 0)
            label += blocks.Length == 1 && blocks[0] == "OffHand" && equips.Contains("MainHand") ? " (two-handed)" : $" (also blocks {string.Join("/", blocks)})";
        return (label, equips.Length == 2 && label == "Ring" ? ["FingerL", "FingerR"] : equips);
    });

    private List<ItemStat>? Stats(Sheets.Item row)
    {
        var stats = new List<(uint Id, string Name, int Value, int? Hq)>();
        for (var i = 0; i < row.BaseParam.Count; i++)
        {
            var param = row.BaseParam[i];
            var value = row.BaseParamValue[i];
            if (param.RowId == 0 || value == 0) continue;
            stats.Add((param.RowId, ParamName(param.RowId), value, null));
        }

        if (row.CanBeHq)
        {
            for (var i = 0; i < row.BaseParamSpecial.Count; i++)
            {
                var param = row.BaseParamSpecial[i];
                var bonus = row.BaseParamValueSpecial[i];
                if (param.RowId == 0 || bonus == 0) continue;
                var existing = stats.FindIndex(s => s.Id == param.RowId);
                if (existing >= 0)
                {
                    var s = stats[existing];
                    stats[existing] = s with { Hq = s.Value + bonus };
                }
                else
                {
                    var baseValue = param.RowId switch
                    {
                        12 => row.DamagePhys,
                        13 => row.DamageMag,
                        21 => row.DefensePhys,
                        24 => row.DefenseMag,
                        _ => 0,
                    };
                    stats.Add((param.RowId, ParamName(param.RowId), baseValue, baseValue + bonus));
                }
            }
        }

        return stats.Count == 0 ? null : stats.Select(s => new ItemStat(s.Name, s.Value, s.Hq)).ToList();
    }

    private string ParamName(uint id) =>
        index.Row<Sheets.BaseParam>(id) is { } p && SheetJson.Text(p.Name) is { Length: > 0 } n ? n : $"BaseParam #{id}";
}

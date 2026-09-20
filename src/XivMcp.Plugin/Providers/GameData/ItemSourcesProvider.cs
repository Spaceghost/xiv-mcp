using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Where an item comes from and what it is used for, from the game's Excel sheets only.</summary>
[McpProvider("gamedata")]
public sealed class ItemSourcesProvider
{
    private const int MaxGatheringPoints = 12;
    private const int MaxText = 240;

    private const string SourcesNotCovered =
        "Not in the game data and therefore never listed: monster and boss drops, treasure coffers and maps, duty loot, random (quick and " +
        "exploration) retainer ventures, fishing and spearfishing, desynthesis and aetherial reduction results, Free Company credit shops, " +
        "seasonal events and the online store. An empty answer with kinds = [other] means one of those, not that the item is unobtainable.";

    private const string UsesNotCovered =
        "Not covered: quest hand-ins (the quest scripts, not the sheets, hold them), custom deliveries and collectable appraisals, " +
        "Grand Company expert delivery (any gear piece qualifies by item level), housing, and glamour or materia uses.";

    private readonly GameDataIndex index;
    private readonly ItemSources sources;

    public ItemSourcesProvider(IGameDataSource data)
    {
        index = GameDataIndex.For(data);
        sources = new ItemSources(index);
    }

    [McpTool("get_item_sources",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Item", "lumina:Recipe", "lumina:GatheringItem", "lumina:GatheringPoint", "lumina:GilShopItem", "lumina:SpecialShop",
            "lumina:GCScripShopItem", "lumina:RetainerTask", "lumina:Quest", "lumina:Achievement", "lumina:ENpcBase", "lumina:Level"],
        Title = "Get every known source of an item",
        Description =
            "Where an item comes from, as far as the game data says. Returns kinds (the source types found, in the order a player would try them: " +
            "gilVendor, gathering, crafting, specialShop, gcSeals, retainerVenture, quest, achievement; [other] when the sheets know none), gilPrice " +
            "(vendor price each, when sold for gil), recipes that make it (recipeId, craftType, level, stars, yield), gathering (per GatheringItem: " +
            "level, stars, hidden, perception and node points with zone, place, node level, type Mining/Quarrying/Logging/Harvesting and timed = " +
            "unspoiled|ephemeral), vendors (NPC name and id, shop, price in gil, location with zone and map X/Y when the NPC has a placement), " +
            "exchanges (special shops: shop name, receiveCount, costs as currency or item ids with amounts, NPCs), gcSeals (Grand Company seal price " +
            "and required rank id), ventures (targeted retainer ventures: retainer level, jobs, venture cost, duration, quantities), quests that " +
            "reward it (count, optional = one of several choices) and achievements. The long lists (vendors, exchanges, ventures, quests, " +
            "achievements) each come as {total, truncated, results} and share limit/offset. " +
            "NOT covered, because the sheets do not hold it: monster drops, treasure, duty loot, random retainer ventures, fishing, desynthesis, " +
            "FC credit shops, events. Use get_item for the item's stats, get_gathering_info for node coordinates and time windows, " +
            "get_recipe_tree for what a craft needs, and get_market_prices for the market board.",
        GameThread = false,
        RequiresLogin = false)]
    public ItemSourcesResult GetItemSources(
        [McpParam("Item id (use search_items to find it). The HQ offset (1,000,000) is ignored.", Minimum = 1)] uint itemId,
        [McpParam("Maximum entries per paged list (1-100).", Minimum = 1, Maximum = 100)] int limit = 10,
        [McpParam("Entries to skip in each paged list.", Minimum = 0)] int offset = 0)
    {
        var row = Item(ref itemId);
        limit = TextSearch.ClampLimit(limit, 100);
        offset = TextSearch.ClampOffset(offset);

        var recipes = sources.CraftedBy(itemId);
        var gathering = sources.Gathering(itemId, MaxGatheringPoints);
        var sold = sources.SoldForGil(itemId);
        var (vendors, vendorTotal) = sources.Vendors(row, offset, limit);
        var (exchanges, exchangeTotal) = sources.Exchanges(itemId, offset, limit);
        var seals = index.GcSealShop.GetValueOrDefault(itemId);
        var ventures = Ventures(itemId);
        var quests = Quests(itemId);
        var achievements = Achievements(itemId);

        var facts = new SourceMerge.Facts(sold, gathering.Count > 0, recipes.Count, exchangeTotal, seals != null, ventures.Count, quests.Count, achievements.Count);
        return new ItemSourcesResult(
            itemId,
            index.ItemName(itemId),
            SourceMerge.Kinds(facts),
            sold ? row.PriceMid : null,
            recipes,
            gathering,
            SourceMerge.Section(vendors, vendorTotal, offset),
            SourceMerge.Section(exchanges, exchangeTotal, offset),
            seals != null ? new GcSealSource(seals.Cost, seals.RequiredRankId) : null,
            SourceMerge.Slice(ventures, offset, limit),
            SourceMerge.Slice(quests, offset, limit),
            SourceMerge.Slice(achievements, offset, limit),
            SourcesNotCovered);
    }

    [McpTool("get_item_uses",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Item", "lumina:Recipe", "lumina:SpecialShop", "lumina:CraftLeve", "lumina:Leve", "lumina:GCSupplyDuty"],
        Title = "Get what an item is used for",
        Description =
            "What an item is good for, so the player can decide whether to keep, sell or turn it in. Returns recipes that consume it (recipeId, the " +
            "crafted item, craftType, level, quantity = amount one synth uses), tradeIns (special shops that take it as payment: amount, what they " +
            "give, other costs, NPCs), leves (tradecraft levequests asking for it: leve name, level, jobs, count per turn-in, repeats), gcSupply " +
            "(Grand Company supply and provisioning missions that can ask for it: class level, crafter or gatherer, count) and the flags " +
            "desynthesizable, aetherialReducible, isCollectable, isCrystal, isMarketable, isUntradable, plus vendorSellPrice in gil. " +
            "recipes, tradeIns and leves each come as {total, truncated, results} and share limit/offset. " +
            "NOT covered: quest hand-ins, custom deliveries, expert delivery, housing and glamour uses. " +
            "Use get_item_sources for the opposite question and get_item for stats.",
        GameThread = false,
        RequiresLogin = false)]
    public ItemUsesResult GetItemUses(
        [McpParam("Item id (use search_items to find it). The HQ offset (1,000,000) is ignored.", Minimum = 1)] uint itemId,
        [McpParam("Maximum entries per paged list (1-200).", Minimum = 1, Maximum = 200)] int limit = 25,
        [McpParam("Entries to skip in each paged list.", Minimum = 0)] int offset = 0)
    {
        var row = Item(ref itemId);
        limit = TextSearch.ClampLimit(limit, 200);
        offset = TextSearch.ClampOffset(offset);

        var (recipes, recipeTotal) = sources.UsedIn(itemId, offset, limit);
        var (tradeIns, tradeInTotal) = sources.TradeIns(itemId, offset, limit);
        var entry = index.Item(itemId);
        return new ItemUsesResult(
            itemId,
            index.ItemName(itemId),
            SourceMerge.Section(recipes, recipeTotal, offset),
            SourceMerge.Section(tradeIns, tradeInTotal, offset),
            SourceMerge.Slice(Leves(itemId), offset, limit),
            GcSupply(itemId),
            row.Desynth > 0,
            row.AetherialReduce > 0,
            row.IsCollectable,
            entry?.IsCrystal ?? false,
            entry?.IsMarketable ?? false,
            row.IsUntradable,
            row.PriceLow,
            UsesNotCovered);
    }

    // ------------------------------------------------------------------ helpers

    private Sheets.Item Item(ref uint itemId)
    {
        if (itemId > 1_000_000 && itemId < 2_000_000) itemId -= 1_000_000;
        if (itemId == 0 || index.Row<Sheets.Item>(itemId) is not { } row || SheetJson.Text(row.Name).Length == 0)
            throw McpToolException.WithCode(McpErrorCodes.NotFound, $"Item {itemId} not found. Use search_items to look up item ids.");
        return row;
    }

    private List<VentureSource> Ventures(uint itemId)
    {
        var list = new List<VentureSource>();
        if (!index.VenturesByItem.TryGetValue(itemId, out var tasks)) return list;
        foreach (var taskId in tasks)
        {
            if (index.Row<Sheets.RetainerTask>(taskId) is not { } task || index.Row<Sheets.RetainerTaskNormal>(task.Task.RowId) is not { } normal) continue;
            list.Add(new VentureSource(
                taskId,
                task.RetainerLevel,
                index.ClassJobCategoryName(task.ClassJobCategory.RowId),
                task.VentureCost,
                task.MaxTimemin,
                normal.Quantity.Select(q => (int)q).Where(q => q > 0).ToList()));
        }

        return list;
    }

    private List<QuestRewardSource> Quests(uint itemId)
    {
        var list = new List<QuestRewardSource>();
        if (!index.QuestsByRewardItem.TryGetValue(itemId, out var questIds)) return list;
        foreach (var questId in questIds)
        {
            if (index.Row<Sheets.Quest>(questId) is not { } quest) continue;
            var count = 0;
            var optional = true;
            for (var i = 0; i < quest.Reward.Count; i++)
            {
                if (quest.Reward[i].RowId != itemId || !quest.Reward[i].Is<Sheets.Item>() || i >= quest.ItemCountReward.Count) continue;
                count = quest.ItemCountReward[i];
                optional = false;
                break;
            }

            if (optional)
            {
                for (var i = 0; i < quest.OptionalItemReward.Count; i++)
                {
                    if (quest.OptionalItemReward[i].RowId != itemId) continue;
                    count = Math.Max(1, i < quest.OptionalItemCountReward.Count ? quest.OptionalItemCountReward[i] : 1);
                    break;
                }
            }

            list.Add(new QuestRewardSource(questId, SheetJson.Text(quest.Name), GameDataIndex.QuestLevel(quest), count, optional));
        }

        return list.OrderBy(q => q.Level).ThenBy(q => q.QuestId).ToList();
    }

    private List<AchievementSource> Achievements(uint itemId)
    {
        var list = new List<AchievementSource>();
        if (!index.AchievementsByItem.TryGetValue(itemId, out var ids)) return list;
        foreach (var id in ids)
        {
            if (index.Row<Sheets.Achievement>(id) is not { } row) continue;
            list.Add(new AchievementSource(id, SheetJson.Text(row.Name), Cap(SheetJson.Text(row.Description))));
        }

        return list;
    }

    private List<LeveUse> Leves(uint itemId)
    {
        var list = new List<LeveUse>();
        if (!index.CraftLevesByItem.TryGetValue(itemId, out var ids)) return list;
        foreach (var id in ids)
        {
            if (index.Row<Sheets.CraftLeve>(id) is not { } craft || craft.Leve.ValueNullable is not { } leve) continue;
            var count = 0;
            for (var i = 0; i < craft.Item.Count && i < craft.ItemCount.Count; i++)
            {
                if (craft.Item[i].RowId == itemId) count += craft.ItemCount[i];
            }

            list.Add(new LeveUse(craft.Leve.RowId, SheetJson.Text(leve.Name), leve.ClassJobLevel,
                index.ClassJobCategoryName(leve.ClassJobCategory.RowId), count, craft.Repeats));
        }

        return list.OrderBy(l => l.Level).ThenBy(l => l.LeveId).ToList();
    }

    private List<GcSupplyUse> GcSupply(uint itemId) =>
        index.GcSupplyByItem.TryGetValue(itemId, out var entries)
            ? entries.OrderBy(e => e.Level).Take(20)
                .Select(e => new GcSupplyUse(e.Level, index.ClassJob(e.ClassJobId)?.Name ?? $"ClassJob #{e.ClassJobId}", e.Count)).ToList()
            : [];

    private static string? Cap(string text) =>
        text.Length == 0 ? null : text.Length <= MaxText ? text : text[..MaxText] + "…";
}

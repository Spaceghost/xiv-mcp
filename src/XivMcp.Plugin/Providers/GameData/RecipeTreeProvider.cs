using XivMcp.Core;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>The whole crafting tree of an item, with a shopping list that says where each raw material comes from.</summary>
[McpProvider("gamedata")]
public sealed class RecipeTreeProvider
{
    private const int MaxTreeNodes = 400;
    private const int MaxDepth = 10;

    private readonly GameDataIndex index;
    private readonly ItemSources sources;

    public RecipeTreeProvider(IGameDataSource data)
    {
        index = GameDataIndex.For(data);
        sources = new ItemSources(index);
    }

    [McpTool("get_recipe_tree",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Recipe", "lumina:RecipeLevelTable", "lumina:Item", "lumina:GilShopItem", "lumina:SpecialShop", "lumina:GatheringItem", "lumina:GCScripShopItem",
            "lumina:RetainerTask", "lumina:Quest", "lumina:Achievement"],
        Title = "Get full crafting tree and sourced shopping list",
        Description =
            "Everything needed to craft `quantity` of an item (itemId) or of one recipe (recipeId), expanded all the way down: every craftable " +
            "ingredient is expanded into its own sub-recipe until only raw materials remain (maxDepth levels at most, a cycle guard, and a cap of " +
            "400 tree nodes; depthLimited/nodeCapHit/hasCycle tell when a cap cut the tree). Returns the root recipe (craftType, level, stars, yield, " +
            "crafts = synths needed, surplus = extra items the last synth makes), tree (per node: itemId, name, quantity for that branch, and for " +
            "crafted nodes recipeId, craftType, level, stars, crafts, yield; notExpanded = depth|cycle|nodeCap when a craftable node was left as a leaf), " +
            "craftOrder (every sub-craft pooled across branches, deepest first, with crafts/needed/surplus), crafterRequirements (highest recipe " +
            "level per crafter class, root included), rawMaterials and crystals. Each raw material carries its total quantity and sources in " +
            "preference order (gilVendor, gathering, crafting, specialShop, gcSeals, retainerVenture, quest, achievement, or other when the game " +
            "data knows no source: monster drops, treasure, duty chests), plus gilPriceEach/gilTotal and one vendor with zone and map coordinates " +
            "when a gil shop sells it, a gathering hint (type, level, zone, timed), the first special-shop exchange with its currency cost, and " +
            "gcSealsEach. gilForVendorMaterials sums the vendor-buyable lines. Quantities respect recipe yields. " +
            "Use get_recipe instead for the craft stats of a single recipe (durability, difficulty, quality), get_item_sources for every source " +
            "of one material, and find_owned_items to subtract what the player already has (this tool never reads the inventory).",
        GameThread = false,
        RequiresLogin = false)]
    public RecipeTreeResult GetRecipeTree(
        [McpParam("Item id of the crafted result. Provide this or recipeId.")] uint? itemId = null,
        [McpParam("Recipe row id. Takes precedence over itemId; use it to pick a specific crafter when an item has several recipes.")] uint? recipeId = null,
        [McpParam("Number of finished items wanted.", Minimum = 1, Maximum = 9999)] int quantity = 1,
        [McpParam("How many levels of sub-recipes to expand (1-10). The default expands everything in practice.", Minimum = 1, Maximum = MaxDepth)] int maxDepth = 8,
        [McpParam("false = omit the nested tree and return only the pooled lists (much smaller).")] bool includeTree = true)
    {
        quantity = Math.Clamp(quantity, 1, 9999);
        maxDepth = Math.Clamp(maxDepth, 1, MaxDepth);

        RecipeTree.Recipe root;
        List<RecipeRef>? alternatives = null;
        if (recipeId is { } rid)
        {
            root = index.TreeRecipe(rid)
                   ?? throw McpToolException.WithCode(McpErrorCodes.NotFound, $"Recipe {rid} not found. Use search_recipes to look up recipe ids.");
        }
        else if (itemId is { } iid)
        {
            if (iid > 1_000_000 && iid < 2_000_000) iid -= 1_000_000;
            root = index.TreeRecipeForItem(iid)
                   ?? throw McpToolException.WithCode(McpErrorCodes.NotFound,
                       $"No recipe crafts item {iid} ({index.ItemName(iid)}). Use get_item_sources to see how it is obtained.");
            var all = sources.CraftedBy(iid);
            if (all.Count > 1) alternatives = all.Where(r => r.RecipeId != root.RecipeId).ToList();
        }
        else
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Provide itemId or recipeId.");
        }

        var rootCrafts = RecipeTree.CraftsFor(quantity, root.Yield);
        var tree = RecipeTree.Expand(root, rootCrafts, maxDepth, MaxTreeNodes, index.TreeRecipeForItem, IsCrystal);
        var totals = RecipeTree.Flatten(root, rootCrafts, maxDepth, index.TreeRecipeForItem, IsCrystal);

        var craftOrder = totals.Crafts
            .Select(c => new RecipeTreeCraft(c.ItemId, index.ItemName(c.ItemId), c.Recipe.RecipeId, index.CraftTypeName(c.Recipe.CraftType),
                c.Recipe.Level, c.Recipe.Stars > 0 ? c.Recipe.Stars : null, RecipeTree.Clamp(c.Crafts), RecipeTree.Clamp(c.Needed), RecipeTree.Clamp(c.Surplus)))
            .ToList();

        var requirements = RecipeTree.CrafterRequirements(totals.Crafts.Select(c => c.Recipe).Append(root))
            .Select(r => new CrafterRequirement(index.CraftTypeName(r.CraftType), r.Level, r.Stars > 0 ? r.Stars : null))
            .ToList();

        var crystals = new List<MaterialLine>();
        var raw = new List<RawMaterial>();
        long gil = 0;
        foreach (var (materialId, amount) in totals.Raw)
        {
            if (IsCrystal(materialId))
            {
                crystals.Add(new MaterialLine(materialId, index.ItemName(materialId), RecipeTree.Clamp(amount), true));
                continue;
            }

            var line = Annotate(materialId, amount);
            gil += line.GilTotal ?? 0;
            raw.Add(line);
        }

        raw.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        crystals.Sort(static (a, b) => a.ItemId.CompareTo(b.ItemId));

        var depthLimited = RecipeTree.AnyStopped(tree, RecipeTree.StoppedByDepth);
        var nodeCapHit = RecipeTree.AnyStopped(tree, RecipeTree.StoppedByNodeCap);
        var hasCycle = RecipeTree.AnyStopped(tree, RecipeTree.StoppedByCycle);
        var surplus = (rootCrafts * root.Yield) - quantity;
        return new RecipeTreeResult(
            root.RecipeId,
            root.ItemId,
            index.ItemName(root.ItemId),
            index.CraftTypeName(root.CraftType),
            root.Level,
            root.Stars > 0 ? root.Stars : null,
            root.Yield,
            quantity,
            RecipeTree.Clamp(rootCrafts),
            RecipeTree.Clamp(surplus),
            maxDepth,
            RecipeTree.CountNodes(tree),
            depthLimited,
            nodeCapHit,
            hasCycle,
            includeTree ? tree.Select(ToDto).ToList() : null,
            craftOrder,
            requirements,
            raw,
            crystals,
            gil,
            alternatives,
            "Tree quantities are per branch; craftOrder, rawMaterials and crystals pool shared intermediates, so they are the numbers to shop by. " +
            "The pooled lists are not cut by the node cap. A raw material whose sources include crafting was left unexpanded (depth or cycle). " +
            "Sources named other are not in the game data (drops, treasure, duty chests, random ventures).");
    }

    private bool IsCrystal(uint itemId) => index.Item(itemId)?.IsCrystal ?? false;

    private RecipeTreeNode ToDto(RecipeTree.Node node)
    {
        var name = index.ItemName(node.ItemId);
        var quantity = RecipeTree.Clamp(node.Quantity);
        if (node.Used is not { } used) return new RecipeTreeNode(node.ItemId, name, quantity, node.IsCrystal, NotExpanded: node.Stopped);
        return new RecipeTreeNode(node.ItemId, name, quantity, false, used.RecipeId, index.CraftTypeName(used.CraftType), used.Level,
            used.Stars > 0 ? used.Stars : null, RecipeTree.Clamp(node.Crafts), used.Yield > 1 ? used.Yield : null, null,
            node.Children.Select(ToDto).ToList());
    }

    private RawMaterial Annotate(uint itemId, long amount)
    {
        var name = index.ItemName(itemId);
        var quantity = RecipeTree.Clamp(amount);
        if (index.Row<Sheets.Item>(itemId) is not { } row) return new RawMaterial(itemId, name, quantity, [SourceMerge.Other]);

        var sold = sources.SoldForGil(itemId);
        var vendors = sold ? sources.Vendors(row, 0, 1).Vendors : [];
        var gathering = sources.Gathering(itemId, 1);
        var (exchanges, exchangeTotal) = sources.Exchanges(itemId, 0, 1);
        var recipe = index.TreeRecipeForItem(itemId);
        var seals = index.GcSealShop.GetValueOrDefault(itemId);
        var facts = new SourceMerge.Facts(
            sold,
            gathering.Count > 0,
            recipe != null ? 1 : 0,
            exchangeTotal,
            seals != null,
            index.VenturesByItem.TryGetValue(itemId, out var ventures) ? ventures.Length : 0,
            index.QuestsByRewardItem.TryGetValue(itemId, out var quests) ? quests.Length : 0,
            index.AchievementsByItem.TryGetValue(itemId, out var achievements) ? achievements.Length : 0);

        GatheringHint? hint = null;
        if (gathering.Count > 0)
        {
            var best = gathering.OrderBy(g => g.Hidden).ThenBy(g => g.ItemLevel).First();
            var point = best.Points.Count > 0 ? best.Points[0] : null;
            hint = new GatheringHint(point?.Type, point?.Level ?? best.ItemLevel, point?.Zone, point?.Place, point?.Timed, gathering.Sum(g => g.TotalPoints));
        }

        return new RawMaterial(
            itemId,
            name,
            quantity,
            SourceMerge.Kinds(facts),
            sold ? row.PriceMid : null,
            SourceMerge.GilTotal(sold, row.PriceMid, amount),
            vendors.Count > 0 ? vendors[0] : null,
            hint,
            exchanges.Count > 0 ? exchanges[0] : null,
            seals?.Cost,
            recipe?.RecipeId);
    }
}

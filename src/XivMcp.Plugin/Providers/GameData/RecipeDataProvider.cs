using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Crafting recipes: search, full ingredient trees and aggregated shopping lists (game data only).</summary>
[McpProvider("gamedata")]
public sealed class RecipeDataProvider
{
    private const int MaxTreeNodes = 250;

    private readonly GameDataIndex index;

    public RecipeDataProvider(IDataManager data) => index = GameDataIndex.For(data);

    [McpTool("get_recipe",
        Title = "Get recipe with ingredient tree",
        Description =
            "Crafting recipe for an item (itemId) or a specific recipe (recipeId): craft type (Carpentry, Smithing, ... Cooking), recipe level and stars, " +
            "durability, difficulty (progress), max quality, required craftsmanship/control, HQ/quick-synth flags, expert and specialist requirements, " +
            "the master recipe book needed (if any), and the ingredient tree expanded `depth` levels (intermediate ingredients that are themselves " +
            "craftable get their own sub-ingredients). Also returns an aggregated shopping list: rawMaterials (everything to buy/gather, crystals flagged) " +
            "and intermediateCrafts (sub-recipes with number of synths), computed for `quantity` finished items and respecting recipe yields. " +
            "Tree quantities are per branch; the shopping list pools shared intermediates. When an item has several recipes (different crafters) " +
            "the first is used and the others are listed in alternativeRecipes. Does not consider what the player already owns.",
        GameThread = false,
        RequiresLogin = false)]
    public RecipeDetail GetRecipe(
        [McpParam("Item id of the crafted result. Provide this or recipeId.")] uint? itemId = null,
        [McpParam("Recipe row id. Takes precedence over itemId.")] uint? recipeId = null,
        [McpParam("How many levels of intermediate crafts to expand: 0 = direct ingredients only, up to 5.", Minimum = 0, Maximum = 5)] int depth = 2,
        [McpParam("Number of finished items wanted; scales the shopping list.", Minimum = 1, Maximum = 9999)] int quantity = 1)
    {
        depth = Math.Clamp(depth, 0, 5);
        quantity = Math.Clamp(quantity, 1, 9999);

        Sheets.Recipe recipe;
        List<RecipeSummary>? alternatives = null;
        if (recipeId is { } rid)
        {
            recipe = index.Row<Sheets.Recipe>(rid) is { } r && r.ItemResult.RowId != 0
                ? r
                : throw new McpToolException($"Recipe {rid} not found.");
        }
        else if (itemId is { } iid)
        {
            if (iid > 1_000_000 && iid < 2_000_000) iid -= 1_000_000;
            if (!index.Recipes.ByResult.TryGetValue(iid, out var ids) || ids.Length == 0)
                throw new McpToolException($"No recipe crafts item {iid} ({index.ItemName(iid)}). Use search_recipes or get_item to check how it is obtained.");
            recipe = index.Row<Sheets.Recipe>(ids[0])!.Value;
            if (ids.Length > 1)
                alternatives = ids.Skip(1).Select(id => index.Row<Sheets.Recipe>(id)).Where(r => r.HasValue).Select(r => Summarize(r!.Value)).ToList();
        }
        else
        {
            throw new McpToolException("Provide itemId or recipeId.");
        }

        var level = recipe.RecipeLevelTable.ValueNullable;
        var resultId = recipe.ItemResult.RowId;
        var yield = Math.Max(1, (int)recipe.AmountResult);
        var rootCrafts = (quantity + yield - 1) / yield;

        var budget = new StrongBox<int>(MaxTreeNodes);
        var ingredients = Ingredients(recipe)
            .Select(i => BuildNode(i.ItemId, i.Amount * rootCrafts, 1, depth, [resultId], budget))
            .ToList();

        var (raw, crafts) = ShoppingList(recipe, rootCrafts, depth);

        var book = recipe.SecretRecipeBook.ValueNullable;
        return new RecipeDetail(
            recipe.RowId,
            resultId,
            index.ItemName(resultId),
            yield,
            index.CraftTypeName(recipe.CraftType.RowId),
            level?.ClassJobLevel ?? 0,
            recipe.RecipeLevelTable.RowId,
            level?.Stars ?? 0,
            level is { } l1 ? l1.Durability * recipe.DurabilityFactor / 100 : 0,
            level is { } l2 ? l2.Difficulty * recipe.DifficultyFactor / 100 : 0,
            level is { } l3 ? (int)(l3.Quality * recipe.QualityFactor / 100) : 0,
            recipe.RequiredCraftsmanship,
            recipe.RequiredControl,
            level?.SuggestedCraftsmanship ?? 0,
            recipe.CanHq,
            recipe.CanQuickSynth,
            recipe.IsExpert,
            recipe.IsSpecializationRequired,
            book is { } b && recipe.SecretRecipeBook.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(b.Name)) : null,
            book is { } b2 && recipe.SecretRecipeBook.RowId != 0 && b2.Item.RowId != 0 ? b2.Item.RowId : null,
            recipe.ItemRequired.RowId != 0 ? index.ItemName(recipe.ItemRequired.RowId) : null,
            recipe.StatusRequired.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(recipe.StatusRequired.ValueNullable?.Name ?? default)) : null,
            depth,
            ingredients,
            raw,
            crafts,
            alternatives);
    }

    [McpTool("search_recipes",
        Title = "Search recipes",
        Description =
            "Searches crafting recipes by the crafted item's name (ranked exact > prefix > word > substring; numeric query matches the recipe id), " +
            "optionally filtered by craftType (crafter name like \"Weaving\"/\"Weaver\", abbreviation like WVR, or craft type id 0-7) and recipe level range. " +
            "Query may be omitted when a filter is set (results are then ordered by level, highest first). Returns {total, offset, returned, truncated, results:[{recipeId, itemId, itemName, craftType, level, stars, " +
            "yield, isExpert, requiresSpecialist, masterBook}]}. Use get_recipe with recipeId for ingredients.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<RecipeSummary> SearchRecipes(
        [McpParam("Crafted item name text (any case).")] string? query = null,
        [McpParam("Crafter: craft type name (Smithing), class name (Blacksmith), abbreviation (BSM) or craft type id.")] string? craftType = null,
        [McpParam("Minimum recipe (class/job) level.", Minimum = 0)] int? minLevel = null,
        [McpParam("Maximum recipe (class/job) level.", Minimum = 0)] int? maxLevel = null,
        [McpParam("Maximum results (1-500).", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var filters = new List<Func<GameDataIndex.RecipeEntry, bool>>();
        if (!string.IsNullOrWhiteSpace(craftType))
        {
            var type = ResolveCraftType(craftType)
                       ?? throw new McpToolException($"Unknown craftType \"{craftType}\". Use CRP, BSM, ARM, GSM, LTW, WVR, ALC or CUL.");
            filters.Add(r => r.CraftType == type);
        }

        if (minLevel is { } min) filters.Add(r => r.Level >= min);
        if (maxLevel is { } max) filters.Add(r => r.Level <= max);
        if (string.IsNullOrWhiteSpace(query) && filters.Count == 0)
            throw new McpToolException("Provide a query or at least one filter.");

        // With only filters, list highest recipe level first instead of by name length.
        var page = string.IsNullOrWhiteSpace(query)
            ? Page<GameDataIndex.RecipeEntry>.From(index.Recipes.All.Where(r => filters.All(f => f(r))).OrderByDescending(r => r.Level).ThenByDescending(r => r.Stars).ThenBy(r => r.Id).ToList(), offset, limit)
            : TextSearch.Search(index.Recipes.All, r => r.Id, r => r.Lower, query, filters.Count == 0 ? null : r => filters.All(f => f(r)), offset, limit);
        var results = page.Items
            .Select(e => index.Row<Sheets.Recipe>(e.Id))
            .Where(r => r.HasValue)
            .Select(r => Summarize(r!.Value))
            .ToList();
        return new PagedResult<RecipeSummary>(page.Total, page.Offset, results.Count, page.Truncated, results);
    }

    // ------------------------------------------------------------------ helpers

    private RecipeSummary Summarize(Sheets.Recipe r)
    {
        var level = r.RecipeLevelTable.ValueNullable;
        return new RecipeSummary(
            r.RowId,
            r.ItemResult.RowId,
            index.ItemName(r.ItemResult.RowId),
            index.CraftTypeName(r.CraftType.RowId),
            level?.ClassJobLevel ?? 0,
            level?.Stars ?? 0,
            Math.Max(1, (int)r.AmountResult),
            r.IsExpert,
            r.IsSpecializationRequired,
            r.SecretRecipeBook.RowId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(r.SecretRecipeBook.ValueNullable?.Name ?? default)) : null);
    }

    private uint? ResolveCraftType(string text)
    {
        var t = text.Trim();
        if (uint.TryParse(t, out var id)) return index.Sheet<Sheets.CraftType>().HasRow(id) ? id : null;

        // Crafter classes are ClassJob 8 (CRP) .. 15 (CUL); craft types are 0..7 in the same order.
        if (index.FindClassJob(t) is { Id: >= 8 and <= 15 } job) return job.Id - 8;

        foreach (var row in index.Sheet<Sheets.CraftType>())
        {
            var name = SheetJson.Text(row.Name);
            if (name.Length > 0 && (name.Equals(t, StringComparison.OrdinalIgnoreCase) || name.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
                return row.RowId;
        }

        foreach (var cj in index.ClassJobs)
        {
            if (cj.Id is >= 8 and <= 15 && cj.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase)) return cj.Id - 8;
        }

        return null;
    }

    private static List<(uint ItemId, int Amount)> Ingredients(Sheets.Recipe recipe)
    {
        var list = new List<(uint, int)>(8);
        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            var item = recipe.Ingredient[i].RowId;
            var amount = recipe.AmountIngredient[i];
            if (item != 0 && amount > 0) list.Add((item, amount));
        }

        return list;
    }

    private Sheets.Recipe? RecipeFor(uint itemId) =>
        index.Recipes.ByResult.TryGetValue(itemId, out var ids) && ids.Length > 0 ? index.Row<Sheets.Recipe>(ids[0]) : null;

    private bool IsCrystal(uint itemId) => index.Item(itemId)?.IsCrystal ?? false;

    private IngredientNode BuildNode(uint itemId, int quantity, int level, int depth, HashSet<uint> path, StrongBox<int> budget)
    {
        budget.Value--;
        var name = index.ItemName(itemId);
        var crystal = IsCrystal(itemId);
        if (level > depth || crystal || budget.Value <= 0 || path.Contains(itemId) || RecipeFor(itemId) is not { } sub)
            return new IngredientNode(itemId, name, quantity, crystal);

        var yield = Math.Max(1, (int)sub.AmountResult);
        var crafts = (quantity + yield - 1) / yield;
        var childPath = new HashSet<uint>(path) { itemId };
        var children = new List<IngredientNode>();
        foreach (var (childId, amount) in Ingredients(sub))
        {
            children.Add(BuildNode(childId, amount * crafts, level + 1, depth, childPath, budget));
        }

        return new IngredientNode(itemId, name, quantity, false, sub.RowId, index.CraftTypeName(sub.CraftType.RowId), crafts,
            yield > 1 ? yield : null, children);
    }

    /// <summary>
    /// Pools demand across the whole tree: discovers intermediates expandable within <paramref name="depth"/> (BFS, so the
    /// shallowest occurrence decides), orders them topologically, then converts demand into synth counts respecting yields.
    /// </summary>
    private (List<MaterialLine> Raw, List<CraftStep> Crafts) ShoppingList(Sheets.Recipe root, int rootCrafts, int depth)
    {
        var expand = new Dictionary<uint, Sheets.Recipe>();
        var edges = new Dictionary<uint, List<(uint Child, int Amount)>>();
        var queue = new Queue<(uint Item, int Level)>();
        var seen = new HashSet<uint> { root.ItemResult.RowId };
        foreach (var (item, _) in Ingredients(root))
        {
            if (seen.Add(item)) queue.Enqueue((item, 1));
        }

        while (queue.Count > 0)
        {
            var (item, level) = queue.Dequeue();
            if (level > depth || IsCrystal(item) || RecipeFor(item) is not { } sub) continue;
            expand[item] = sub;
            var children = Ingredients(sub);
            edges[item] = children;
            foreach (var (child, _) in children)
            {
                if (seen.Add(child)) queue.Enqueue((child, level + 1));
            }
        }

        // Kahn's algorithm over expanded nodes; any node left over sits on a cycle and is treated as raw.
        var indegree = expand.Keys.ToDictionary(k => k, _ => 0);
        foreach (var (_, children) in edges)
        foreach (var (child, _) in children)
        {
            if (indegree.ContainsKey(child)) indegree[child]++;
        }

        var ready = new Queue<uint>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var order = new List<uint>();
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            order.Add(node);
            foreach (var (child, _) in edges[node])
            {
                if (indegree.ContainsKey(child) && --indegree[child] == 0) ready.Enqueue(child);
            }
        }

        var ordered = order.ToHashSet();
        var demand = new Dictionary<uint, long>();
        foreach (var (item, amount) in Ingredients(root)) demand[item] = demand.GetValueOrDefault(item) + (long)amount * rootCrafts;

        var crafts = new List<CraftStep>();
        foreach (var item in order)
        {
            var need = demand.GetValueOrDefault(item);
            if (need <= 0) continue;
            var sub = expand[item];
            var yield = Math.Max(1, (int)sub.AmountResult);
            var synths = (need + yield - 1) / yield;
            demand.Remove(item);
            crafts.Add(new CraftStep(item, index.ItemName(item), sub.RowId, index.CraftTypeName(sub.CraftType.RowId),
                sub.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0, (int)synths, (int)need));
            foreach (var (child, amount) in edges[item]) demand[child] = demand.GetValueOrDefault(child) + amount * synths;
        }

        var raw = demand
            .Where(kv => kv.Value > 0 && !ordered.Contains(kv.Key))
            .Select(kv => new MaterialLine(kv.Key, index.ItemName(kv.Key), (int)Math.Min(kv.Value, int.MaxValue), IsCrystal(kv.Key)))
            .OrderBy(m => m.IsCrystal)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        crafts.Reverse(); // deepest sub-crafts first: the order to actually craft them
        return (raw, crafts);
    }
}

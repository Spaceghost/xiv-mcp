namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Pure ingredient-tree maths shared by get_recipe and get_recipe_tree: expansion with a depth cap, a cycle guard and a
/// node budget, and the pooled shopping list that respects recipe yields. Works on plain records and delegates so the
/// tests can drive it without game data.
/// </summary>
internal static class RecipeTree
{
    /// <summary>Why a craftable ingredient was left as a leaf.</summary>
    public const string StoppedByDepth = "depth";

    public const string StoppedByCycle = "cycle";

    public const string StoppedByNodeCap = "nodeCap";

    internal readonly record struct Input(uint ItemId, int Amount);

    internal sealed record Recipe(
        uint RecipeId,
        uint ItemId,
        int Yield,
        uint CraftType,
        int Level,
        int Stars,
        IReadOnlyList<Input> Ingredients);

    /// <summary>One tree node. <see cref="Used"/> is set only when the node was expanded into <see cref="Children"/>.</summary>
    internal sealed record Node(
        uint ItemId,
        long Quantity,
        bool IsCrystal,
        Recipe? Used,
        long Crafts,
        IReadOnlyList<Node> Children,
        string? Stopped);

    internal sealed record CraftLine(uint ItemId, Recipe Recipe, long Crafts, long Needed)
    {
        /// <summary>Items produced beyond what is needed, because synths make whole yields.</summary>
        public long Surplus => (Crafts * Math.Max(1, Recipe.Yield)) - Needed;
    }

    internal sealed record Totals(IReadOnlyList<(uint ItemId, long Quantity)> Raw, IReadOnlyList<CraftLine> Crafts);

    /// <summary>Synths needed to end up with at least <paramref name="quantity"/> items from a recipe making <paramref name="yield"/> per synth.</summary>
    public static long CraftsFor(long quantity, int yield)
    {
        var y = Math.Max(1, yield);
        return quantity <= 0 ? 0 : (quantity + y - 1) / y;
    }

    /// <summary>
    /// Expands the direct ingredients of <paramref name="root"/> for <paramref name="rootCrafts"/> synths. Ingredients at
    /// level 1..<paramref name="maxDepth"/> that have a recipe are expanded; crystals never are. Quantities are per branch.
    /// </summary>
    public static IReadOnlyList<Node> Expand(
        Recipe root,
        long rootCrafts,
        int maxDepth,
        int maxNodes,
        Func<uint, Recipe?> recipeFor,
        Func<uint, bool> isCrystal)
    {
        var budget = new Budget { Left = maxNodes };
        var path = new HashSet<uint> { root.ItemId };
        var nodes = new List<Node>(root.Ingredients.Count);
        foreach (var input in root.Ingredients)
        {
            nodes.Add(ExpandNode(input.ItemId, input.Amount * rootCrafts, 1, maxDepth, path, budget, recipeFor, isCrystal));
        }

        return nodes;
    }

    private static Node ExpandNode(
        uint itemId,
        long quantity,
        int level,
        int maxDepth,
        HashSet<uint> path,
        Budget budget,
        Func<uint, Recipe?> recipeFor,
        Func<uint, bool> isCrystal)
    {
        budget.Left--;
        if (isCrystal(itemId)) return new Node(itemId, quantity, true, null, 0, [], null);
        if (recipeFor(itemId) is not { } sub) return new Node(itemId, quantity, false, null, 0, [], null);

        var stopped = level > maxDepth ? StoppedByDepth
            : budget.Left <= 0 ? StoppedByNodeCap
            : path.Contains(itemId) ? StoppedByCycle
            : null;
        if (stopped != null) return new Node(itemId, quantity, false, null, 0, [], stopped);

        var crafts = CraftsFor(quantity, sub.Yield);
        path.Add(itemId);
        var children = new List<Node>(sub.Ingredients.Count);
        foreach (var input in sub.Ingredients)
        {
            children.Add(ExpandNode(input.ItemId, input.Amount * crafts, level + 1, maxDepth, path, budget, recipeFor, isCrystal));
        }

        path.Remove(itemId);
        return new Node(itemId, quantity, false, sub, crafts, children, null);
    }

    /// <summary>
    /// Pools demand across the whole tree: discovers intermediates expandable within <paramref name="maxDepth"/> (breadth
    /// first, so the shallowest occurrence decides), orders them topologically, then converts demand into synth counts
    /// respecting yields. Items on a recipe cycle are treated as raw. Crafts come back deepest first (the order to make them).
    /// </summary>
    public static Totals Flatten(
        Recipe root,
        long rootCrafts,
        int maxDepth,
        Func<uint, Recipe?> recipeFor,
        Func<uint, bool> isCrystal)
    {
        var expand = new Dictionary<uint, Recipe>();
        var queue = new Queue<(uint Item, int Level)>();
        var seen = new HashSet<uint> { root.ItemId };
        foreach (var input in root.Ingredients)
        {
            if (seen.Add(input.ItemId)) queue.Enqueue((input.ItemId, 1));
        }

        while (queue.Count > 0)
        {
            var (item, level) = queue.Dequeue();
            if (level > maxDepth || isCrystal(item) || recipeFor(item) is not { } sub) continue;
            expand[item] = sub;
            foreach (var child in sub.Ingredients)
            {
                if (seen.Add(child.ItemId)) queue.Enqueue((child.ItemId, level + 1));
            }
        }

        // Kahn's algorithm over expanded nodes; any node left over sits on a cycle and is treated as raw.
        var indegree = expand.Keys.ToDictionary(k => k, _ => 0);
        foreach (var sub in expand.Values)
        {
            foreach (var child in sub.Ingredients)
            {
                if (indegree.TryGetValue(child.ItemId, out var n)) indegree[child.ItemId] = n + 1;
            }
        }

        var ready = new Queue<uint>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var order = new List<uint>();
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            order.Add(node);
            foreach (var child in expand[node].Ingredients)
            {
                if (indegree.ContainsKey(child.ItemId) && --indegree[child.ItemId] == 0) ready.Enqueue(child.ItemId);
            }
        }

        var demand = new Dictionary<uint, long>();
        var firstSeen = new List<uint>();
        void Need(uint item, long amount)
        {
            if (!demand.TryGetValue(item, out var have)) firstSeen.Add(item);
            demand[item] = have + amount;
        }

        foreach (var input in root.Ingredients) Need(input.ItemId, input.Amount * rootCrafts);

        var crafted = new HashSet<uint>();
        var crafts = new List<CraftLine>();
        foreach (var item in order)
        {
            var need = demand.GetValueOrDefault(item);
            if (need <= 0) continue;
            var sub = expand[item];
            var synths = CraftsFor(need, sub.Yield);
            crafted.Add(item);
            crafts.Add(new CraftLine(item, sub, synths, need));
            foreach (var child in sub.Ingredients) Need(child.ItemId, child.Amount * synths);
        }

        var raw = firstSeen
            .Where(item => !crafted.Contains(item) && demand[item] > 0)
            .Select(item => (item, demand[item]))
            .ToList();
        crafts.Reverse();
        return new Totals(raw, crafts);
    }

    /// <summary>Highest recipe level (then stars) needed per craft type across the given recipes, ordered by craft type.</summary>
    public static IReadOnlyList<(uint CraftType, int Level, int Stars)> CrafterRequirements(IEnumerable<Recipe> recipes) =>
        recipes
            .GroupBy(r => r.CraftType)
            .Select(g => g.OrderByDescending(r => r.Level).ThenByDescending(r => r.Stars).First())
            .OrderBy(r => r.CraftType)
            .Select(r => (r.CraftType, r.Level, r.Stars))
            .ToList();

    /// <summary>Counts every node in a tree, for reporting and the node-cap tests.</summary>
    public static int CountNodes(IEnumerable<Node> nodes) => nodes.Sum(n => 1 + CountNodes(n.Children));

    /// <summary>True when any craftable node was left unexpanded for <paramref name="reason"/>.</summary>
    public static bool AnyStopped(IEnumerable<Node> nodes, string reason) =>
        nodes.Any(n => string.Equals(n.Stopped, reason, StringComparison.Ordinal) || AnyStopped(n.Children, reason));

    public static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private sealed class Budget
    {
        public int Left { get; set; }
    }
}

using XivMcp.Plugin.Providers.GameData;

namespace XivMcp.Plugin.Tests;

/// <summary>The pure ingredient-tree maths behind get_recipe and get_recipe_tree, driven with hand-made recipes.</summary>
public class RecipeTreeTests
{
    private const uint Shard = 2;
    private const uint Ore = 10;
    private const uint Log = 11;
    private const uint Ingot = 20;
    private const uint Lumber = 21;
    private const uint Rivets = 22;
    private const uint Sword = 30;

    private static readonly Dictionary<uint, RecipeTree.Recipe> Book = new[]
    {
        Recipe(100, Ingot, yield: 1, craftType: 1, level: 10, (Ore, 3), (Shard, 1)),
        Recipe(101, Lumber, yield: 1, craftType: 0, level: 12, (Log, 2), (Shard, 1)),
        Recipe(102, Rivets, yield: 3, craftType: 1, level: 15, (Ingot, 1), (Shard, 1)),
        Recipe(103, Sword, yield: 1, craftType: 1, level: 20, (Ingot, 2), (Lumber, 1), (Rivets, 2), (Shard, 3)),
    }.ToDictionary(r => r.ItemId);

    private static RecipeTree.Recipe Recipe(uint id, uint item, int yield, uint craftType, int level, params (uint Item, int Amount)[] inputs) =>
        new(id, item, yield, craftType, level, 0, inputs.Select(i => new RecipeTree.Input(i.Item, i.Amount)).ToList());

    private static RecipeTree.Recipe? Lookup(uint item) => Book.GetValueOrDefault(item);

    private static bool IsCrystal(uint item) => item == Shard;

    private static uint MaxItem(IEnumerable<RecipeTree.Node> nodes) =>
        nodes.Select(n => Math.Max(n.ItemId, n.Children.Count > 0 ? MaxItem(n.Children) : 0)).DefaultIfEmpty(0u).Max();

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 3, 2)]
    [InlineData(7, 3, 3)]
    [InlineData(5, 0, 5)]
    [InlineData(0, 3, 0)]
    public void CraftsRoundUpByYield(long quantity, int yield, long expected) =>
        Assert.Equal(expected, RecipeTree.CraftsFor(quantity, yield));

    [Fact]
    public void FlattenPoolsSharedIntermediatesAndRespectsYield()
    {
        var root = Book[Sword];
        var totals = RecipeTree.Flatten(root, rootCrafts: 2, maxDepth: 8, Lookup, IsCrystal);

        // 2 swords: 4 rivets -> 2 synths (yield 3, surplus 2) -> 2 ingots; plus 4 ingots direct = 6 ingots -> 18 ore.
        var crafts = totals.Crafts.ToDictionary(c => c.ItemId);
        Assert.Equal(2, crafts[Rivets].Crafts);
        Assert.Equal(4, crafts[Rivets].Needed);
        Assert.Equal(2, crafts[Rivets].Surplus);
        Assert.Equal(6, crafts[Ingot].Crafts);
        Assert.Equal(2, crafts[Lumber].Crafts);

        var raw = totals.Raw.ToDictionary(r => r.ItemId, r => r.Quantity);
        Assert.Equal(18, raw[Ore]);
        Assert.Equal(4, raw[Log]);
        // Shards: 3*2 root + 2 rivet synths + 6 ingot synths + 2 lumber synths.
        Assert.Equal(16, raw[Shard]);
        Assert.DoesNotContain(Ingot, raw.Keys);

        // Deepest first: ingots must be made before the rivets that consume them.
        var order = totals.Crafts.Select(c => c.ItemId).ToList();
        Assert.True(order.IndexOf(Ingot) < order.IndexOf(Rivets));
    }

    [Fact]
    public void ExpandGivesPerBranchQuantitiesAndNeverExpandsCrystals()
    {
        var tree = RecipeTree.Expand(Book[Sword], 1, 8, 100, Lookup, IsCrystal);
        Assert.Equal(4, tree.Count);
        var rivets = tree.Single(n => n.ItemId == Rivets);
        Assert.Equal(2, rivets.Quantity);
        Assert.Equal(1, rivets.Crafts);
        Assert.Equal(102u, rivets.Used!.RecipeId);
        var ingotUnderRivets = rivets.Children.Single(n => n.ItemId == Ingot);
        Assert.Equal(1, ingotUnderRivets.Quantity);
        Assert.Equal(3, ingotUnderRivets.Children.Single(n => n.ItemId == Ore).Quantity);

        var shard = tree.Single(n => n.ItemId == Shard);
        Assert.True(shard.IsCrystal);
        Assert.Null(shard.Used);
        Assert.Null(shard.Stopped);
    }

    [Fact]
    public void DepthCapLeavesCraftableLeavesMarkedAndTreatsThemAsRaw()
    {
        var tree = RecipeTree.Expand(Book[Sword], 1, maxDepth: 1, 100, Lookup, IsCrystal);
        var rivets = tree.Single(n => n.ItemId == Rivets);
        Assert.NotNull(rivets.Used);
        var ingot = rivets.Children.Single(n => n.ItemId == Ingot);
        Assert.Null(ingot.Used);
        Assert.Equal(RecipeTree.StoppedByDepth, ingot.Stopped);
        Assert.True(RecipeTree.AnyStopped(tree, RecipeTree.StoppedByDepth));
        Assert.False(RecipeTree.AnyStopped(tree, RecipeTree.StoppedByCycle));

        // Ingot is a direct ingredient (level 1), so the pooled list still crafts it; with depth 0 nothing is expanded.
        var none = RecipeTree.Flatten(Book[Sword], 1, maxDepth: 0, Lookup, IsCrystal);
        Assert.Empty(none.Crafts);
        Assert.Equal(2, none.Raw.Single(r => r.ItemId == Ingot).Quantity);
    }

    [Fact]
    public void CyclesTerminateAndAreReported()
    {
        // A needs B, B needs A; C needs A.
        var book = new[]
        {
            Recipe(1, 50, 1, 0, 1, (51, 1)),
            Recipe(2, 51, 1, 0, 1, (50, 1), (Ore, 1)),
            Recipe(3, 52, 1, 0, 1, (50, 2)),
        }.ToDictionary(r => r.ItemId);
        RecipeTree.Recipe? Find(uint item) => book.GetValueOrDefault(item);

        var tree = RecipeTree.Expand(book[52], 1, 10, 100, Find, _ => false);
        Assert.True(RecipeTree.AnyStopped(tree, RecipeTree.StoppedByCycle));
        Assert.True(RecipeTree.CountNodes(tree) < 10);

        var totals = RecipeTree.Flatten(book[52], 1, 10, Find, _ => false);
        // Both sit on the cycle, so neither can be ordered: the demand stays raw instead of looping.
        Assert.Empty(totals.Crafts);
        Assert.Equal(2, totals.Raw.Single(r => r.ItemId == 50).Quantity);
    }

    [Fact]
    public void RootAppearingAsItsOwnIngredientIsNotExpanded()
    {
        var book = new[] { Recipe(1, 60, 1, 0, 1, (61, 1)), Recipe(2, 61, 1, 0, 1, (60, 1)) }.ToDictionary(r => r.ItemId);
        var tree = RecipeTree.Expand(book[60], 1, 10, 100, i => book.GetValueOrDefault(i), _ => false);
        var inner = tree.Single().Children.Single();
        Assert.Equal(60u, inner.ItemId);
        Assert.Equal(RecipeTree.StoppedByCycle, inner.Stopped);
    }

    [Fact]
    public void NodeCapStopsExpansionButNotThePooledList()
    {
        // A chain 200 -> 201 -> ... each needing two of the next: the tree is cut, the totals are not.
        var book = Enumerable.Range(0, 12).Select(i => Recipe((uint)(i + 1), (uint)(200 + i), 1, 0, i + 1, ((uint)(201 + i), 2), (Ore, 1)))
            .ToDictionary(r => r.ItemId);
        RecipeTree.Recipe? Find(uint item) => book.GetValueOrDefault(item);

        var tree = RecipeTree.Expand(book[200], 1, 20, maxNodes: 9, Find, _ => false);
        Assert.True(RecipeTree.AnyStopped(tree, RecipeTree.StoppedByNodeCap));
        Assert.True(MaxItem(tree) <= 209, "expansion went past the node budget");

        var totals = RecipeTree.Flatten(book[200], 1, 20, Find, _ => false);
        Assert.Equal(11, totals.Crafts.Count);
        Assert.Equal(4096, totals.Raw.Single(r => r.ItemId == 212).Quantity);
    }

    [Fact]
    public void LargeQuantitiesDoNotOverflow()
    {
        var book = new[] { Recipe(1, 70, 1, 0, 1, (71, 99)), Recipe(2, 71, 1, 0, 1, (72, 99)), Recipe(3, 72, 1, 0, 1, (Ore, 99)) }.ToDictionary(r => r.ItemId);
        var totals = RecipeTree.Flatten(book[70], 9999, 10, i => book.GetValueOrDefault(i), _ => false);
        var ore = totals.Raw.Single().Quantity;
        Assert.Equal(9999L * 99 * 99 * 99, ore);
        Assert.Equal(int.MaxValue, RecipeTree.Clamp(ore));
    }

    [Fact]
    public void CrafterRequirementsKeepTheHighestLevelPerCrafter()
    {
        var needs = RecipeTree.CrafterRequirements(Book.Values);
        Assert.Equal(new (uint, int, int)[] { (0u, 12, 0), (1u, 20, 0) }, needs.ToArray());
    }
}

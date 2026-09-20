namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Pure merging of what the sheets say about where an item comes from into an ordered list of source kinds. The order
/// is the order a player would normally try them in: buy for gil, gather, craft, exchange, then one-off rewards.
/// </summary>
internal static class SourceMerge
{
    public const string GilVendor = "gilVendor";
    public const string Gathering = "gathering";
    public const string Crafting = "crafting";
    public const string SpecialShop = "specialShop";
    public const string GcSeals = "gcSeals";
    public const string RetainerVenture = "retainerVenture";
    public const string Quest = "quest";
    public const string Achievement = "achievement";

    /// <summary>Nothing in the sheets: monster drops, treasure, duty chests, random ventures, events, the Mog Station.</summary>
    public const string Other = "other";

    /// <summary>What the lookups found for one item. Counts are totals, not page sizes.</summary>
    internal readonly record struct Facts(
        bool SoldForGil = false,
        bool Gatherable = false,
        int Recipes = 0,
        int Exchanges = 0,
        bool GcSeals = false,
        int Ventures = 0,
        int Quests = 0,
        int Achievements = 0);

    /// <summary>Every kind the facts support, in preference order; ["other"] when the sheets know no source.</summary>
    public static IReadOnlyList<string> Kinds(Facts facts)
    {
        var kinds = new List<string>(4);
        if (facts.SoldForGil) kinds.Add(GilVendor);
        if (facts.Gatherable) kinds.Add(Gathering);
        if (facts.Recipes > 0) kinds.Add(Crafting);
        if (facts.Exchanges > 0) kinds.Add(SpecialShop);
        if (facts.GcSeals) kinds.Add(GcSeals);
        if (facts.Ventures > 0) kinds.Add(RetainerVenture);
        if (facts.Quests > 0) kinds.Add(Quest);
        if (facts.Achievements > 0) kinds.Add(Achievement);
        if (kinds.Count == 0) kinds.Add(Other);
        return kinds;
    }

    /// <summary>Gil needed to buy <paramref name="quantity"/> from a vendor, or null when no vendor sells it.</summary>
    public static long? GilTotal(bool soldForGil, uint priceEach, long quantity) =>
        soldForGil && priceEach > 0 && quantity > 0 ? priceEach * quantity : null;

    /// <summary>One page of a list with its total, for sections that are paged side by side with one limit/offset.</summary>
    public static SourceSection<T> Section<T>(IReadOnlyList<T> page, int total, int offset) =>
        new(total, Math.Max(0, offset) + page.Count < total, page);

    /// <summary>Slices an in-memory list into a section.</summary>
    public static SourceSection<T> Slice<T>(IReadOnlyList<T> all, int offset, int limit)
    {
        var page = Page<T>.From(all, offset, limit);
        return new SourceSection<T>(page.Total, page.Truncated, page.Items);
    }
}

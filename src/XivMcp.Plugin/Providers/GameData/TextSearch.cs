namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Case-insensitive relevance ranking shared by every name search.</summary>
internal static class TextSearch
{
    public const int NoMatch = -1;

    public static string Normalize(string? text) => (text ?? "").Trim().ToLowerInvariant();

    public static string[] Tokens(string normalizedQuery) =>
        normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 0 exact, 1 prefix, 2 word prefix, 3 substring, 4 all words present (any order); 5 when the query is empty;
    /// <see cref="NoMatch"/> otherwise.
    /// </summary>
    public static int Rank(string lowerName, string query, string[] tokens)
    {
        if (query.Length == 0) return 5;
        if (lowerName.Length == 0) return NoMatch;
        if (lowerName.Equals(query, StringComparison.Ordinal)) return 0;
        if (lowerName.StartsWith(query, StringComparison.Ordinal)) return 1;
        var index = lowerName.IndexOf(query, StringComparison.Ordinal);
        while (index > 0)
        {
            if (!char.IsLetterOrDigit(lowerName[index - 1])) return 2;
            index = lowerName.IndexOf(query, index + 1, StringComparison.Ordinal);
        }

        if (lowerName.Contains(query, StringComparison.Ordinal)) return 3;
        if (tokens.Length > 1)
        {
            foreach (var token in tokens)
            {
                if (!lowerName.Contains(token, StringComparison.Ordinal)) return NoMatch;
            }

            return 4;
        }

        return NoMatch;
    }

    /// <summary>
    /// Ranks <paramref name="source"/> by name relevance (then shorter name, then id) and applies paging.
    /// A purely numeric query also matches the row id exactly (rank 0).
    /// </summary>
    public static Page<T> Search<T>(
        IEnumerable<T> source,
        Func<T, uint> id,
        Func<T, string> lowerName,
        string? query,
        Func<T, bool>? filter,
        int offset,
        int limit)
    {
        var q = Normalize(query);
        var tokens = Tokens(q);
        uint? numeric = uint.TryParse(q, out var n) ? n : null;
        var hits = new List<(T Item, int Rank, int Length, uint Id)>();
        foreach (var item in source)
        {
            if (filter != null && !filter(item)) continue;
            var itemId = id(item);
            var name = lowerName(item);
            var rank = numeric.HasValue && itemId == numeric.Value ? 0 : Rank(name, q, tokens);
            if (rank == NoMatch) continue;
            hits.Add((item, rank, name.Length, itemId));
        }

        hits.Sort(static (a, b) =>
        {
            var c = a.Rank.CompareTo(b.Rank);
            if (c != 0) return c;
            c = a.Length.CompareTo(b.Length);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        return Page<T>.From(hits.Select(h => h.Item).ToList(), offset, limit);
    }

    public static int ClampLimit(int limit, int max = 500) => Math.Clamp(limit, 1, max);

    public static int ClampOffset(int offset) => Math.Max(0, offset);
}

internal sealed class Page<T>
{
    public required List<T> Items { get; init; }

    public required int Total { get; init; }

    public required int Offset { get; init; }

    public bool Truncated => Offset + Items.Count < Total;

    public static Page<T> From(IReadOnlyList<T> all, int offset, int limit)
    {
        offset = TextSearch.ClampOffset(offset);
        limit = TextSearch.ClampLimit(limit);
        var items = all.Skip(offset).Take(limit).ToList();
        return new Page<T> { Items = items, Total = all.Count, Offset = offset };
    }
}

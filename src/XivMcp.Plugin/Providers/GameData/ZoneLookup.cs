namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Pure helpers for the zone tools: a coarse zone kind and name → territory resolution over the zone index.</summary>
internal static class ZoneLookup
{
    public const string City = "city";
    public const string Field = "field";
    public const string Inn = "inn";
    public const string Housing = "housing";
    public const string Duty = "duty";
    public const string Pvp = "pvp";
    public const string Other = "other";

    public static readonly string[] Kinds = [City, Field, Inn, Housing, Duty, Pvp, Other];

    /// <summary>
    /// Coarse kind from TerritoryIntendedUse (0 town, 1 open world, 2 inn room, 13 residential district, 14 house interior;
    /// 3 dungeon, 8 alliance raid, 10 trial, 16/17 raid) and the zone's Duty Finder entry. Everything else is "other".
    /// </summary>
    public static string KindOf(uint intendedUse, uint dutyId, bool isPvp)
    {
        if (isPvp) return Pvp;
        return intendedUse switch
        {
            0 => City,
            1 => Field,
            2 => Inn,
            13 or 14 => Housing,
            3 or 8 or 10 or 16 or 17 => Duty,
            _ => dutyId != 0 ? Duty : Other,
        };
    }

    internal sealed record Resolution(GameDataIndex.ZoneEntry? Match, IReadOnlyList<GameDataIndex.ZoneEntry> Candidates);

    /// <summary>
    /// Resolves a territory id or a zone name. Several territories share one name (instanced and quest copies), so among
    /// equal names the one <paramref name="prefer"/> accepts wins, then towns and open-world zones, then the lowest id.
    /// When the best matches have different names the result is ambiguous: Match is null and Candidates lists them.
    /// </summary>
    public static Resolution Resolve(IReadOnlyList<GameDataIndex.ZoneEntry> zones, string? text, Func<GameDataIndex.ZoneEntry, bool>? prefer = null)
    {
        var query = TextSearch.Normalize(text);
        if (query.Length == 0) return new Resolution(null, []);
        if (uint.TryParse(query, out var id))
        {
            var byId = zones.FirstOrDefault(z => z.Id == id);
            return new Resolution(byId, byId != null ? [byId] : []);
        }

        var tokens = TextSearch.Tokens(query);
        var best = int.MaxValue;
        var matches = new List<GameDataIndex.ZoneEntry>();
        foreach (var zone in zones)
        {
            var rank = TextSearch.Rank(zone.Lower, query, tokens);
            if (rank == TextSearch.NoMatch || rank > best) continue;
            if (rank < best)
            {
                best = rank;
                matches.Clear();
            }

            matches.Add(zone);
        }

        if (matches.Count == 0) return new Resolution(null, []);
        var ordered = matches
            .OrderByDescending(z => prefer?.Invoke(z) ?? true)
            .ThenBy(z => z.IntendedUse > 1)
            .ThenBy(z => z.DutyId != 0)
            .ThenBy(z => z.Id)
            .ToList();
        var names = ordered.Select(z => z.Lower).Distinct(StringComparer.Ordinal).Count();
        return names == 1 ? new Resolution(ordered[0], ordered) : new Resolution(null, ordered.DistinctBy(z => z.Lower).Take(10).ToList());
    }
}

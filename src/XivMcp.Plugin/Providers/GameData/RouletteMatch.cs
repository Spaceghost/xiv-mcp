namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Pure matching of a ContentFinderCondition roulette flag column (LevelingRoulette, MSQRoulette, ...) to a ContentRoulette
/// row. The sheets do not link the two, so the match goes through the English roulette name; no match means the roulette
/// is reported by its column name only.
/// </summary>
internal static class RouletteMatch
{
    internal sealed record Roulette(uint Id, string EnglishName);

    /// <summary>Words that must all appear in the English roulette name, per flag column.</summary>
    private static readonly Dictionary<string, string[]> Keywords = new(StringComparer.Ordinal)
    {
        ["LevelingRoulette"] = ["Leveling"],
        ["HighLevelRoulette"] = ["High-level"],
        ["MSQRoulette"] = ["Main Scenario"],
        ["GuildHestRoulette"] = ["Guildhest"],
        ["ExpertRoulette"] = ["Expert"],
        ["TrialRoulette"] = ["Trials"],
        ["DailyFrontlineChallenge"] = ["Frontline"],
        ["LevelCapRoulette"] = ["Level Cap"],
        ["MentorRoulette"] = ["Mentor"],
        ["AllianceRoulette"] = ["Alliance"],
        ["NormalRaidRoulette"] = ["Normal Raid"],
    };

    /// <summary>The lowest-id roulette whose English name contains every keyword of the column, or null.</summary>
    public static uint? Find(string column, IEnumerable<Roulette> roulettes)
    {
        if (!Keywords.TryGetValue(column, out var words)) return null;
        uint? best = null;
        foreach (var roulette in roulettes)
        {
            if (!words.All(w => roulette.EnglishName.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            if (best == null || roulette.Id < best) best = roulette.Id;
        }

        return best;
    }
}

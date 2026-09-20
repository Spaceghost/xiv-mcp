using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using CsAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Inventory;

/// <summary>Achievement completion for the logged-in character (framework thread).</summary>
[McpProvider("progress")]
public sealed unsafe class AchievementProvider
{
    private readonly GameDataIndex index;

    public AchievementProvider(IDataManager data) => index = GameDataIndex.For(data);

    public sealed record AchievementEntry(uint Id, string Name, string? Description, string? Category, int Points, bool Completed);

    public sealed record AchievementsResult(
        bool Loaded,
        int TotalInGame,
        int Completed,
        int PointsEarned,
        int PointsAvailable,
        int Total,
        int Offset,
        int Returned,
        bool Truncated,
        List<AchievementEntry> Achievements,
        string? Note);

    [McpTool("get_achievements",
        Sources = ["client:Achievement", "lumina:Achievement"],
        Title = "Get achievement progress",
        Description =
            "The character's achievement progress: totalInGame, completed, pointsEarned and pointsAvailable, then a filtered, paged list " +
            "of {id, name, description, category, points, completed}. Filter with completed=all|completed|missing, nameContains " +
            "(matches name or description) and category (case-insensitive substring of the achievement category, e.g. \"Battle\", " +
            "\"Crafting\", \"Quests\"). " +
            "The client only knows which achievements are complete after the Achievements window has been opened once this session: " +
            "until then loaded=false and everything reads as missing, which the note says. Achievements hidden until unlocked are " +
            "included because they are in game data.")]
    public AchievementsResult GetAchievements(
        [McpParam("Filter by completion.", Enum = ["all", "completed", "missing"])] string completed = "all",
        [McpParam("Case-insensitive substring matched against the achievement name or description.")] string? nameContains = null,
        [McpParam("Case-insensitive substring matched against the achievement category.")] string? category = null,
        [McpParam("Maximum entries (1-200).", Minimum = 1, Maximum = 200)] int limit = 50,
        [McpParam("Entries to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var filter = (completed ?? "all").Trim().ToLowerInvariant();
        if (filter is not ("all" or "completed" or "missing"))
        {
            throw new McpToolException("completed must be all, completed or missing.");
        }

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        var state = CsAchievement.Instance();
        var loaded = state != null && state->IsLoaded();

        var entries = new List<AchievementEntry>();
        var totalPoints = 0;
        var earnedPoints = 0;
        var completedCount = 0;

        foreach (var row in index.Sheet<Sheets.Achievement>())
        {
            var name = SheetJson.Text(row.Name);
            if (row.RowId == 0 || name.Length == 0)
            {
                continue;
            }

            var done = loaded && Safe(() => state->IsComplete((int)row.RowId));
            totalPoints += row.Points;
            if (done)
            {
                completedCount++;
                earnedPoints += row.Points;
            }

            entries.Add(new AchievementEntry(
                row.RowId,
                name,
                GameDataIndex.NullIfEmpty(SheetJson.Text(row.Description)),
                GameDataIndex.NullIfEmpty(SheetJson.Text(row.AchievementCategory.ValueNullable?.Name ?? default)),
                row.Points,
                done));
        }

        var needle = nameContains?.Trim() ?? "";
        var categoryNeedle = category?.Trim() ?? "";
        var matching = entries
            .Where(e => filter == "all" || (filter == "completed") == e.Completed)
            .Where(e => needle.Length == 0 ||
                        e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        (e.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(e => categoryNeedle.Length == 0 ||
                        (e.Category?.Contains(categoryNeedle, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        var page = matching.Skip(offset).Take(limit).ToList();
        return new AchievementsResult(
            loaded,
            entries.Count,
            completedCount,
            earnedPoints,
            totalPoints,
            matching.Count,
            offset,
            page.Count,
            offset + page.Count < matching.Count,
            page,
            loaded
                ? null
                : "Achievement data is not loaded: open the Achievements window in game once this session, then call again. Until then everything reads as not completed.");
    }

    private static bool Safe(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch
        {
            return false;
        }
    }
}

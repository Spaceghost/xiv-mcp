using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Duty;

/// <summary>Which duties the character has unlocked and cleared, from the client's own flags (framework thread).</summary>
[McpProvider("duty")]
public sealed class DutyUnlockProvider
{
    private readonly GameDataIndex index;

    public DutyUnlockProvider(IDataManager data) => index = GameDataIndex.For(data);

    [McpTool("get_duty_unlocks",
        Sources = ["client:UIState", "lumina:ContentFinderCondition", "lumina:ContentType"],
        Title = "Get duty unlocks and clears",
        Description =
            "Which Duty Finder duties this character has unlocked and cleared, as far as the client knows. Each duty: dutyId " +
            "(ContentFinderCondition row id, the id search_duties and get_duty use), name, contentType (Dungeons, Trials, Raids, ...), " +
            "level (required level), itemLevel (required average item level), unlocked, completed (cleared at least once), contentKind " +
            "(instanceContent|publicContent|other) and contentId. unlocked/completed are omitted for content kinds the client keeps no " +
            "flag for. Filters: contentType (name substring or ContentType id), minLevel/maxLevel, nameContains, onlyLocked (still locked), " +
            "onlyIncomplete (unlocked but never cleared). Also returns unlocked/completed counts per content type for the filtered scope. " +
            "Sorted by level; paged with limit/offset. Use for 'what have I not unlocked/cleared'; use get_duty for how a duty is " +
            "unlocked, and get_roulette_status for today's roulette bonuses.",
        RequiresLogin = true)]
    public unsafe DutyUnlocksResult GetDutyUnlocks(
        [McpParam("Content type name (partial ok, e.g. \"Dungeon\", \"Trial\", \"Raid\") or ContentType id.")] string? contentType = null,
        [McpParam("Lowest required level to include.", Minimum = 0, Maximum = 100)] int minLevel = 0,
        [McpParam("Highest required level to include; 0 = no upper bound.", Minimum = 0, Maximum = 100)] int maxLevel = 0,
        [McpParam("Only duties that are still locked.")] bool onlyLocked = false,
        [McpParam("Only duties that are unlocked but have never been cleared.")] bool onlyIncomplete = false,
        [McpParam("Case-insensitive duty name substring filter.")] string? nameContains = null,
        [McpParam("Maximum duties (1-500).", Minimum = 1, Maximum = 500)] int limit = 50,
        [McpParam("Duties to skip for paging.", Minimum = 0)] int offset = 0)
    {
        if (UIState.Instance() == null)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Unlock data is not loaded yet; try again once the character is in the world.");

        var records = new List<DutyUnlockRecord>(1200);
        foreach (var row in index.Sheet<Sheets.ContentFinderCondition>())
        {
            var name = SheetJson.Text(row.Name);
            if (row.RowId == 0 || name.Length == 0) continue;

            var kind = "other";
            bool? unlocked = null;
            bool? completed = null;
            var contentId = row.Content.RowId;
            if (contentId != 0 && row.Content.Is<Sheets.InstanceContent>())
            {
                kind = "instanceContent";
                unlocked = UIState.IsInstanceContentUnlocked(contentId);
                completed = UIState.IsInstanceContentCompleted(contentId);
            }
            else if (contentId != 0 && row.Content.Is<Sheets.PublicContent>())
            {
                kind = "publicContent";
                unlocked = UIState.IsPublicContentUnlocked(contentId);
                completed = UIState.IsPublicContentCompleted(contentId);
            }

            var typeId = row.ContentType.RowId;
            var typeName = typeId != 0 ? GameDataIndex.NullIfEmpty(SheetJson.Text(row.ContentType.ValueNullable?.Name ?? default)) : null;
            records.Add(new DutyUnlockRecord(
                row.RowId,
                GameDataIndex.Capitalize(name),
                typeId,
                typeName,
                row.ClassJobLevelRequired,
                row.ItemLevelRequired,
                kind,
                contentId,
                unlocked,
                completed));
        }

        return DutyUnlockShaper.Build(records, contentType, minLevel, maxLevel, onlyLocked, onlyIncomplete, nameContains, limit, offset);
    }
}

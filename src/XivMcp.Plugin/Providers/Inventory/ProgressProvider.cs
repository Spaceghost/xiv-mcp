using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Inventory;

/// <summary>Quest completion and collection unlock progress of the logged-in character (framework thread).</summary>
[McpProvider("progress")]
public sealed unsafe class ProgressProvider
{
    private const int MaxQuestIds = 200;

    private static readonly string[] Kinds =
        ["mounts", "minions", "orchestrion", "emotes", "fashionAccessories", "triadCards", "bardings", "glasses", "ornaments"];

    private readonly GameDataIndex index;
    private readonly IUnlockState unlocks;

    public ProgressProvider(IDataManager data, IUnlockState unlocks)
    {
        index = GameDataIndex.For(data);
        this.unlocks = unlocks;
    }

    [McpTool("get_quest_status",
        Title = "Get quest completion status",
        Description =
            "For each quest id (Quest sheet row id; short ids below 65536 are accepted): whether the character has completed it, whether it is " +
            "currently accepted (in the journal) and its current sequence step, plus name and whether it is repeatable. Unknown ids return " +
            "exists=false. Use search_quests to find quest ids by name. Up to 200 ids per call.",
        RequiresLogin = true)]
    public QuestStatusResult GetQuestStatus([McpParam("Quest ids, e.g. [70000, 69999].")] uint[] questIds)
    {
        if (questIds == null || questIds.Length == 0) throw new McpToolException("questIds must contain at least one id.");
        if (questIds.Length > MaxQuestIds) throw new McpToolException($"At most {MaxQuestIds} quest ids per call.");

        var manager = QuestManager.Instance();
        var list = new List<QuestStatus>(questIds.Length);
        foreach (var raw in questIds)
        {
            var id = QuestDataProvider.NormalizeQuestId(raw);
            if (index.Row<Sheets.Quest>(id) is not { } row)
            {
                list.Add(new QuestStatus(id, null, false, false, false, null, false));
                continue;
            }

            var completed = QuestManager.IsQuestComplete(id);
            var accepted = manager != null && manager->IsQuestAccepted(id);
            var sequence = accepted ? QuestManager.GetQuestSequence(id) : (byte)0;
            list.Add(new QuestStatus(id, GameDataIndex.NullIfEmpty(SheetJson.Text(row.Name)), true, completed, accepted,
                accepted ? sequence : null, row.IsRepeatable));
        }

        return new QuestStatusResult(list.Count(q => q.Completed), list.Count(q => q.Accepted), list);
    }

    [McpTool("get_collection_progress",
        Title = "Get collection progress",
        Description =
            "Unlock progress for one collection kind: mounts, minions, orchestrion (orchestrion rolls), emotes, fashionAccessories (also accepted as " +
            "ornaments), triadCards (Triple Triad cards), bardings (chocobo barding), glasses (facewear styles). Returns totalInGame, owned and missing " +
            "counts for the whole collection, then a filtered, paged list of {id, name, owned}: owned=all|owned|missing and nameContains " +
            "(case-insensitive). The game-data list includes a few entries that cannot currently be obtained, so totals can exceed the in-game " +
            "collection log counts slightly. Emotes available by default count as owned.",
        RequiresLogin = true)]
    public CollectionResult GetCollectionProgress(
        [McpParam("Collection kind.", Enum = ["mounts", "minions", "orchestrion", "emotes", "fashionAccessories", "triadCards", "bardings", "glasses", "ornaments"])]
        string kind,
        [McpParam("Filter the returned list by ownership.", Enum = ["all", "owned", "missing"])] string owned = "all",
        [McpParam("Case-insensitive name substring filter for the returned list.")] string? nameContains = null,
        [McpParam("Maximum entries (1-500).", Minimum = 1, Maximum = 500)] int limit = 50,
        [McpParam("Entries to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var normalizedKind = Kinds.FirstOrDefault(k => k.Equals(kind?.Trim(), StringComparison.OrdinalIgnoreCase))
                             ?? throw new McpToolException($"Unknown kind \"{kind}\". Use: {string.Join(", ", Kinds)}.");
        var ownedFilter = (owned ?? "all").Trim().ToLowerInvariant();
        if (ownedFilter is not ("all" or "owned" or "missing"))
            throw new McpToolException("owned must be all, owned or missing.");

        var entries = Collect(normalizedKind);
        var ownedCount = entries.Count(e => e.Owned);
        var filter = nameContains?.Trim() ?? "";
        var matching = entries
            .Where(e => ownedFilter == "all" || (ownedFilter == "owned") == e.Owned)
            .Where(e => filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var page = Page<CollectionEntry>.From(matching, offset, limit);

        return new CollectionResult(
            normalizedKind == "ornaments" ? "fashionAccessories" : normalizedKind,
            entries.Count,
            ownedCount,
            entries.Count - ownedCount,
            page.Total,
            page.Offset,
            page.Items.Count,
            page.Truncated,
            page.Items,
            ownedCount == 0 && entries.Count > 0
                ? "No unlocks reported: the client's unlock data may not be loaded yet (e.g. right after login); retry shortly."
                : null);
    }

    private List<CollectionEntry> Collect(string kind)
    {
        var list = new List<CollectionEntry>();
        switch (kind)
        {
            case "mounts":
                foreach (var row in index.Sheet<Sheets.Mount>())
                {
                    var name = SheetJson.Text(row.Singular);
                    if (name.Length == 0 || row.Icon == 0 || row.Order < 0) continue;
                    list.Add(new CollectionEntry(row.RowId, GameDataIndex.Capitalize(name), Safe(() => unlocks.IsMountUnlocked(row))));
                }

                break;
            case "minions":
                foreach (var row in index.Sheet<Sheets.Companion>())
                {
                    var name = SheetJson.Text(row.Singular);
                    if (name.Length == 0 || row.Icon == 0) continue;
                    list.Add(new CollectionEntry(row.RowId, GameDataIndex.Capitalize(name), Safe(() => unlocks.IsCompanionUnlocked(row))));
                }

                break;
            case "orchestrion":
                foreach (var row in index.Sheet<Sheets.Orchestrion>())
                {
                    var name = SheetJson.Text(row.Name);
                    if (name.Length == 0) continue;
                    list.Add(new CollectionEntry(row.RowId, name, Safe(() => unlocks.IsOrchestrionUnlocked(row))));
                }

                break;
            case "emotes":
                foreach (var row in index.Sheet<Sheets.Emote>())
                {
                    var name = SheetJson.Text(row.Name);
                    if (name.Length == 0 || row.Icon == 0 || row.EmoteCategory.RowId == 0) continue;
                    list.Add(new CollectionEntry(row.RowId, name, Safe(() => unlocks.IsEmoteUnlocked(row))));
                }

                break;
            case "fashionAccessories":
            case "ornaments":
                foreach (var row in index.Sheet<Sheets.Ornament>())
                {
                    var name = SheetJson.Text(row.Singular);
                    if (name.Length == 0 || row.Order <= 0) continue;
                    list.Add(new CollectionEntry(row.RowId, GameDataIndex.Capitalize(name), Safe(() => unlocks.IsOrnamentUnlocked(row))));
                }

                break;
            case "triadCards":
                var residents = index.Sheet<Sheets.TripleTriadCardResident>();
                foreach (var row in index.Sheet<Sheets.TripleTriadCard>())
                {
                    var name = SheetJson.Text(row.Name);
                    if (name.Length == 0 || !residents.TryGetRow(row.RowId, out var resident) || resident.Order == 0) continue;
                    list.Add(new CollectionEntry(row.RowId, name, Safe(() => unlocks.IsTripleTriadCardUnlocked(row))));
                }

                break;
            case "bardings":
                foreach (var row in index.Sheet<Sheets.BuddyEquip>())
                {
                    var name = SheetJson.Text(row.Name);
                    if (name.Length == 0 || row.Order == 0) continue;
                    list.Add(new CollectionEntry(row.RowId, name, Safe(() => unlocks.IsBuddyEquipUnlocked(row))));
                }

                break;
            case "glasses":
                foreach (var row in index.Sheet<Sheets.GlassesStyle>())
                {
                    var name = SheetJson.Text(row.Name);
                    if (name.Length == 0 || row.Order == 0) continue;
                    list.Add(new CollectionEntry(row.RowId, name, Safe(() => unlocks.IsGlassesStyleUnlocked(row))));
                }

                break;
        }

        return list;
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

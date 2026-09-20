using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Inventory;

/// <summary>The accepted quests as the Journal lists them (framework thread). Shaping lives in <see cref="QuestJournalShaper"/>.</summary>
[McpProvider("progress")]
public sealed unsafe class QuestJournalProvider
{
    private const int TrackedNormalQuest = 1;
    private const int BeastTribeAllowancesPerDay = 12;
    private const int AcceptedQuestLimit = 30;

    private readonly GameDataIndex index;

    public QuestJournalProvider(IDataManager data) => index = GameDataIndex.For(data);

    [McpTool("get_quest_journal",
        Sources = ["client:QuestManager", "lumina:Quest", "lumina:JournalGenre"],
        Title = "Get the quest journal",
        Description =
            "The quests the character has accepted, as the Journal lists them: questId (Quest sheet row id), name, sequence (the client's step " +
            "counter; 255 = all steps done, readyToComplete=true), objectives (the journal to-do text for the current step, looked up in game " +
            "data; objectiveResolved=false when it could not be matched, then rely on sequence), level, genre and category (journal headings), " +
            "priority (marked as priority in the journal), tracked (shown in the duty list on screen), daily/dailyCompleted for tribal dailies, " +
            "repeatable and acceptedAs (the job it was accepted on). Priority and tracked quests sort first. allowances has accepted quest " +
            "count out of 30, tribal (beast tribe) daily allowances remaining out of 12, accepted levequests and leve allowances with the " +
            "next allowance time. Paged with limit/offset. Use this for 'what am I in the middle of'; use get_quest_status to test specific " +
            "quest ids (including completed ones) and get_quest for static quest data such as rewards and prerequisites.",
        RequiresLogin = true)]
    public QuestJournalResult GetQuestJournal(
        [McpParam("Only quests tracked on screen (the duty list).")] bool trackedOnly = false,
        [McpParam("Include quests the player has hidden in the journal.")] bool includeHidden = true,
        [McpParam("Case-insensitive quest name substring filter.")] string? nameContains = null,
        [McpParam("Maximum quests (1-500).", Minimum = 1, Maximum = 500)] int limit = 50,
        [McpParam("Quests to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var manager = QuestManager.Instance();
        if (manager == null)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Quest data is not loaded yet; try again once the character is in the world.");

        var records = ReadRecords(manager);
        var allowances = new QuestAllowances(
            manager->NumAcceptedQuests,
            AcceptedQuestLimit,
            manager->NumAcceptedDailyQuests,
            (int)manager->GetBeastTribeAllowance(),
            BeastTribeAllowancesPerDay,
            manager->NumAcceptedLeveQuests,
            manager->NumLeveAllowances,
            NextLeveAllowances());

        return QuestJournalShaper.Build(
            records,
            StaticInfo,
            job => index.ClassJob(job)?.Name,
            allowances,
            trackedOnly,
            includeHidden,
            nameContains,
            limit,
            offset);
    }

    /// <summary>Copies the accepted-quest array into plain records; no pointer leaves this method.</summary>
    private static List<QuestWorkRecord> ReadRecords(QuestManager* manager)
    {
        var tracked = new HashSet<int>();
        foreach (ref var entry in manager->TrackedQuests)
        {
            if (entry.QuestType == TrackedNormalQuest) tracked.Add(entry.Index);
        }

        var daily = new Dictionary<ushort, bool>();
        foreach (ref var entry in manager->DailyQuests)
        {
            if (entry.QuestId != 0) daily[entry.QuestId] = entry.IsCompleted;
        }

        var list = new List<QuestWorkRecord>();
        var quests = manager->NormalQuests;
        for (var i = 0; i < quests.Length; i++)
        {
            ref var quest = ref quests[i];
            if (quest.QuestId == 0) continue;
            var isDaily = daily.TryGetValue(quest.QuestId, out var dailyDone);
            list.Add(new QuestWorkRecord(
                quest.QuestId,
                quest.Sequence,
                quest.IsPriority,
                quest.IsHidden,
                tracked.Contains(i),
                quest.AcceptClassJob,
                isDaily,
                isDaily && dailyDone));
        }

        return list;
    }

    private static DateTimeOffset? NextLeveAllowances()
    {
        var unix = QuestManager.GetNextLeveAllowancesUnixTimestamp();
        return unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
    }

    private QuestStaticInfo? StaticInfo(uint questId)
    {
        if (index.Row<Sheets.Quest>(questId) is not { } row) return null;
        var name = SheetJson.Text(row.Name);
        if (name.Length == 0) return null;

        var genre = row.JournalGenre.ValueNullable;
        var category = genre?.JournalCategory.ValueNullable;
        var texts = TodoTexts(SheetJson.Text(row.Id));
        var todos = new List<QuestTodo>();
        var todoParams = row.TodoParams;
        for (var i = 0; i < todoParams.Count; i++)
        {
            var sequence = todoParams[i].ToDoCompleteSeq;
            if (sequence == 0) continue;
            todos.Add(new QuestTodo(i, sequence, texts.GetValueOrDefault(i)));
        }

        return new QuestStaticInfo(
            name,
            GameDataIndex.QuestLevel(row),
            genre is { } g ? GameDataIndex.NullIfEmpty(SheetJson.Text(g.Name)) : null,
            category is { } c ? GameDataIndex.NullIfEmpty(SheetJson.Text(c.Name)) : null,
            row.IsRepeatable,
            todos);
    }

    /// <summary>TODO_NN texts from the quest's own text sheet (column 0 = key, column 1 = text). Empty when the sheet is missing.</summary>
    private Dictionary<int, string> TodoTexts(string questKey)
    {
        var result = new Dictionary<int, string>();
        var path = QuestJournalShaper.TextSheetPath(questKey);
        if (path == null) return result;
        try
        {
            var raw = index.Module.GetRawSheet(path, index.Language);
            if (raw.Columns.Count < 2) return result;
            foreach (var textRow in new ExcelSheet<RawRow>(raw))
            {
                if (QuestJournalShaper.TodoIndex(SheetJson.Text(textRow.ReadStringColumn(0))) is not { } todo) continue;
                var text = SheetJson.Text(textRow.ReadStringColumn(1));
                if (text.Length > 0) result[todo] = text;
            }
        }
        catch
        {
            // No text sheet for this quest (or an unexpected layout): fall back to sequence numbers only.
        }

        return result;
    }
}

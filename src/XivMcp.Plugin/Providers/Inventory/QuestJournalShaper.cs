using System.Globalization;

namespace XivMcp.Plugin.Providers.Inventory;

/// <summary>One accepted quest as copied out of QuestManager (no pointers).</summary>
internal sealed record QuestWorkRecord(
    ushort QuestId,
    byte Sequence,
    bool Priority,
    bool Hidden,
    bool Tracked,
    byte AcceptClassJob,
    bool Daily = false,
    bool DailyCompleted = false);

/// <summary>One journal objective from the Quest sheet: the step it belongs to and its text (null when unresolved).</summary>
internal sealed record QuestTodo(int Index, byte Sequence, string? Text);

/// <summary>What game data knows about a quest.</summary>
internal sealed record QuestStaticInfo(
    string Name,
    int Level,
    string? Genre,
    string? Category,
    bool Repeatable,
    IReadOnlyList<QuestTodo> Todos);

public sealed record JournalQuest(
    uint QuestId,
    string? Name,
    int Sequence,
    bool ReadyToComplete,
    IReadOnlyList<string>? Objectives,
    bool ObjectiveResolved,
    int? Level,
    string? Genre,
    string? Category,
    bool Priority,
    bool Tracked,
    bool? Hidden,
    bool? Daily,
    bool? DailyCompleted,
    bool? Repeatable,
    string? AcceptedAs);

public sealed record QuestAllowances(
    int AcceptedQuests,
    int AcceptedQuestLimit,
    int AcceptedDailyQuests,
    int BeastTribeAllowancesRemaining,
    int BeastTribeAllowancesPerDay,
    int AcceptedLeves,
    int LeveAllowances,
    DateTimeOffset? NextLeveAllowancesUtc);

public sealed record QuestJournalResult(
    int Total,
    int Offset,
    int Returned,
    bool Truncated,
    QuestAllowances? Allowances,
    IReadOnlyList<JournalQuest> Quests,
    string Note);

/// <summary>Pure shaping for get_quest_journal: naming, objective lookup, filtering, ordering and paging.</summary>
internal static class QuestJournalShaper
{
    public const uint QuestIdBase = 65536;
    public const byte FinalSequence = 255;
    public const int MaxObjectiveLength = 400;

    public const string Note =
        "sequence is the raw step counter from the client (255 = every step done, hand the quest in). objectives are looked up in game data " +
        "(the quest's own text sheet, steps matched by Quest.TodoParams.ToDoCompleteSeq); objectiveResolved=false means the text could not be " +
        "matched and only the sequence is reliable. tracked comes from the client's tracked-quest list. Not verified in game.";

    /// <summary>Path of a quest's text sheet: quest/&lt;first three digits of the number&gt;/&lt;Id&gt;, e.g. quest/000/SubFst010_00039.</summary>
    public static string? TextSheetPath(string? questKey)
    {
        if (string.IsNullOrWhiteSpace(questKey)) return null;
        var key = questKey.Trim();
        var underscore = key.LastIndexOf('_');
        if (underscore < 0 || key.Length - underscore - 1 < 5) return null;
        var digits = key.AsSpan(underscore + 1, 5);
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c)) return null;
        }

        return $"quest/{digits[..3]}/{key}";
    }

    /// <summary>Index N of a TEXT_&lt;ID&gt;_TODO_NN key, or null for any other key.</summary>
    public static int? TodoIndex(string? textKey)
    {
        if (string.IsNullOrEmpty(textKey)) return null;
        const string marker = "_TODO_";
        var at = textKey.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var tail = textKey.AsSpan(at + marker.Length);
        return tail.Length is > 0 and <= 3 && int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>Objective texts for the quest's current step; empty when nothing matches.</summary>
    public static List<string> Objectives(QuestStaticInfo info, byte sequence)
    {
        var list = new List<string>();
        foreach (var todo in info.Todos)
        {
            if (todo.Sequence != sequence || string.IsNullOrWhiteSpace(todo.Text)) continue;
            var text = todo.Text.Trim();
            if (text.Length > MaxObjectiveLength) text = text[..MaxObjectiveLength] + "...";
            if (!list.Contains(text, StringComparer.Ordinal)) list.Add(text);
        }

        return list;
    }

    public static JournalQuest Shape(QuestWorkRecord work, QuestStaticInfo? info, Func<byte, string?> classJobName)
    {
        var objectives = info == null ? [] : Objectives(info, work.Sequence);
        return new JournalQuest(
            work.QuestId + QuestIdBase,
            info?.Name,
            work.Sequence,
            work.Sequence == FinalSequence,
            objectives.Count > 0 ? objectives : null,
            objectives.Count > 0,
            info?.Level,
            info?.Genre,
            info?.Category,
            work.Priority,
            work.Tracked,
            work.Hidden ? true : null,
            work.Daily ? true : null,
            work.Daily ? work.DailyCompleted : null,
            info is { Repeatable: true } ? true : null,
            work.AcceptClassJob != 0 ? classJobName(work.AcceptClassJob) : null);
    }

    public static QuestJournalResult Build(
        IEnumerable<QuestWorkRecord> records,
        Func<uint, QuestStaticInfo?> lookup,
        Func<byte, string?> classJobName,
        QuestAllowances? allowances,
        bool trackedOnly,
        bool includeHidden,
        string? nameContains,
        int limit,
        int offset)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var needle = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();

        var all = records
            .Where(r => r.QuestId != 0)
            .Where(r => includeHidden || !r.Hidden)
            .Where(r => !trackedOnly || r.Tracked)
            .Select(r => Shape(r, lookup(r.QuestId + QuestIdBase), classJobName))
            .Where(q => needle == null || (q.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(q => q.Priority)
            .ThenByDescending(q => q.Tracked)
            .ThenBy(q => q.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.QuestId)
            .ToList();

        var page = all.Skip(offset).Take(limit).ToList();
        return new QuestJournalResult(all.Count, offset, page.Count, offset + page.Count < all.Count, allowances, page, Note);
    }
}

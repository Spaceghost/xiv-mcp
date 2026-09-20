namespace XivMcp.Plugin.Providers.Duty;

/// <summary>One duty with the client's unlock/completion flags (null = the client exposes no flag for this kind of content).</summary>
internal sealed record DutyUnlockRecord(
    uint DutyId,
    string Name,
    uint ContentTypeId,
    string? ContentType,
    int Level,
    int ItemLevel,
    string ContentKind,
    uint ContentId,
    bool? Unlocked,
    bool? Completed);

public sealed record DutyUnlockDto(
    uint DutyId,
    string Name,
    string? ContentType,
    int Level,
    int? ItemLevel,
    bool? Unlocked,
    bool? Completed,
    string ContentKind,
    uint ContentId);

public sealed record DutyUnlockCount(string ContentType, int Known, int Unlocked, int Completed);

public sealed record DutyUnlocksResult(
    int Total,
    int Offset,
    int Returned,
    bool Truncated,
    int Known,
    int Unlocked,
    int Completed,
    IReadOnlyList<DutyUnlockCount> ByContentType,
    IReadOnlyList<DutyUnlockDto> Duties,
    string Note);

/// <summary>Pure shaping for get_duty_unlocks: filters, per-type counts, ordering and paging.</summary>
internal static class DutyUnlockShaper
{
    public const string Note =
        "unlocked/completed come from the client's instance content and public content flags (UIState); they are omitted for duties whose " +
        "content kind has no such flag (contentKind other), and counts only cover duties with a flag. completed means cleared at least once. " +
        "Roulettes are not listed: the client exposes no simple roulette unlock flag, and get_roulette_status has the daily bonus state. " +
        "Not verified in game.";

    public static bool MatchesType(DutyUnlockRecord record, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return true;
        var text = contentType.Trim();
        if (uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
            return record.ContentTypeId == id;
        return record.ContentType?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public static DutyUnlocksResult Build(
        IEnumerable<DutyUnlockRecord> records,
        string? contentType,
        int minLevel,
        int maxLevel,
        bool onlyLocked,
        bool onlyIncomplete,
        string? nameContains,
        int limit,
        int offset)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        if (maxLevel > 0 && minLevel > maxLevel)
            throw XivMcp.Core.McpToolException.WithCode(XivMcp.Core.McpErrorCodes.InvalidArguments, $"minLevel {minLevel} is above maxLevel {maxLevel}.");
        var needle = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();

        var scoped = records
            .Where(r => MatchesType(r, contentType))
            .Where(r => r.Level >= minLevel && (maxLevel <= 0 || r.Level <= maxLevel))
            .Where(r => needle == null || r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var counts = scoped
            .Where(r => r.Unlocked != null)
            .GroupBy(r => r.ContentType ?? "Other", StringComparer.OrdinalIgnoreCase)
            .Select(g => new DutyUnlockCount(g.Key, g.Count(), g.Count(r => r.Unlocked == true), g.Count(r => r.Completed == true)))
            .OrderBy(c => c.ContentType, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var listed = scoped
            .Where(r => !onlyLocked || r.Unlocked == false)
            .Where(r => !onlyIncomplete || (r.Unlocked == true && r.Completed == false))
            .OrderBy(r => r.Level)
            .ThenBy(r => r.ItemLevel)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var page = listed.Skip(offset).Take(limit)
            .Select(r => new DutyUnlockDto(r.DutyId, r.Name, r.ContentType, r.Level, r.ItemLevel > 0 ? r.ItemLevel : null, r.Unlocked, r.Completed, r.ContentKind, r.ContentId))
            .ToList();

        return new DutyUnlocksResult(
            listed.Count,
            offset,
            page.Count,
            offset + page.Count < listed.Count,
            counts.Sum(c => c.Known),
            counts.Sum(c => c.Unlocked),
            counts.Sum(c => c.Completed),
            counts,
            page,
            Note);
    }
}

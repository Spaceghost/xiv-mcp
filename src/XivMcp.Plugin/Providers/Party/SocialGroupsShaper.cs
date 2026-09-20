namespace XivMcp.Plugin.Providers.Party;

/// <summary>Names copied from the client's social info proxies (no pointers). A null list means that proxy was not available.</summary>
internal sealed record SocialGroupsRecord(
    bool FreeCompanyProxyAvailable,
    ulong FreeCompanyId,
    string? FreeCompanyName,
    string? FreeCompanyTag,
    int FreeCompanyRank,
    string? GrandCompany,
    IReadOnlyList<string?>? Linkshells,
    IReadOnlyList<string?>? CrossWorldLinkshells);

public sealed record FreeCompanyDto(bool Loaded, bool Member, string? Name, string? Tag, int? Rank, string? GrandCompany, string? Note);

public sealed record GroupNamesDto(bool Loaded, int Count, IReadOnlyList<GroupNameDto> Groups, string? Note);

public sealed record GroupNameDto(int Slot, string Name);

public sealed record SocialGroupsResult(
    FreeCompanyDto FreeCompany,
    GroupNamesDto Linkshells,
    GroupNamesDto CrossWorldLinkshells,
    IReadOnlyList<string> NotExposed,
    string Note);

/// <summary>Pure shaping for list_social_groups. Names only: nothing about members ever enters these records.</summary>
internal static class SocialGroupsShaper
{
    public const int MaxNameLength = 64;

    public const string Note =
        "Names of the groups this character belongs to, nothing else: no member lists, ranks of other players or chat. " +
        "Linkshell slots are numbered as in the chat commands (/linkshell1-8, /cwlinkshell1-8). Not verified in game.";

    public static readonly string[] NotExposed =
    [
        "pvpTeam: the client structures available to this plugin carry no PvP team name",
        "fellowships: the fellowship list carries no names until its window is opened, and none are read here",
        "freeCompany.memberRank: the character's own rank title is only in the member list, which is never read",
    ];

    public static SocialGroupsResult Build(SocialGroupsRecord record)
    {
        return new SocialGroupsResult(
            FreeCompany(record),
            Names(record.Linkshells, "linkshell data is not available yet; it arrives shortly after login"),
            Names(record.CrossWorldLinkshells, "cross-world linkshell data is not available yet; it arrives shortly after login"),
            NotExposed,
            Note);
    }

    public static FreeCompanyDto FreeCompany(SocialGroupsRecord record)
    {
        var tag = Clean(record.FreeCompanyTag);
        var name = Clean(record.FreeCompanyName);
        if (record.FreeCompanyProxyAvailable && record.FreeCompanyId != 0 && name != null)
            return new FreeCompanyDto(true, true, name, tag, record.FreeCompanyRank > 0 ? record.FreeCompanyRank : null, Clean(record.GrandCompany), null);

        // The nameplate tag is always present for members, so it tells "no free company" apart from "details not loaded".
        if (tag != null)
            return new FreeCompanyDto(false, true, null, tag, null, null,
                "The character shows a free company tag but the company details are not in memory; they load when the Free Company window is opened.");

        return new FreeCompanyDto(record.FreeCompanyProxyAvailable, false, null, null, null, null,
            record.FreeCompanyProxyAvailable ? null : "Free company data is not available yet.");
    }

    public static GroupNamesDto Names(IReadOnlyList<string?>? names, string unavailableNote)
    {
        if (names == null) return new GroupNamesDto(false, 0, [], unavailableNote);
        var list = new List<GroupNameDto>();
        for (var i = 0; i < names.Count; i++)
        {
            if (Clean(names[i]) is { } name) list.Add(new GroupNameDto(i + 1, name));
        }

        return new GroupNamesDto(true, list.Count, list,
            list.Count == 0 ? "None listed. Right after login this can also mean the list has not arrived yet." : null);
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length > MaxNameLength ? text[..MaxNameLength] : text;
    }
}

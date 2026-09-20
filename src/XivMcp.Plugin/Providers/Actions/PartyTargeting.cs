using System.Globalization;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>One row of the on-screen party list. Slot is 1-based, top to bottom.</summary>
public sealed record PartySlot(int Slot, string Name, string? World, uint EntityId);

/// <summary>
/// Pure resolution of "which party member did the caller name" for target_party_member. Deliberately has no notion of
/// lowest HP, next, role or any other filter: one named member (or one numbered slot) per approved call.
/// </summary>
public static class PartyTargeting
{
    public const int MaxSlots = 8;

    /// <summary>Splits "Firstname Lastname@World" into name and optional world.</summary>
    public static (string Name, string? World) ParseName(string text)
    {
        var trimmed = text.Trim();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
            return (trimmed, null);
        var name = trimmed[..at].Trim();
        var world = trimmed[(at + 1)..].Trim();
        if (name.Length == 0 || world.Length == 0 || world.Contains('@', StringComparison.Ordinal))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"'{trimmed}' is not a character name. Use \"Firstname Lastname\" or \"Firstname Lastname@World\".");
        return (name, world);
    }

    public static PartySlot Resolve(IReadOnlyList<PartySlot> party, string? name, int? slot)
    {
        if (slot.HasValue == !string.IsNullOrWhiteSpace(name))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Pass exactly one of name or slot.");
        if (party.Count == 0)
            throw McpToolException.WithCode(McpErrorCodes.NotFound, "The party list is empty. Use set_target for anything that is not a party member.");

        if (slot is { } number)
        {
            if (number is < 1 or > MaxSlots)
                throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"slot {number} is out of range; party list slots are 1-{MaxSlots}, top to bottom.");
            return party.FirstOrDefault(m => m.Slot == number)
                ?? throw McpToolException.WithCode(McpErrorCodes.NotFound, $"Party list slot {number} is empty; the party has {party.Count} member(s): {Describe(party)}.");
        }

        var (wantedName, wantedWorld) = ParseName(name!);
        var pool = wantedWorld is null
            ? party
            : party.Where(m => string.Equals(m.World, wantedWorld, StringComparison.OrdinalIgnoreCase)).ToList();

        var exact = pool.Where(m => string.Equals(m.Name, wantedName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            return exact[0];
        if (exact.Count > 1)
            throw Ambiguous(name!, exact);

        var partial = pool.Where(m => m.Name.Contains(wantedName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1)
            return partial[0];
        if (partial.Count > 1)
            throw Ambiguous(name!, partial);
        throw McpToolException.WithCode(McpErrorCodes.NotFound, $"No party member named like '{name!.Trim()}'. The party is: {Describe(party)}.");
    }

    public static string Describe(IEnumerable<PartySlot> members) =>
        string.Join(", ", members.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Slot}: {FullName(m)}")));

    public static string FullName(PartySlot member) => string.IsNullOrEmpty(member.World) ? member.Name : $"{member.Name}@{member.World}";

    private static McpToolException Ambiguous(string name, IReadOnlyList<PartySlot> matches) =>
        McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"'{name.Trim()}' matches {matches.Count} party members ({Describe(matches)}). Use the full name with @World, or the slot number.");
}

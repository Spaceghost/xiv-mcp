namespace XivMcp.Plugin.Providers.Actions;

/// <summary>One hotbar slot as copied out of RaptureHotbarModule (no pointers). Types are the ClientStructs enum member names.</summary>
internal sealed record HotbarSlotRecord(
    int Slot,
    string CommandType,
    uint CommandId,
    string ApparentType,
    uint ApparentId,
    uint IconId,
    string? PopUpHelp,
    string? KeybindHint);

internal sealed record HotbarRecord(int RawIndex, bool? Shared, IReadOnlyList<HotbarSlotRecord> Slots);

public sealed record HotbarSlotDto(
    int Slot,
    string Kind,
    uint Id,
    string? Name,
    string? Keybind,
    uint? IconId,
    string? ShownAsKind,
    uint? ShownAsId,
    string? ShownAsName,
    string? MacroSet,
    int? MacroIndex);

public sealed record HotbarDto(string Kind, int Number, bool? SharedAcrossJobs, int SlotCount, int Used, IReadOnlyList<HotbarSlotDto> Slots);

public sealed record HotbarsResult(string? Job, bool PvpHotbarsActive, int Bars, IReadOnlyList<HotbarDto> Hotbars, string Note);

/// <summary>Pure shaping for list_hotbars: bar numbering, slot-kind naming, macro id decoding and name resolution.</summary>
internal static class HotbarShaper
{
    public const int StandardBars = 10;
    public const int CrossBars = 8;
    public const int StandardSlots = 12;
    public const int CrossSlots = 16;
    public const int MaxNameLength = 120;

    public const string Note =
        "Read-only listing of what is assigned to each slot; nothing here can press a slot. id is the row id for kind (action -> Action sheet, " +
        "item -> Item, macro -> macro number, gearset -> gear set index as stored by the client, ...). shownAs* appears when the slot currently " +
        "displays something other than what is assigned (upgraded or combo actions). Not verified in game.";

    public static readonly string[] BarKinds = ["standard", "cross", "pet", "petCross", "all"];

    /// <summary>ClientStructs HotbarSlotType member name to the kind reported to the model (camelCase, stable spellings).</summary>
    public static string Kind(string? slotType) => slotType switch
    {
        null or "" or "Empty" => "empty",
        "GearSet" => "gearset",
        "PvPQuickChat" => "pvpQuickChat",
        "PvPCombo" => "pvpCombo",
        "McGuffin" => "collectionItem",
        "BgcArmyAction" => "squadronOrder",
        _ when slotType.StartsWith("Unknown", StringComparison.Ordinal) || char.IsAsciiDigit(slotType[0]) => "unknown",
        _ => char.ToLowerInvariant(slotType[0]) + slotType[1..],
    };

    /// <summary>The client numbers shared macros from 256: id 0-99 is this character's macro, 256-355 the shared page.</summary>
    public static (string Set, int Index)? Macro(uint commandId) => commandId switch
    {
        < 100 => ("individual", (int)commandId),
        >= 256 and < 356 => ("shared", (int)commandId - 256),
        _ => null,
    };

    /// <summary>The name part of the hover text, which the client builds as "Name [keybind]" in most cases.</summary>
    public static string? NameFromPopUp(string? popUpHelp, string? keybind)
    {
        if (string.IsNullOrWhiteSpace(popUpHelp)) return null;
        var text = popUpHelp.Trim();
        if (!string.IsNullOrWhiteSpace(keybind))
        {
            var suffix = keybind.Trim();
            if (text.EndsWith(suffix, StringComparison.Ordinal)) text = text[..^suffix.Length].TrimEnd();
        }

        if (text.EndsWith(']') && text.LastIndexOf('[') is > 0 and var open) text = text[..open].TrimEnd();
        return text.Length == 0 ? null : Cap(text);
    }

    public static HotbarSlotDto Shape(HotbarSlotRecord slot, Func<string, uint, string?> nameLookup)
    {
        var kind = Kind(slot.CommandType);
        var keybind = string.IsNullOrWhiteSpace(slot.KeybindHint) ? null : slot.KeybindHint.Trim();
        if (kind == "empty" || slot.CommandId == 0)
            return new HotbarSlotDto(slot.Slot, "empty", 0, null, keybind, null, null, null, null, null, null);

        var name = Cap(nameLookup(kind, slot.CommandId));
        var shownKind = Kind(slot.ApparentType);
        var differs = shownKind != "empty" && slot.ApparentId != 0 && (shownKind != kind || slot.ApparentId != slot.CommandId);
        var shownName = differs ? Cap(nameLookup(shownKind, slot.ApparentId)) ?? NameFromPopUp(slot.PopUpHelp, keybind) : null;
        name ??= differs ? null : NameFromPopUp(slot.PopUpHelp, keybind);
        var macro = kind == "macro" ? Macro(slot.CommandId) : null;

        return new HotbarSlotDto(
            slot.Slot,
            kind,
            slot.CommandId,
            name,
            keybind,
            slot.IconId != 0 ? slot.IconId : null,
            differs ? shownKind : null,
            differs ? slot.ApparentId : null,
            shownName,
            macro?.Set,
            macro?.Index);
    }

    /// <summary>Raw module indexes for a request: standard 1-10 are 0-9, cross 1-8 are 10-17; pet bars are separate (-1, -2).</summary>
    public static List<(string Kind, int Number, int RawIndex)> Select(string? bars, int? number)
    {
        var kind = BarKinds.FirstOrDefault(k => k.Equals(bars?.Trim() ?? "standard", StringComparison.OrdinalIgnoreCase))
                   ?? throw XivMcp.Core.McpToolException.WithCode(
                       XivMcp.Core.McpErrorCodes.InvalidArguments, $"Unknown bars \"{bars}\". Use: {string.Join(", ", BarKinds)}.");
        if (number != null && kind is "all" or "pet" or "petCross")
            throw XivMcp.Core.McpToolException.WithCode(
                XivMcp.Core.McpErrorCodes.InvalidArguments, "number selects one standard (1-10) or cross (1-8) hotbar; set bars to standard or cross.");
        var list = new List<(string, int, int)>();

        void Range(string k, int count, int rawBase)
        {
            if (number is { } n)
            {
                if (n < 1 || n > count)
                    throw XivMcp.Core.McpToolException.WithCode(
                        XivMcp.Core.McpErrorCodes.InvalidArguments, $"{k} hotbars are numbered 1-{count}; got {n}.");
                list.Add((k, n, rawBase + n - 1));
                return;
            }

            for (var i = 0; i < count; i++) list.Add((k, i + 1, rawBase + i));
        }

        if (kind is "standard" or "all") Range("standard", StandardBars, 0);
        if (kind is "cross" or "all") Range("cross", CrossBars, StandardBars);
        if (kind is "pet" or "all") list.Add(("pet", 1, -1));
        if (kind is "petCross" or "all") list.Add(("petCross", 1, -2));
        return list;
    }

    public static HotbarDto ShapeBar(string kind, int number, HotbarRecord record, bool includeEmpty, Func<string, uint, string?> nameLookup)
    {
        var slotCount = kind is "cross" or "petCross" ? CrossSlots : StandardSlots;
        var shaped = record.Slots.Where(s => s.Slot >= 1 && s.Slot <= slotCount).Select(s => Shape(s, nameLookup)).ToList();
        var used = shaped.Count(s => s.Kind != "empty");
        return new HotbarDto(kind, number, record.Shared, slotCount, used, includeEmpty ? shaped : shaped.Where(s => s.Kind != "empty").ToList());
    }

    private static string? Cap(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        return t.Length > MaxNameLength ? t[..MaxNameLength] : t;
    }
}

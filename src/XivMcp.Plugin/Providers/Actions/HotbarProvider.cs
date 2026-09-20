using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Text.ReadOnly;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>Read-only listing of the hotbars. Copies slots out on the framework thread; shaping lives in <see cref="HotbarShaper"/>.</summary>
[McpProvider("actions")]
public sealed unsafe class HotbarProvider
{
    private readonly GameDataIndex index;

    public HotbarProvider(IDataManager data) => index = GameDataIndex.For(data);

    [McpTool("list_hotbars",
        Sources =
        [
            "client:RaptureHotbarModule", "client:RaptureMacroModule", "client:RaptureGearsetModule", "lumina:Action", "lumina:Item",
            "lumina:GeneralAction", "lumina:Emote",
        ],
        Title = "List hotbar contents",
        Description =
            "What is on the player's hotbars for the current job, as the HUD shows them. bars: standard (hotbars 1-10, 12 slots), cross " +
            "(cross hotbar sets 1-8, 16 slots), pet, petCross, or all; number picks one standard/cross bar. Each bar: kind, number, " +
            "sharedAcrossJobs, slotCount, used, and slots {slot (1-based), kind (action, item, macro, gearset, emote, generalAction, " +
            "craftAction, petAction, buddyAction, mainCommand, extraCommand, mount, companion, marker, fieldMarker, recipe, ...), id, " +
            "name (resolved from game data; macro title or gear set name for those kinds), keybind (the hint drawn on the slot, e.g. " +
            "'1' or 'c2'), iconId, shownAs* when the slot currently displays a different action than the one assigned, macroSet + " +
            "macroIndex for macros (match list_macros)}. Empty slots are skipped unless includeEmpty=true. Strictly a listing: no tool " +
            "presses a hotbar slot. Use for 'what is on my bars', 'where did I put Sprint', hotbar layout advice.",
        RequiresLogin = true)]
    public HotbarsResult ListHotbars(
        [McpParam("Which bars to list.", Enum = ["standard", "cross", "pet", "petCross", "all"])] string bars = "standard",
        [McpParam("One bar number: 1-10 for standard, 1-8 for cross. Omit for every bar of that kind.", Minimum = 1, Maximum = 10)] int? number = null,
        [McpParam("Include empty slots.")] bool includeEmpty = false)
    {
        var selection = HotbarShaper.Select(bars, number);
        var module = RaptureHotbarModule.Instance();
        if (module == null || !module->ModuleReady)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Hotbar data is not loaded yet; try again once the character is in the world.");

        Func<string, uint, string?> lookup = ResolveName;
        var result = new List<HotbarDto>(selection.Count);
        foreach (var (kind, barNumber, rawIndex) in selection)
        {
            var record = rawIndex switch
            {
                -1 => Copy(ref module->PetHotbar, rawIndex, null),
                -2 => Copy(ref module->PetCrossHotbar, rawIndex, null),
                _ => Copy(ref module->Hotbars[rawIndex], rawIndex, module->IsHotbarShared((uint)rawIndex)),
            };
            result.Add(HotbarShaper.ShapeBar(kind, barNumber, record, includeEmpty, lookup));
        }

        var job = module->ActiveHotbarClassJobId != 0 ? index.ClassJob(module->ActiveHotbarClassJobId)?.Name : null;
        return new HotbarsResult(job, module->PvPHotbarsActive, result.Count, result, HotbarShaper.Note);
    }

    private static HotbarRecord Copy(ref RaptureHotbarModule.Hotbar bar, int rawIndex, bool? shared)
    {
        var slots = bar.Slots;
        var list = new List<HotbarSlotRecord>(slots.Length);
        for (var i = 0; i < slots.Length; i++)
        {
            ref var slot = ref slots[i];
            list.Add(new HotbarSlotRecord(
                i + 1,
                slot.CommandType.ToString(),
                slot.CommandId,
                slot.ApparentSlotType.ToString(),
                slot.ApparentActionId,
                slot.IconId,
                slot.IsEmpty ? null : Read(ref slot.PopUpHelp),
                NullIfEmpty(slot.KeybindHintString)));
        }

        return new HotbarRecord(rawIndex, shared, list);
    }

    /// <summary>Name for a slot kind + id. Game data first; macro titles and gear set names come from their client modules.</summary>
    private string? ResolveName(string kind, uint id)
    {
        try
        {
            return kind switch
            {
                "action" or "pvpCombo" => Sheet<Sheets.Action>(id)?.Name is { } n ? Text(n) : null,
                "item" or "inventoryItem" or "crystal" or "lostFindsItem" => index.Item(id % 1_000_000) is { } ? index.ItemName(id % 1_000_000) : null,
                "eventItem" or "keyItem" => Sheet<Sheets.EventItem>(id)?.Name is { } n ? Text(n) : null,
                "emote" => Sheet<Sheets.Emote>(id)?.Name is { } n ? Text(n) : null,
                "macro" => MacroTitle(id),
                "marker" => Sheet<Sheets.Marker>(id)?.Name is { } n ? Text(n) : null,
                "fieldMarker" => Sheet<Sheets.FieldMarker>(id)?.Name is { } n ? Text(n) : null,
                "craftAction" => Sheet<Sheets.CraftAction>(id)?.Name is { } n ? Text(n) : null,
                "generalAction" => Sheet<Sheets.GeneralAction>(id)?.Name is { } n ? Text(n) : null,
                "buddyAction" => Sheet<Sheets.BuddyAction>(id)?.Name is { } n ? Text(n) : null,
                "petAction" => Sheet<Sheets.PetAction>(id)?.Name is { } n ? Text(n) : null,
                "mainCommand" => Sheet<Sheets.MainCommand>(id)?.Name is { } n ? Text(n) : null,
                "extraCommand" => Sheet<Sheets.ExtraCommand>(id)?.Name is { } n ? Text(n) : null,
                "companion" => Sheet<Sheets.Companion>(id)?.Singular is { } n ? Text(n) : null,
                "mount" => Sheet<Sheets.Mount>(id)?.Singular is { } n ? Text(n) : null,
                "ornament" => Sheet<Sheets.Ornament>(id)?.Singular is { } n ? Text(n) : null,
                "glasses" => Sheet<Sheets.Glasses>(id)?.Name is { } n ? Text(n) : null,
                "classJob" => index.ClassJob(id)?.Name,
                "recipe" => Sheet<Sheets.Recipe>(id)?.ItemResult.RowId is > 0 and var item ? index.ItemName(item) : null,
                "gearset" => GearsetName(id),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private T? Sheet<T>(uint id)
        where T : struct, Lumina.Excel.IExcelRow<T> => index.Row<T>(id);

    private static string? Text(ReadOnlySeString value) => GameDataIndex.NullIfEmpty(GameDataIndex.Capitalize(SheetJson.Text(value)));

    private static string? MacroTitle(uint commandId)
    {
        if (HotbarShaper.Macro(commandId) is not { } macro) return null;
        var module = RaptureMacroModule.Instance();
        if (module == null) return null;
        var page = macro.Set == "shared" ? module->Shared : module->Individual;
        if (macro.Index < 0 || macro.Index >= page.Length) return null;
        return Read(ref page[macro.Index].Name);
    }

    private static string? GearsetName(uint id)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null || id > 99 || !module->IsValidGearset((int)id)) return null;
        var entries = module->Entries;
        return id < entries.Length ? NullIfEmpty(entries[(int)id].NameString) : null;
    }

    private static string? Read(ref Utf8String str)
    {
        if (str.StringPtr.Value == null || str.BufUsed <= 1) return null;
        try
        {
            return NullIfEmpty(((ReadOnlySeStringSpan)str.AsSpan()).ExtractText().Trim());
        }
        catch
        {
            return NullIfEmpty(str.ToString().Trim());
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

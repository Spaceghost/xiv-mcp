using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>
/// The native half of write_macro / clear_macro (framework thread only). Everything goes through the client's own
/// functions: RaptureMacroModule.GetMacro, Utf8String.SetString, Macro.SetIcon, Macro.Clear,
/// RaptureMacroModule.SetSavePendingFlag (what the client calls after any macro edit so MACRO.DAT / MACROSYS.DAT get
/// saved) and RaptureHotbarModule.ReloadMacroSlots (what it calls so hotbar slots show the new name and icon).
/// </summary>
internal static unsafe class MacroMemory
{
    public static MacroWriteProvider.MacroContent Read(MacroProvider.MacroSet set, int index) => Snapshot(Slot(set, index));

    public static MacroWriteProvider.MacroContent Replace(MacroProvider.MacroSet set, int index, string title, IReadOnlyList<string> lines, uint? iconId)
    {
        var macro = Slot(set, index);
        var previous = Snapshot(macro);

        macro->Name.SetString(title);
        var slots = macro->Lines;
        for (var i = 0; i < slots.Length; i++)
            slots[i].SetString(i < lines.Count ? lines[i] : "");
        if (iconId is { } icon)
            macro->SetIcon(icon);
        else if (macro->IconId == 0)
            macro->SetIcon(MacroText.DefaultIconId);

        Commit(set, index);
        return previous;
    }

    public static MacroWriteProvider.MacroContent Clear(MacroProvider.MacroSet set, int index)
    {
        var macro = Slot(set, index);
        var previous = Snapshot(macro);
        if (previous.Title.Length == 0 && previous.Lines.Count == 0)
            return previous;

        macro->Clear();
        Commit(set, index);
        return previous;
    }

    private static RaptureMacroModule.Macro* Slot(MacroProvider.MacroSet set, int index)
    {
        MacroText.EnsureSlot(index);
        var module = RaptureMacroModule.Instance();
        if (module == null)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Macro data is not loaded yet.", retryable: true);
        var macro = module->GetMacro((uint)set, (uint)index);
        if (macro == null)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"The game returned no macro for {set} slot {index}.");
        return macro;
    }

    private static void Commit(MacroProvider.MacroSet set, int index)
    {
        var module = RaptureMacroModule.Instance();
        if (module != null)
            module->SetSavePendingFlag(true, (uint)set);
        var hotbars = RaptureHotbarModule.Instance();
        if (hotbars != null)
            hotbars->ReloadMacroSlots((byte)set, (byte)index);
    }

    private static MacroWriteProvider.MacroContent Snapshot(RaptureMacroModule.Macro* macro)
    {
        var lines = new List<string>();
        var slots = macro->Lines;
        for (var i = 0; i < slots.Length; i++)
            lines.Add(MacroProvider.Read(ref slots[i]));
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return new MacroWriteProvider.MacroContent(MacroProvider.Read(ref macro->Name), macro->IconId, lines);
    }
}

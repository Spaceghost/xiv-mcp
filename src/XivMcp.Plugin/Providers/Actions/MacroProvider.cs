using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Text.ReadOnly;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>Read-only access to the user's user macros.</summary>
[McpProvider("actions")]
public sealed unsafe class MacroProvider
{
    public enum MacroSet
    {
        Individual,
        Shared,
    }

    [McpTool("list_macros",
        Sources = ["client:RaptureMacroModule"],
        Title = "List user macros",
        Description =
            "Lists the user's macros from the in-game User Macros window: set individual (this character) or shared (all characters on the account), 100 slots each. " +
            "Each entry: index (0-99 slot number), name, iconId, lineCount and lines (the macro's text lines with trailing empty lines removed). " +
            "By default empty slots are skipped. Read-only; use execute_command with the lines if the user wants to run one step manually.",
        Permission = ToolPermission.Read)]
    public MacroListResult ListMacros(
        [McpParam("Which macro page to read.")] MacroSet set = MacroSet.Individual,
        [McpParam("Skip slots with no name and no lines.")] bool nonEmptyOnly = true)
    {
        var module = RaptureMacroModule.Instance();
        if (module == null)
            throw new McpToolException("Macro data is not loaded yet.");

        var macros = set == MacroSet.Shared ? module->Shared : module->Individual;
        var list = new List<MacroInfo>();
        for (var i = 0; i < macros.Length; i++)
        {
            ref var macro = ref macros[i];
            var name = Read(ref macro.Name);
            var lines = new List<string>();
            var lastNonEmpty = -1;
            var macroLines = macro.Lines;
            for (var l = 0; l < macroLines.Length; l++)
            {
                var text = Read(ref macroLines[l]);
                lines.Add(text);
                if (text.Length > 0)
                    lastNonEmpty = l;
            }

            lines.RemoveRange(lastNonEmpty + 1, lines.Count - lastNonEmpty - 1);
            if (nonEmptyOnly && name.Length == 0 && lines.Count == 0)
                continue;

            list.Add(new MacroInfo(i, name, macro.IconId, lines.Count, lines));
        }

        return new MacroListResult(set.ToString().ToLowerInvariant(), list, list.Count);
    }

    private static string Read(ref Utf8String str)
    {
        if (str.StringPtr.Value == null || str.BufUsed <= 1)
            return "";
        try
        {
            return ((ReadOnlySeStringSpan)str.AsSpan()).ExtractText().TrimEnd();
        }
        catch
        {
            return str.ToString().TrimEnd();
        }
    }

    public sealed record MacroInfo(int Index, string Name, uint IconId, int LineCount, IReadOnlyList<string> Lines);

    public sealed record MacroListResult(string Set, IReadOnlyList<MacroInfo> Macros, int Count);
}

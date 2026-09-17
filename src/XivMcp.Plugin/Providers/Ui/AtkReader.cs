using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace XivMcp.Plugin.Providers.Ui;

/// <summary>Read-only helpers for walking addon node trees. Framework thread only; never retain pointers.</summary>
internal static unsafe class AtkReader
{
    public const int MaxTextLength = 2000;

    private const int MaxComponentDepth = 12;
    private const int MaxNodesVisited = 20000;
    private const int MaxParentChain = 64;

    public readonly record struct TextItem(uint NodeId, string Path, string Text, bool Visible, float X, float Y);

    /// <summary>Finds a loaded addon by exact name, falling back to a case-insensitive scan.</summary>
    public static AtkUnitBase* FindAddon(string name, int index = 1)
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null || string.IsNullOrEmpty(name))
            return null;

        var addon = manager->GetAddonByName(name, index);
        if (addon != null)
            return addon;

        if (index != 1)
            return null;

        ref var list = ref manager->AtkUnitManager.AllLoadedUnitsList;
        var count = Math.Min((int)list.Count, list.Entries.Length);
        for (var i = 0; i < count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit != null && string.Equals(unit->NameString, name, StringComparison.OrdinalIgnoreCase))
                return unit;
        }

        return null;
    }

    /// <summary>Addon exists, is visible and has finished setting up.</summary>
    public static AtkUnitBase* FindVisibleAddon(string name)
    {
        var addon = FindAddon(name);
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }

    /// <summary>All text/counter node strings in the addon, components included, in reading order (top-to-bottom, left-to-right).</summary>
    public static List<TextItem> CollectText(AtkUnitBase* addon, bool includeHidden)
    {
        var result = new List<TextItem>();
        if (addon == null)
            return result;

        var visited = new HashSet<nint>();
        var budget = MaxNodesVisited;
        Walk(&addon->UldManager, "", addon->IsVisible, includeHidden, result, visited, ref budget, 0);

        result.Sort(static (a, b) =>
        {
            var ay = MathF.Round(a.Y);
            var by = MathF.Round(b.Y);
            return ay != by ? ay.CompareTo(by) : a.X.CompareTo(b.X);
        });
        return result;
    }

    /// <summary>Visible, non-empty, de-duplicated strings of an addon, bounded.</summary>
    public static List<string> VisibleStrings(AtkUnitBase* addon, int max, IEnumerable<string>? exclude = null)
    {
        var skip = exclude is null ? new HashSet<string>() : new HashSet<string>(exclude);
        var seen = new HashSet<string>();
        var list = new List<string>();
        foreach (var item in CollectText(addon, includeHidden: false))
        {
            if (skip.Contains(item.Text) || !seen.Add(item.Text))
                continue;
            list.Add(item.Text);
            if (list.Count >= max)
                break;
        }

        return list;
    }

    public static string ReadText(AtkTextNode* node) => node == null ? "" : ReadUtf8(&node->NodeText);

    public static string ReadUtf8(Utf8String* str)
    {
        if (str == null || str->StringPtr.Value == null || str->BufUsed <= 1)
            return "";
        return Clip(ExtractSeString(str->AsSpan()));
    }

    public static string ReadCString(byte* ptr)
    {
        if (ptr == null)
            return "";
        var length = 0;
        while (length < 8192 && ptr[length] != 0)
            length++;
        return Clip(ExtractSeString(new ReadOnlySpan<byte>(ptr, length)));
    }

    private static string ExtractSeString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return "";
        try
        {
            return ((ReadOnlySeStringSpan)bytes).ExtractText().Trim();
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes).Trim();
        }
    }

    private static string Clip(string text) => text.Length > MaxTextLength ? text[..MaxTextLength] + "…" : text;

    private static void Walk(AtkUldManager* uld, string prefix, bool parentVisible, bool includeHidden, List<TextItem> result, HashSet<nint> visited, ref int budget, int depth)
    {
        if (uld == null || uld->NodeList == null || depth > MaxComponentDepth)
            return;

        var count = (int)uld->NodeListCount;
        for (var i = 0; i < count; i++)
        {
            if (--budget < 0)
                return;

            var node = uld->NodeList[i];
            if (node == null || !visited.Add((nint)node))
                continue;

            var visible = parentVisible && IsChainVisible(node);
            if (!visible && !includeHidden)
                continue;

            var type = (ushort)node->Type;
            if (node->Type == NodeType.Text)
            {
                AddText(result, node, prefix, ReadUtf8(&((AtkTextNode*)node)->NodeText), visible);
            }
            else if (node->Type == NodeType.Counter)
            {
                AddText(result, node, prefix, ReadUtf8(&((AtkCounterNode*)node)->NodeText), visible);
            }
            else if (type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    Walk(&component->UldManager, $"{prefix}{node->NodeId}/", visible, includeHidden, result, visited, ref budget, depth + 1);
            }
        }
    }

    private static void AddText(List<TextItem> result, AtkResNode* node, string prefix, string text, bool visible)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        result.Add(new TextItem(node->NodeId, $"{prefix}{node->NodeId}", text, visible, node->ScreenX, node->ScreenY));
    }

    private static bool IsChainVisible(AtkResNode* node)
    {
        for (var i = 0; node != null && i < MaxParentChain; i++, node = node->ParentNode)
        {
            if ((node->NodeFlags & NodeFlags.Visible) == 0)
                return false;
        }

        return true;
    }
}

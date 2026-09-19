using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XivMcp.Plugin.Windows;

/// <summary>Where the game's Duty List (_ToDoList) is on screen and how its text looks. Read-only snapshot.</summary>
public readonly record struct DutyListLayout(
    bool Visible,
    Vector2 TopLeft,
    float Width,
    float ContentBottom,
    float Scale,
    bool RightAligned,
    Vector4? TitleColor,
    Vector4? TitleEdge,
    Vector4? TextColor,
    Vector4? TextEdge);

/// <summary>
/// Reads the Duty List addon's position, scale, content height and text colours so the objectives overlay can sit just
/// below it and match it. It only reads node fields (no native calls, no hooks, nothing attached to the addon), so a
/// rebuilt or closed addon simply yields a new or invisible layout on the next frame. Framework/draw thread only.
/// </summary>
public static unsafe class DutyListAnchor
{
    public const string AddonName = "_ToDoList";
    private const int MaxNodes = 2048;

    public static DutyListLayout Read(IGameGui gameGui)
    {
        var addon = gameGui.GetAddonByName<AddonToDoList>(AddonName);
        if (addon == null)
            return default;
        var unit = &addon->AtkUnitBase;
        var root = unit->RootNode;
        if (root == null || !unit->IsVisible || !Visible(root))
            return default;

        var scale = unit->Scale is > 0.1f and < 10f ? unit->Scale : 1f;
        var topLeft = new Vector2(unit->X, unit->Y);
        var width = root->Width * scale;
        var bottom = topLeft.Y;
        var styles = new List<(float Y, uint Color, uint Edge)>();
        var budget = MaxNodes;
        Walk(&unit->UldManager, 0, ref bottom, styles, ref budget, scale);

        styles.Sort((a, b) => a.Y.CompareTo(b.Y));
        var distinct = styles.Select(s => (s.Color, s.Edge)).Distinct().Take(2).ToArray();
        Vector4? titleColor = distinct.Length > 0 ? Unpack(distinct[0].Color) : null;
        Vector4? titleEdge = distinct.Length > 0 ? Unpack(distinct[0].Edge) : null;
        Vector4? textColor = distinct.Length > 1 ? Unpack(distinct[1].Color) : titleColor;
        Vector4? textEdge = distinct.Length > 1 ? Unpack(distinct[1].Edge) : titleEdge;

        return new DutyListLayout(true, topLeft, width, bottom, scale, addon->RightAligned, titleColor, titleEdge, textColor, textEdge);
    }

    private static void Walk(AtkUldManager* uld, int depth, ref float bottom, List<(float, uint, uint)> styles, ref int budget, float scale)
    {
        if (uld == null || uld->NodeList == null || depth > 4)
            return;
        for (var i = 0; i < uld->NodeListCount && budget > 0; i++, budget--)
        {
            var node = uld->NodeList[i];
            if (node == null || !Visible(node))
                continue;

            if (node->Type == NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                if (text->NodeText.StringLength <= 0)
                    continue;
                bottom = Math.Max(bottom, node->ScreenY + (node->Height * scale));
                if (styles.Count < 64)
                    styles.Add((node->ScreenY, Pack(text->TextColor.R, text->TextColor.G, text->TextColor.B, text->TextColor.A), Pack(text->EdgeColor.R, text->EdgeColor.G, text->EdgeColor.B, text->EdgeColor.A)));
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    Walk(&component->UldManager, depth + 1, ref bottom, styles, ref budget, scale);
            }
        }
    }

    /// <summary>The node and every ancestor have the Visible flag.</summary>
    private static bool Visible(AtkResNode* node)
    {
        for (var i = 0; node != null && i < 32; i++, node = node->ParentNode)
        {
            if ((node->NodeFlags & NodeFlags.Visible) == 0)
                return false;
        }

        return true;
    }

    private static uint Pack(byte r, byte g, byte b, byte a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));

    private static Vector4 Unpack(uint c) => new((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1f);
}

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Ui;

/// <summary>Read-only inspection of the game's UI windows ("addons").</summary>
[McpProvider("ui")]
public sealed unsafe class AddonProvider
{
    private const int MaxMenuOptions = 64;
    private const int MaxDialogueTexts = 40;

    [McpTool("list_addons",
        Title = "List game UI windows",
        Description =
            "Lists the game's loaded UI windows (\"addons\") with their internal names, which get_addon_text needs. " +
            "Each entry: name (internal, e.g. Inventory, Character, _PartyList, Talk, SelectString), id, visible, ready, focused, x/y (screen pixels, top-left), width/height (scaled pixels), scale, depthLayer (higher draws on top), parentId (for child windows). " +
            "By default only visible addons are returned, sorted by name. Names starting with '_' are HUD elements. Returns total matches and truncated when more exist than limit.",
        Permission = ToolPermission.Read)]
    public AddonListResult ListAddons(
        [McpParam("Only addons currently shown on screen.")] bool visibleOnly = true,
        [McpParam("Case-insensitive substring filter on the addon name.")] string? nameContains = null,
        [McpParam("Maximum entries to return.", Minimum = 1, Maximum = 500)] int limit = 100,
        [McpParam("Entries to skip (for paging).", Minimum = 0)] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
            throw new McpToolException("The game UI is not initialised yet.");

        var focused = manager->AtkUnitManager.FocusedAddon;
        ref var list = ref manager->AtkUnitManager.AllLoadedUnitsList;
        var count = Math.Min((int)list.Count, list.Entries.Length);
        var all = new List<AddonInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var addon = list.Entries[i].Value;
            if (addon == null)
                continue;
            var visible = addon->IsVisible;
            if (visibleOnly && !visible)
                continue;
            var name = addon->NameString;
            if (string.IsNullOrEmpty(name))
                continue;
            if (!string.IsNullOrEmpty(nameContains) && name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var root = addon->RootNode;
            var scale = addon->Scale;
            all.Add(new AddonInfo(
                name,
                addon->Id,
                visible,
                addon->IsReady,
                addon == focused ? true : null,
                addon->X,
                addon->Y,
                root != null ? (int)MathF.Round(root->Width * scale) : 0,
                root != null ? (int)MathF.Round(root->Height * scale) : 0,
                MathF.Round(scale, 3),
                (int)addon->DepthLayer,
                addon->ParentId != 0 ? addon->ParentId : null));
        }

        all.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var page = all.Skip(offset).Take(limit).ToList();
        return new AddonListResult(page, all.Count, offset + page.Count < all.Count);
    }

    [McpTool("get_addon_text",
        Title = "Read a UI window's text",
        Description =
            "Reads every text string shown in one game UI window (addon), including text inside nested components such as lists, buttons and tabs, in reading order (top-to-bottom, left-to-right by screen position). " +
            "Use it to read any open window: quest details, item tooltips (ItemDetail), retainer lists, duty finder, shop windows, etc. Get the addon name from list_addons (exact internal name; case-insensitive fallback). " +
            "Each entry: nodeId, path (component node ids joined with '/', ending in the text node id), text (plain, icons/macros flattened), visible, x/y (screen position). " +
            "Hidden nodes (e.g. collapsed sections, unused list rows) are skipped unless includeHidden=true. Read-only: this never clicks or changes anything.",
        Permission = ToolPermission.Read)]
    public AddonTextResult GetAddonText(
        [McpParam("Internal addon name from list_addons, e.g. \"JournalDetail\" or \"ItemDetail\".")] string addonName,
        [McpParam("Also return text from hidden nodes.")] bool includeHidden = false,
        [McpParam("Maximum text entries to return.", Minimum = 1, Maximum = 500)] int limit = 200,
        [McpParam("1-based instance when several addons share the name (rare).", Minimum = 1)] int index = 1)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            throw new McpToolException("addonName is required. Use list_addons to find it.");
        limit = Math.Clamp(limit, 1, 500);

        var addon = AtkReader.FindAddon(addonName.Trim(), Math.Max(1, index));
        if (addon == null)
            throw new McpToolException($"Addon '{addonName}' is not loaded. Use list_addons (visibleOnly=false) to see loaded addon names.");

        var items = AtkReader.CollectText(addon, includeHidden);
        var page = items.Take(limit)
            .Select(t => new AddonTextNode(t.NodeId, t.Path, t.Text, t.Visible, (int)MathF.Round(t.X), (int)MathF.Round(t.Y)))
            .ToList();

        return new AddonTextResult(addon->NameString, addon->Id, addon->IsVisible, addon->IsReady, page, items.Count, items.Count > page.Count);
    }

    [McpTool("get_dialogue",
        Title = "Read open dialogue and prompts",
        Description =
            "Returns whatever conversation or prompt windows are currently visible, read-only: " +
            "talk (NPC speaker + dialogue text of the current Talk box), subtitle (cutscene subtitle), " +
            "selectString / selectIconString (the option list of an NPC menu, plus other visible text such as the question), cutSceneSelectString (cutscene choice), " +
            "selectYesno (confirmation prompt text and its button labels, Yes first), selectOk (notice text and its OK label), " +
            "journalAccept (quest offer: title and visible text), journalResult (quest completion window), request (item hand-in window) and contextMenu (right-click menu entries). " +
            "Fields are omitted when that window is not open; anyOpen=false means nothing is showing. It never answers, clicks or advances dialogue.",
        Permission = ToolPermission.Read)]
    public DialogueResult GetDialogue()
    {
        TalkInfo? talk = null;
        var talkAddon = (AddonTalk*)AtkReader.FindVisibleAddon("Talk");
        if (talkAddon != null)
        {
            var speaker = AtkReader.ReadText(talkAddon->AtkTextNode220);
            var text = AtkReader.ReadText(talkAddon->AtkTextNode228);
            if (speaker.Length > 0 || text.Length > 0)
                talk = new TalkInfo(speaker.Length > 0 ? speaker : null, text);
        }

        string? subtitle = null;
        var subtitleAddon = (AddonTalkSubtitle*)AtkReader.FindVisibleAddon("TalkSubtitle");
        if (subtitleAddon != null)
        {
            var text = AtkReader.ReadUtf8(&subtitleAddon->SubtitleText);
            subtitle = text.Length > 0 ? text : null;
        }

        var selectString = ReadPopupMenu("SelectString");
        var selectIconString = ReadPopupMenu("SelectIconString");

        MenuInfo? cutSceneSelect = null;
        var cutSceneAddon = AtkReader.FindVisibleAddon("CutSceneSelectString");
        if (cutSceneAddon != null)
            cutSceneSelect = new MenuInfo(null, AtkReader.VisibleStrings(cutSceneAddon, MaxDialogueTexts));

        PromptInfo? yesno = null;
        var yesnoAddon = (AddonSelectYesno*)AtkReader.FindVisibleAddon("SelectYesno");
        if (yesnoAddon != null)
        {
            yesno = new PromptInfo(
                AtkReader.ReadText(yesnoAddon->PromptText),
                Labels(ButtonLabel(yesnoAddon->YesButton), ButtonLabel(yesnoAddon->NoButton)));
        }

        PromptInfo? selectOk = null;
        var okAddon = (AddonSelectOk*)AtkReader.FindVisibleAddon("SelectOk");
        if (okAddon != null)
            selectOk = new PromptInfo(AtkReader.ReadText(okAddon->PromptText), Labels(ButtonLabel(okAddon->OkButton)));

        WindowTextInfo? journalAccept = null;
        var acceptAddon = (AddonJournalAccept*)AtkReader.FindVisibleAddon("JournalAccept");
        if (acceptAddon != null)
        {
            var title = AtkReader.ReadText(acceptAddon->QuestTitleText);
            journalAccept = new WindowTextInfo(
                title.Length > 0 ? title : null,
                AtkReader.VisibleStrings(&acceptAddon->AtkUnitBase, MaxDialogueTexts, title.Length > 0 ? [title] : null));
        }

        var journalResult = ReadWindow("JournalResult");
        var request = ReadWindow("Request");
        var contextMenu = ReadWindow("ContextMenu");

        var anyOpen = talk != null || subtitle != null || selectString != null || selectIconString != null || cutSceneSelect != null ||
                      yesno != null || selectOk != null || journalAccept != null || journalResult != null || request != null || contextMenu != null;

        return new DialogueResult(anyOpen, talk, subtitle, selectString, selectIconString, cutSceneSelect, yesno, selectOk, journalAccept, journalResult, request, contextMenu);
    }

    private static MenuInfo? ReadPopupMenu(string addonName)
    {
        var addon = AtkReader.FindVisibleAddon(addonName);
        if (addon == null)
            return null;

        // AddonSelectString and AddonSelectIconString share the layout: AtkUnitBase followed by PopupMenuDerive.
        var menu = addonName == "SelectString"
            ? &((AddonSelectString*)addon)->PopupMenu.PopupMenu
            : &((AddonSelectIconString*)addon)->PopupMenu.PopupMenu;

        var options = new List<string>();
        if (menu->EntryNames != null)
        {
            var n = Math.Clamp(menu->EntryCount, 0, MaxMenuOptions);
            for (var i = 0; i < n; i++)
                options.Add(AtkReader.ReadCString(menu->EntryNames[i].Value));
        }

        var texts = AtkReader.VisibleStrings(addon, MaxDialogueTexts, options);
        return new MenuInfo(options, texts);
    }

    private static WindowTextInfo? ReadWindow(string addonName)
    {
        var addon = AtkReader.FindVisibleAddon(addonName);
        return addon == null ? null : new WindowTextInfo(null, AtkReader.VisibleStrings(addon, MaxDialogueTexts));
    }

    private static IReadOnlyList<string>? Labels(params string?[] labels)
    {
        var list = labels.Where(l => !string.IsNullOrEmpty(l)).Select(l => l!).ToList();
        return list.Count > 0 ? list : null;
    }

    private static string? ButtonLabel(AtkComponentButton* button)
    {
        if (button == null)
            return null;
        var text = AtkReader.ReadText(button->ButtonTextNode);
        return text.Length > 0 ? text : null;
    }

    public sealed record AddonInfo(
        string Name,
        int Id,
        bool Visible,
        bool Ready,
        bool? Focused,
        int X,
        int Y,
        int Width,
        int Height,
        float Scale,
        int DepthLayer,
        int? ParentId);

    public sealed record AddonListResult(IReadOnlyList<AddonInfo> Addons, int Total, bool Truncated);

    public sealed record AddonTextNode(uint NodeId, string Path, string Text, bool Visible, int X, int Y);

    public sealed record AddonTextResult(string Addon, int Id, bool Visible, bool Ready, IReadOnlyList<AddonTextNode> Texts, int Total, bool Truncated);

    public sealed record TalkInfo(string? Speaker, string Text);

    /// <summary>Options is null when the option list could not be read structurally (texts then holds everything visible).</summary>
    public sealed record MenuInfo(IReadOnlyList<string>? Options, IReadOnlyList<string> Texts);

    /// <summary>Buttons are the visible button labels in on-screen order (e.g. ["Yes", "No"]).</summary>
    public sealed record PromptInfo(string Prompt, IReadOnlyList<string>? Buttons);

    public sealed record WindowTextInfo(string? Title, IReadOnlyList<string> Texts);

    public sealed record DialogueResult(
        bool AnyOpen,
        TalkInfo? Talk,
        string? Subtitle,
        MenuInfo? SelectString,
        MenuInfo? SelectIconString,
        MenuInfo? CutSceneSelectString,
        PromptInfo? SelectYesno,
        PromptInfo? SelectOk,
        WindowTextInfo? JournalAccept,
        WindowTextInfo? JournalResult,
        WindowTextInfo? Request,
        WindowTextInfo? ContextMenu);
}

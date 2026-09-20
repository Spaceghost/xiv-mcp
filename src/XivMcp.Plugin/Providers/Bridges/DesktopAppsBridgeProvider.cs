using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Bridges;

namespace XivMcp.Plugin.Providers.Bridges;

/// <summary>
/// Typed tools over XivDesktop (the host desktop's applications, windows and workspaces, and its "ask an NPC" assistant).
/// The raw desktop_query and desktop_command stay. XivDesktop marshals onto the framework thread itself; these tools run
/// there already (the default), so its two-second wait never applies.
/// </summary>
[McpProvider("bridges")]
public sealed class DesktopAppsBridgeProvider
{
    private readonly DesktopBridge bridge;

    public DesktopAppsBridgeProvider(IDalamudPluginInterface pluginInterface, IPluginLog log) =>
        bridge = new DesktopBridge(new DalamudBridgeInvoker(pluginInterface, log));

    [McpTool("list_desktop_apps",
        Sources = ["ipc:" + DesktopBridge.ListAppsGate],
        Title = "List desktop applications",
        RequiresLogin = false,
        Description =
            "The applications XivDesktop can launch on the player's computer, filtered and paged here: id (the desktop-file id launch_desktop_app takes), name, " +
            "generic name, categories, favourite, terminal (runs in a terminal), launchable and recent (0 = most recently launched). query matches id, name, " +
            "generic name, keywords and categories, ignoring case. Returns total, matched and truncated. Read-only; nothing is launched. " +
            "Fails with unavailable when XivDesktop is not loaded.")]
    public DesktopAppListDto ListDesktopApps(
        [McpParam("Text to match. Omit for everything.")] string? query = null,
        [McpParam("Only the player's favourites.")] bool favouritesOnly = false,
        [McpParam("Maximum apps returned (1-500).", Minimum = 1, Maximum = 500)] int limit = 50,
        [McpParam("Apps to skip.", Minimum = 0)] int offset = 0) =>
        bridge.ListApps(query, favouritesOnly, limit, offset);

    [McpTool("list_desktop_windows",
        Sources = ["ipc:" + DesktopBridge.WindowsGate],
        Title = "List desktop windows",
        RequiresLogin = false,
        Description =
            "The desktop windows shown as panels in game, as XivDesktop tracks them: available (GhosttyDalamud's call gate is there), rev, workspace (current, " +
            "1-9), target (the window id actions without an id apply to) and windows[] with id, title, app, state (pending|live|ended), kind (pet|pin|full|tab), " +
            "focused, workspace, hidden (on another workspace) and appId. ids are the same panel ids list_terminal_panels returns. Read-only.")]
    public BridgeDataDto ListDesktopWindows() => bridge.ReadJson(DesktopBridge.WindowsGate);

    [McpTool("search_desktop_palette",
        Sources = ["ipc:" + DesktopBridge.PaletteGate],
        Title = "Search the desktop palette",
        RequiresLogin = false,
        Description =
            "XivDesktop's command-palette ranking for a query (at most 10): applications, open windows, actions, calculator results and commands, each with " +
            "provider, title, subtitle, score, enabled/reason and the command running it would perform. Read-only: nothing is run. Use it to resolve " +
            "\"open my browser\" to an app id before launch_desktop_app.")]
    public BridgeDataDto SearchDesktopPalette(
        [McpParam("What the player would type into the palette.")] string query,
        [McpParam("Maximum results (1-50; XivDesktop itself returns at most 10).", Minimum = 1, Maximum = 50)] int limit = 10) =>
        bridge.SearchPalette(query, limit);

    [McpTool("get_desktop_status",
        Sources = ["ipc:" + DesktopBridge.StatusGate, SiblingIpcProposals.DesktopApiVersion],
        Title = "Desktop bridge status",
        RequiresLogin = false,
        Description =
            "XivDesktop's health: ghostty (launching is possible), ghosttyStatus, apps (catalogue size), scanning, scannedAt, lastLaunch, lastError, summary, " +
            "plus apiVersion (null until XivDesktop offers an ApiVersion gate). Check ghostty=true before launch_desktop_app.")]
    public BridgeDataDto GetDesktopStatus() => bridge.GetStatus();

    [McpTool("launch_desktop_app",
        Sources = ["ipc:" + DesktopBridge.LaunchGate],
        Title = "Launch a desktop application",
        Permission = ToolPermission.Action,
        Idempotent = false,
        RequiresLogin = false,
        ApprovalSummary = "Launch this application on your computer: {app}",
        Description =
            "Starts an application on the player's computer through XivDesktop and shows its window as a panel in game. app is a desktop-file id from " +
            "list_desktop_apps (\"org.gnome.TextEditor\", with or without \".desktop\") or search text, in which case XivDesktop launches its BEST MATCH, so " +
            "prefer an exact id. Only catalogued applications can be started, never an arbitrary command line. Success means the launch was handed over, not " +
            "that a window appeared; check list_desktop_windows. Fails with XivDesktop's own reason (no match, GhosttyDalamud missing).")]
    public DesktopReplyDto LaunchDesktopApp(
        [McpParam("Desktop-file id (preferred) or search text; shown verbatim to the player.")] string app) =>
        bridge.Launch(app);

    [McpTool("desktop_window_action",
        Sources = ["ipc:" + DesktopBridge.WindowActionGate],
        Title = "Act on a desktop window",
        Permission = ToolPermission.Action,
        Idempotent = false,
        RequiresLogin = false,
        ApprovalSummary = "Desktop window {id}: {action} (pin: {pin}, workspace: {workspace}). \"close\" closes the application's window.",
        Description =
            "One action on a desktop window panel: focus, close (closes the window's stream and panel), pet (make it a pet), pin (pin it where the character " +
            "stands), toggle (pet ↔ pin in place), place (needs pin, as /term pin takes it: \"orbit 3.5\", \"hud 0.85 0.2\" …) or move (needs workspace 1-9). " +
            "id is from list_desktop_windows; 0 means the focused, else last focused, window. pin and workspace are refused with other actions.")]
    public DesktopReplyDto DesktopWindowAction(
        [McpParam("What to do.", Enum = ["focus", "close", "pet", "pin", "toggle", "place", "move"])] string action,
        [McpParam("Window id from list_desktop_windows; 0 = the focused (else last focused) window.", Minimum = 0)] long id = 0,
        [McpParam("Placement for action place, e.g. \"orbit 3.5\".")] string? pin = null,
        [McpParam("Workspace for action move (1-9).", Minimum = 0, Maximum = 9)] int workspace = 0) =>
        bridge.WindowAction(action, id, pin, workspace);

    [McpTool("switch_desktop_workspace",
        Sources = ["ipc:" + DesktopBridge.WorkspaceGate],
        Title = "Switch desktop workspace",
        Permission = ToolPermission.Action,
        RequiresLogin = false,
        ApprovalSummary = "Switch XivDesktop to workspace {workspace}; window panels on other workspaces are hidden.",
        Description =
            "Switches XivDesktop's current workspace (1-9): window panels assigned to other workspaces are hidden, this one's are shown. Nothing is closed. " +
            "The current workspace is in list_desktop_windows.")]
    public DesktopReplyDto SwitchDesktopWorkspace(
        [McpParam("Workspace number (1-9).", Minimum = 1, Maximum = 9)] int workspace) =>
        bridge.SwitchWorkspace(workspace);

    [McpTool("ask_npc_assistant",
        Sources = ["ipc:" + DesktopBridge.AskGate],
        Title = "Ask the NPC assistant",
        Permission = ToolPermission.Action,
        Idempotent = false,
        OpenWorld = true,
        RequiresLogin = false,
        ApprovalSummary = "Send this to XivDesktop's NPC assistant (it summons a speaker on your screen and starts a model generation): {text}",
        Description =
            "What follows XivDesktop's /ask: a question (summons the speaker if needed and asks it), \"\" (only summon), \"bye\" (dismiss), or " +
            "\"as <preset|npc:ID|minion:ID|mount:ID|pet:ID|self> [question]\" to switch speaker first. The answer streams into the in-game dialogue window " +
            "for the PLAYER; it is NOT returned here (the gate answers only ok or error). The text goes to whichever model the player configured, which " +
            "may be a remote service. Only the player sees the speaker; nothing is sent to game chat.")]
    public DesktopReplyDto AskNpcAssistant(
        [McpParam("The /ask argument, shown verbatim to the player.")] string text) =>
        bridge.Ask(text);

    [McpTool("get_desktop_panels",
        Sources = [SiblingIpcProposals.DesktopPanels],
        Title = "Desktop launcher state",
        RequiresLogin = false,
        Description =
            "What the player currently sees in XivDesktop: the launcher (open, search text, view, category, page, selected and visible app ids), favourites in " +
            "order, whether the palette is open and the assistant's state (summoned, speaker, busy). NEEDS A NEWER XivDesktop: written against the proposed " +
            "XivDesktop.v1.Panels gate; fails with unavailable, naming it, until it ships.")]
    public BridgeDataDto GetDesktopPanels() => bridge.ReadProposed(SiblingIpcProposals.DesktopPanelsGate);

    [McpTool("rescan_desktop_apps",
        Sources = [SiblingIpcProposals.DesktopRescan],
        Title = "Rescan desktop applications",
        Permission = ToolPermission.Action,
        RequiresLogin = false,
        ApprovalSummary = "Have XivDesktop rescan the applications installed on your computer.",
        Description =
            "Starts XivDesktop's background scan of installed applications so a newly installed one appears in list_desktop_apps; get_desktop_status shows " +
            "scanning and scannedAt. NEEDS A NEWER XivDesktop: written against the proposed XivDesktop.v1.Rescan gate; fails with unavailable, naming it, " +
            "until it ships.")]
    public DesktopReplyDto RescanDesktopApps() =>
        bridge.ChangeProposed(SiblingIpcProposals.DesktopRescanGate, "The scan runs in the background; get_desktop_status shows scanning and scannedAt.");
}

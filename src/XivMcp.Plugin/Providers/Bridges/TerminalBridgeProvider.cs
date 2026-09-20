using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Bridges;

namespace XivMcp.Plugin.Providers.Bridges;

/// <summary>
/// Typed tools over GhosttyDalamud (terminal and remote-window panels in the game world). The raw ghostty_query and
/// ghostty_command stay; these say what they do in their names, check their arguments and wait briefly for an outcome.
/// Nothing here types into a shell: there is no tool for /term send or /term type, by design.
/// </summary>
[McpProvider("bridges")]
public sealed class TerminalBridgeProvider
{
    private const string Call = "ipc:" + TerminalBridge.CallGate;
    private const string Post = "ipc:" + TerminalBridge.PostGate;

    private const string ChangeNote =
        " GhosttyDalamud queues the change and runs it at the end of its next frame; this waits at most 2 seconds for the outcome (completed=true) and " +
        "otherwise returns the request id for get_terminal_request. A change GhosttyDalamud ran and refused fails with its own reason.";

    private readonly TerminalBridge bridge;

    public TerminalBridgeProvider(IDalamudPluginInterface pluginInterface, IPluginLog log, IGameThread gameThread) =>
        bridge = new TerminalBridge(new DalamudBridgeInvoker(pluginInterface, log), gameThread);

    [McpTool("list_terminal_panels",
        Sources = [Call],
        Title = "List terminal panels",
        RequiresLogin = false,
        Description =
            "Every GhosttyDalamud panel, oldest first: terminals (in the dropdown, floating, minimized or in the world), remote desktop windows, adopted plugin " +
            "windows and the chat. Each has id (what every terminal panel tool takes), kind (terminal|window|adopted|chat), title, app, profile and running " +
            "(terminals), view (tab|dropdown|min|pet|pin|hud|full|hidden), anchor/state/width/height (remote windows only), focused (has the keyboard), hidden " +
            "and order (a pet's place from 1). Merged from panel.list and window.list, at most one frame old. Use get_terminal_layout for how to reproduce the " +
            "arrangement, ghostty_query for the raw JSON. Fails with unavailable when GhosttyDalamud is not loaded.")]
    public TerminalPanelListDto ListTerminalPanels(
        [McpParam("Maximum panels returned (1-500).", Minimum = 1, Maximum = 500)] int limit = 50,
        [McpParam("Panels to skip.", Minimum = 0)] int offset = 0) =>
        bridge.ListPanels(limit, offset);

    [McpTool("get_terminal_layout",
        Sources = [Call],
        Title = "Describe the terminal layout",
        RequiresLogin = false,
        Description =
            "The current arrangement of GhosttyDalamud panels as a layout description: per panel its view, pet order and, where the snapshot allows, the pin " +
            "argument that would put a panel there again (pin, for place_terminal_panel/open_terminal), with reproducible and a how sentence. This is " +
            "RECONSTRUCTED: GhosttyDalamud has no layout.get, and its snapshot omits the numbers behind a place (a pin's world position, an orbit's distance, a " +
            "HUD dock's X/Y), so only pets are exact. For saved, named layouts use get_terminal_layouts (needs a newer GhosttyDalamud).")]
    public TerminalLayoutDto GetTerminalLayout() => bridge.GetLayout();

    [McpTool("get_terminal_status",
        Sources = [Call, "ipc:" + TerminalBridge.StatusGate, SiblingIpcProposals.GhosttyApiVersion],
        Title = "Terminal bridge status",
        RequiresLogin = false,
        Description =
            "GhosttyDalamud's health in one call: status (its one-line text, e.g. \"ghostty 3\"), agent (the desktop agent that streams windows: connected, " +
            "version, windows_ok, agent address, window_lists), focus (the world panel with the keyboard, {id: 0} when none) and api (its verb list; null " +
            "until GhosttyDalamud offers api.version). Check agent.windows_ok before open_terminal with run. Fails with unavailable when it is not loaded.")]
    public TerminalStatusDto GetTerminalStatus() => bridge.GetStatus();

    [McpTool("get_terminal_request",
        Sources = [Call],
        Title = "Look up a terminal change",
        RequiresLogin = false,
        Description =
            "The outcome of a queued GhosttyDalamud change, by the request id a terminal tool returned: state done (with result, e.g. {id: panel}), failed (with " +
            "GhosttyDalamud's error text) or unknown (not run yet, or no longer among the last 16 outcomes it keeps). Only needed when a change tool came back " +
            "with completed=false.")]
    public TerminalRequestDto GetTerminalRequest(
        [McpParam("Request id as returned in a change tool's request field (digits).")] string request) =>
        bridge.GetRequest(request);

    [McpTool("open_terminal",
        Sources = [Call],
        Title = "Open a terminal panel",
        Permission = ToolPermission.Action,
        GameThread = false,
        Idempotent = false,
        ApprovalSummary = "Open a terminal panel in the game (profile: {profile}, placed: {pin}). Run this command on your computer in a new terminal: {run}",
        Description =
            "Opens a new GhosttyDalamud panel in the game world. Without run: a terminal (terminal.new) with the given profile (a name from the player's " +
            "GhosttyDalamud profiles, or its place from 1; default profile when omitted). With run: the desktop agent STARTS THAT COMMAND on the player's " +
            "computer and streams its window (window.open); a text program needs a terminal emulator in the command, e.g. \"foot htop\". run and profile " +
            "exclude each other. pin places it as /term pin does: \"pet\" (default, floats beside the character), \"here\", \"me 2 1.7\", \"target\", " +
            "\"orbit 3.5\", \"hud 0.85 0.2\". Returns request, completed and panelId (the new panel's id) when the outcome arrived. Common failures: " +
            "\"no player (log in first)\", \"the agent is not connected\", \"no such profile\". run is shown to the player verbatim for approval; there is " +
            "deliberately no tool that types text into an open shell." + ChangeNote)]
    public Task<TerminalChangeDto> OpenTerminal(
        [McpParam("Command the desktop agent starts, shown verbatim to the player. Omit for a plain terminal.")] string? run = null,
        [McpParam("Terminal profile name or place from 1. Not with run.")] string? profile = null,
        [McpParam("Placement as /term pin takes it, e.g. \"pet\", \"here\", \"orbit 3.5\", \"hud 0.85 0.2\". Default: a pet.")] string? pin = null,
        CancellationToken ct = default)
    {
        var parameters = TerminalBridge.OpenParameters(run, profile, pin, out var method);
        return bridge.ChangeAsync(method, parameters, ct);
    }

    [McpTool("place_terminal_panel",
        Sources = [Call],
        Title = "Move a terminal panel",
        Permission = ToolPermission.Action,
        GameThread = false,
        ApprovalSummary = "Move GhosttyDalamud panel {id} to: {pin}",
        Description =
            "Moves a panel (panel.place): pin is \"pet\", \"here\" (fixed where the character stands), \"me 2 1.7\", \"target\", \"orbit 3.5\", " +
            "\"hud [X Y [DIST]]\" (docked to the screen, X/Y as 0..1 fractions from the top left), \"hide\" or \"hide off\". A terminal in the dropdown or " +
            "minimized moves into the world; windows keep their size. id comes from list_terminal_panels." + ChangeNote)]
    public Task<TerminalChangeDto> PlaceTerminalPanel(
        [McpParam("Panel id from list_terminal_panels.", Minimum = 1)] long id,
        [McpParam("Placement, e.g. \"pet\", \"here\", \"orbit 3.5\", \"hud 0.85 0.2\".")] string pin,
        CancellationToken ct = default) =>
        bridge.ChangeAsync("panel.place", new JsonObject { ["id"] = TerminalBridge.CheckId(id), ["pin"] = TerminalBridge.CheckPin(pin) }, ct);

    [McpTool("focus_terminal_panel",
        Sources = [Call],
        Title = "Focus a terminal panel",
        Permission = ToolPermission.Action,
        GameThread = false,
        ApprovalSummary = "Give GhosttyDalamud panel {id} the keyboard and bring it forward.",
        Description =
            "Gives a panel the keyboard and brings it forward (panel.focus): a hidden panel is shown, a dropdown tab opens the dropdown, a minimized terminal " +
            "is restored. Always sent with fly=false, so the character is NOT walked up to the panel and the camera is not turned (XivMcp does not move the " +
            "character). The game stops receiving keys until the player presses Esc twice or clicks the world." + ChangeNote)]
    public Task<TerminalChangeDto> FocusTerminalPanel(
        [McpParam("Panel id from list_terminal_panels.", Minimum = 1)] long id,
        CancellationToken ct = default) =>
        bridge.ChangeAsync("panel.focus", new JsonObject { ["id"] = TerminalBridge.CheckId(id), ["fly"] = false }, ct);

    [McpTool("close_terminal_panel",
        Sources = [Call],
        Title = "Close a terminal panel",
        Permission = ToolPermission.Action,
        GameThread = false,
        Destructive = true,
        ApprovalSummary = "Close GhosttyDalamud panel {id}. A terminal's shell, and anything running in it, ends with it.",
        Description =
            "Closes a panel as its close button does (panel.close): a terminal's shell and every program in it END, a remote window's stream stops, an " +
            "adopted plugin window goes back to its plugin and the chat back to the game. Not undoable for terminals. To get a panel out of the way " +
            "without losing it use set_terminal_panel_hidden." + ChangeNote)]
    public Task<TerminalChangeDto> CloseTerminalPanel(
        [McpParam("Panel id from list_terminal_panels.", Minimum = 1)] long id,
        CancellationToken ct = default) =>
        bridge.ChangeAsync("panel.close", new JsonObject { ["id"] = TerminalBridge.CheckId(id) }, ct);

    [McpTool("set_terminal_panel_hidden",
        Sources = [Call],
        Title = "Hide or show a terminal panel",
        Permission = ToolPermission.Action,
        GameThread = false,
        ApprovalSummary = "Set GhosttyDalamud world panel {id} hidden: {hidden}",
        Description =
            "Hides or shows a world panel (window.hide): hidden panels are neither drawn nor streamed, lose the keyboard and sleep; hidden=false shows it " +
            "again where its anchor puts it. Nothing is closed. Only world panels (pet, pin, hud): a terminal in the dropdown is refused by GhosttyDalamud." +
            ChangeNote)]
    public Task<TerminalChangeDto> SetTerminalPanelHidden(
        [McpParam("Panel id from list_terminal_panels.", Minimum = 1)] long id,
        [McpParam("true hides, false shows.")] bool hidden,
        CancellationToken ct = default) =>
        bridge.ChangeAsync("window.hide", new JsonObject { ["id"] = TerminalBridge.CheckId(id), ["hidden"] = hidden }, ct);

    [McpTool("order_terminal_panel",
        Sources = [Call],
        Title = "Reorder a pet panel",
        Permission = ToolPermission.Action,
        GameThread = false,
        ApprovalSummary = "Move GhosttyDalamud pet panel {id} in the lineup to: {to}",
        Description =
            "Moves a pet panel within the lineup beside the character (panel.order): to is \"left\" or \"right\" (swap with that neighbour), \"first\", " +
            "\"last\" or a place from 1. Only shown pets have a place; anything else fails with \"only pets have a place in the order\"." + ChangeNote)]
    public Task<TerminalChangeDto> OrderTerminalPanel(
        [McpParam("Panel id from list_terminal_panels.", Minimum = 1)] long id,
        [McpParam("left, right, first, last or a place from 1.")] string to,
        CancellationToken ct = default) =>
        bridge.ChangeAsync("panel.order", TerminalBridge.OrderParameters(id, to), ct);

    [McpTool("capture_terminal_screenshot",
        Sources = [Post, SiblingIpcProposals.GhosttyCaptureShot],
        Title = "Screenshot through GhosttyDalamud",
        Permission = ToolPermission.Action,
        GameThread = false,
        Idempotent = false,
        ApprovalSummary = "Have GhosttyDalamud save a screenshot to its capture folder (target: {target}, game UI hidden: {clean}).",
        Description =
            "Asks GhosttyDalamud for a PNG of the frame the game just drew, including its world panels: target full (the whole frame) or panel (just the " +
            "focused terminal's window); clean=true hides the game's own UI for that frame. It writes a file on the player's computer, hence Action tier. " +
            "Today this is posted as the chat command \"/term shot …\", which returns nothing: the result says via=post and completion CANNOT be confirmed " +
            "until GhosttyDalamud adds a capture.shot IPC verb (then via=call with path, width, height). To look at the screen yourself use take_screenshot " +
            "and get_latest_screenshot, which return the image.")]
    public Task<TerminalChangeDto> CaptureTerminalScreenshot(
        [McpParam("What to capture.", Enum = ["full", "panel"])] string target = "full",
        [McpParam("Hide the game's own UI for the capture.")] bool clean = false,
        CancellationToken ct = default)
    {
        var what = NormalizeTarget(target);
        return bridge.ChangeOrPostAsync(
            "capture.shot",
            new JsonObject { ["target"] = what, ["clean"] = clean },
            TerminalBridge.BuildPost("shot", what == "panel" ? "panel" : null, clean ? "clean" : null),
            "The PNG lands in GhosttyDalamud's own capture folder on the player's computer; use take_screenshot / get_latest_screenshot to see the screen from here.",
            ct);
    }

    [McpTool("capture_terminal_clip",
        Sources = [Post, SiblingIpcProposals.GhosttyCaptureClip],
        Title = "Record a clip through GhosttyDalamud",
        Permission = ToolPermission.Action,
        GameThread = false,
        Idempotent = false,
        ApprovalSummary = "Have GhosttyDalamud record {seconds} seconds of the game as {format} into its capture folder (focused panel only: {panel}, game UI hidden: {clean}).",
        Description =
            "Asks GhosttyDalamud to record a short clip (1-30 seconds, gif or mp4) of the game frame, or of the focused panel only, optionally with the game's " +
            "own UI hidden. Writes a file on the player's computer. Posted today as \"/term clip …\" with no result (via=post): completion cannot be confirmed " +
            "until GhosttyDalamud adds capture.clip; once it does, get_terminal_capture_status shows the finished file.")]
    public Task<TerminalChangeDto> CaptureTerminalClip(
        [McpParam("Length in seconds (1-30).", Minimum = 1, Maximum = 30)] int seconds = 6,
        [McpParam("File format.", Enum = ["gif", "mp4"])] string format = "gif",
        [McpParam("Record only the focused panel.")] bool panel = false,
        [McpParam("Hide the game's own UI while recording.")] bool clean = false,
        CancellationToken ct = default)
    {
        if (seconds is < 1 or > 30)
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "seconds is 1 to 30.");
        }

        var kind = (format ?? "").Trim().ToLowerInvariant();
        if (kind is not ("gif" or "mp4"))
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "format is gif or mp4.");
        }

        return bridge.ChangeOrPostAsync(
            "capture.clip",
            new JsonObject { ["seconds"] = seconds, ["format"] = kind, ["panel"] = panel, ["clean"] = clean },
            TerminalBridge.BuildPost("clip", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), kind, panel ? "panel" : null, clean ? "clean" : null),
            "The clip lands in GhosttyDalamud's own capture folder once the recording ends.",
            ct);
    }

    [McpTool("set_terminal_theme",
        Sources = [Post, SiblingIpcProposals.GhosttyThemeSet],
        Title = "Switch the terminal theme",
        Permission = ToolPermission.Action,
        GameThread = false,
        RequiresLogin = false,
        ApprovalSummary = "Switch GhosttyDalamud's colour theme to: {name}",
        Description =
            "Switches the colours of the terminals and the glass around them to a named theme and saves it, as \"/term theme NAME\" does (names may contain " +
            "spaces, e.g. \"Gruvbox Light\"). Posted as that chat command today: a misspelt name fails silently in the game's chat log and the result says " +
            "via=post. With a GhosttyDalamud that offers theme.set the outcome is reported; list_terminal_themes lists the names once theme.list exists.")]
    public Task<TerminalChangeDto> SetTerminalTheme(
        [McpParam("Theme name as GhosttyDalamud's settings list it.")] string name,
        CancellationToken ct = default)
    {
        var theme = BridgeJson.CheckArgument("name", name, maxBytes: 128);
        return bridge.ChangeOrPostAsync("theme.set", new JsonObject { ["name"] = theme }, TerminalBridge.BuildPost("theme", theme),
            "A name GhosttyDalamud does not know only prints a message in the game's chat log.", ct);
    }

    [McpTool("run_terminal_selftest",
        Sources = [Post, SiblingIpcProposals.GhosttySelftestRun],
        Title = "Run GhosttyDalamud's self-test",
        Permission = ToolPermission.Action,
        GameThread = false,
        Idempotent = false,
        ApprovalSummary = "Run GhosttyDalamud's in-game self-test (suites: {suites}). Test panels may flash on screen while it runs.",
        Description =
            "Starts GhosttyDalamud's own in-game checks (\"/term selftest SUITES\"): suites is \"all\", \"list\" or suite names separated by spaces. It opens " +
            "and closes test panels and writes a JSON report into GhosttyDalamud's config folder (selftest/*.json), which its CI reads from disk. Posted as " +
            "the chat command today, so nothing comes back here (via=post); get_terminal_selftest_report returns the report once GhosttyDalamud offers " +
            "selftest.report. \"leakwatch\" is refused: it toggles a setting, it is not a suite.")]
    public Task<TerminalChangeDto> RunTerminalSelftest(
        [McpParam("\"all\", \"list\" or suite names separated by spaces.")] string suites = "all",
        CancellationToken ct = default)
    {
        var names = TerminalBridge.CheckSuites(suites);
        return bridge.ChangeOrPostAsync(
            "selftest.run",
            new JsonObject { ["suites"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) },
            TerminalBridge.BuildPost("selftest", [.. names]),
            "The report is written to selftest/*.json in GhosttyDalamud's config folder on the player's computer.",
            ct);
    }

    [McpTool("get_terminal_layouts",
        Sources = [SiblingIpcProposals.GhosttyLayoutGet],
        Title = "Saved terminal layouts",
        RequiresLogin = false,
        Description =
            "GhosttyDalamud's saved, named layouts (panels with kind, profile or run/match, view, exact pin arguments, order, size) and which is current; name " +
            "narrows to one. NEEDS A NEWER GhosttyDalamud: it reads the proposed layout.get verb and fails with unavailable, naming that verb, until " +
            "GhosttyDalamud ships it. Until then get_terminal_layout reconstructs the current arrangement approximately.")]
    public BridgeDataDto GetTerminalLayouts(
        [McpParam("Only this layout. Omit for all.")] string? name = null)
    {
        var layout = BridgeJson.CheckArgument("name", name, allowEmpty: true, maxBytes: 128);
        return bridge.ReadProposed("layout.get", layout.Length == 0 ? null : new JsonObject { ["name"] = layout });
    }

    [McpTool("apply_terminal_layout",
        Sources = [SiblingIpcProposals.GhosttyLayoutSet],
        Title = "Apply a saved terminal layout",
        Permission = ToolPermission.Action,
        GameThread = false,
        Idempotent = false,
        ApprovalSummary = "Apply GhosttyDalamud's saved layout \"{name}\": panels move, and missing ones (terminals or programs the layout names) are opened.",
        Description =
            "Applies a saved layout by name: existing panels move to their saved places and missing ones are opened (which may start the programs the layout " +
            "names); nothing is closed. NEEDS A NEWER GhosttyDalamud: written against the proposed layout.set verb, it fails with unavailable, naming the " +
            "verb, until that ships. Read the layout first with get_terminal_layouts." + ChangeNote)]
    public Task<TerminalChangeDto> ApplyTerminalLayout(
        [McpParam("Layout name from get_terminal_layouts.")] string name,
        CancellationToken ct = default) =>
        bridge.ChangeAsync("layout.set", new JsonObject { ["name"] = BridgeJson.CheckArgument("name", name, maxBytes: 128), ["apply"] = true }, ct);

    [McpTool("list_terminal_themes",
        Sources = [SiblingIpcProposals.GhosttyThemeList],
        Title = "List terminal themes",
        RequiresLogin = false,
        Description =
            "The theme names set_terminal_theme accepts and the current one. NEEDS A NEWER GhosttyDalamud: today themes are listed only into the game's chat " +
            "log by \"/term theme\", which IPC cannot read, so this fails with unavailable (naming the proposed theme.list verb) until GhosttyDalamud ships it.")]
    public BridgeDataDto ListTerminalThemes() => bridge.ReadProposed("theme.list");

    [McpTool("get_terminal_capture_status",
        Sources = [SiblingIpcProposals.GhosttyCaptureStatus],
        Title = "Terminal capture status",
        RequiresLogin = false,
        Description =
            "Whether GhosttyDalamud is recording, and its last screenshot or clip (kind, path, size, time, error). This is how a capture_terminal_* call is " +
            "confirmed. NEEDS A NEWER GhosttyDalamud: fails with unavailable, naming the proposed capture.status verb, until it ships.")]
    public BridgeDataDto GetTerminalCaptureStatus() => bridge.ReadProposed("capture.status");

    [McpTool("get_terminal_selftest_report",
        Sources = [SiblingIpcProposals.GhosttySelftestReport],
        Title = "Terminal self-test report",
        RequiresLogin = false,
        Description =
            "GhosttyDalamud's last self-test report (build stamp, per-suite pass/fail/skip counts, failed cases) and whether a run is in progress. NEEDS A " +
            "NEWER GhosttyDalamud: the report is a file on the player's disk today; this fails with unavailable, naming the proposed selftest.report verb, " +
            "until it ships.")]
    public BridgeDataDto GetTerminalSelftestReport() => bridge.ReadProposed("selftest.report");

    [McpTool("get_terminal_leakwatch_report",
        Sources = [SiblingIpcProposals.GhosttyLeakwatchReport],
        Title = "Terminal leak-watch report",
        RequiresLogin = false,
        Description =
            "GhosttyDalamud's leak watch: memory samples per core load and the growth per load. NEEDS A NEWER GhosttyDalamud: today this goes to the plugin " +
            "log only; fails with unavailable, naming the proposed leakwatch.report verb, until it ships.")]
    public BridgeDataDto GetTerminalLeakwatchReport() => bridge.ReadProposed("leakwatch.report");

    [McpTool("share_terminal_capture",
        Sources = [SiblingIpcProposals.GhosttyGalleryShare],
        Title = "Upload a capture to the gallery",
        Permission = ToolPermission.Action,
        GameThread = false,
        Idempotent = false,
        OpenWorld = true,
        ApprovalSummary = "UPLOAD a capture from your computer to GhosttyDalamud's online gallery, where anyone with the link can see it: {path}",
        Description =
            "Uploads one screenshot or clip from GhosttyDalamud's capture folder to its online gallery and returns the link (result.url). path is a file " +
            "path from get_terminal_capture_status, or \"last\" for the most recent capture. This sends an image of the player's screen to a server, so the " +
            "player is shown exactly what is uploaded. NEEDS A NEWER GhosttyDalamud: written against the proposed gallery.share verb; fails with " +
            "unavailable until it ships (\"/term share\" is deliberately not posted blind, since nothing could report what was uploaded)." + ChangeNote)]
    public Task<TerminalChangeDto> ShareTerminalCapture(
        [McpParam("Capture file path, or \"last\" for the most recent capture.")] string path = "last",
        CancellationToken ct = default)
    {
        var file = BridgeJson.CheckArgument("path", path);
        var parameters = file.Equals("last", StringComparison.OrdinalIgnoreCase) ? new JsonObject { ["last"] = true } : new JsonObject { ["path"] = file };
        return bridge.ChangeAsync("gallery.share", parameters, ct);
    }

    private static string NormalizeTarget(string? target)
    {
        var what = (target ?? "").Trim().ToLowerInvariant();
        return what is "full" or "panel" ? what : throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "target is full or panel.");
    }
}

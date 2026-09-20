namespace XivMcp.Plugin.Bridges;

/// <summary>Whether a proposed gate or verb only reads, or changes something on the owner's machine.</summary>
public enum SiblingIpcKind
{
    /// <summary>Answers from existing state; safe to call without approval.</summary>
    Read,

    /// <summary>Changes state (screen, files, settings, a running job, an upload). XivMcp only calls it from an approved Action tool.</summary>
    Change,
}

/// <summary>
/// One piece of IPC a sibling plugin does not offer yet, specified precisely enough to implement from this row alone.
/// </summary>
/// <param name="Sibling">Bridge key from <see cref="BridgeCatalog"/>: ghostty, xivdesktop or almanac.</param>
/// <param name="Gate">Dalamud call gate name.</param>
/// <param name="Verb">Method name inside a JSON call gate; null when the gate itself is the proposal.</param>
/// <param name="Signature">The gate's delegate shape as the provider registers it.</param>
/// <param name="Kind">Read or change.</param>
/// <param name="Request">JSON request (the <c>params</c> object for a verb, the argument for a discrete gate).</param>
/// <param name="Response">JSON response (the envelope's <c>result</c> for a verb).</param>
/// <param name="Threading">What the caller may assume about threads and blocking.</param>
/// <param name="Why">What an agent can do once it exists.</param>
/// <param name="Tools">XivMcp tools already written against it; they answer "unavailable" until it ships.</param>
public sealed record SiblingIpcProposal(
    string Sibling,
    string Gate,
    string? Verb,
    string Signature,
    SiblingIpcKind Kind,
    string Request,
    string Response,
    string Threading,
    string Why,
    IReadOnlyList<string> Tools)
{
    /// <summary>The label the tools carry in <c>Sources</c>: <c>ipc:Gate</c> or <c>ipc:Gate#verb</c>.</summary>
    public string Source => Verb is null ? $"ipc:{Gate}" : $"ipc:{Gate}#{Verb}";

    /// <summary>The capability name used in error messages: <c>Gate</c> or <c>Gate#verb</c>.</summary>
    public string Capability => Verb is null ? Gate : $"{Gate}#{Verb}";
}

/// <summary>
/// IPC the owner's other plugins (GhosttyDalamud, XivDesktop, Almanac) would need to add for the tools named in each
/// row to work. This is a request list, not a description of what exists: none of these gates or verbs was present when
/// this file was written. XivMcp never guesses from a version number; each tool simply calls, and a missing gate
/// ("not registered") or a JSON gate answering <c>{"ok":false,"error":"unknown method: NAME"}</c> turns into an
/// <c>unavailable</c> tool error that names the row. When a sibling ships a row, the tool starts working with no change here.
///
/// Conventions every row follows, taken from GhosttyDalamud's existing Call gate (docs/IPC.md there), which is the
/// family precedent:
/// <list type="bullet">
/// <item>JSON call gates are <c>Func&lt;string, string&gt;</c>: request <c>{"method", "params", "caller"}</c>, response
/// <c>{"ok":true,"result":…}</c> or <c>{"ok":false,"error":"text"}</c>. An unsupported verb answers exactly
/// <c>unknown method: NAME</c> so callers can tell "not shipped" from "refused".</item>
/// <item>A call never blocks: reads answer from a snapshot, changes are validated, queued and acknowledged. Responses stay
/// under 64 KiB; long data is paged or summarised. The gate must be callable from the framework thread and from a task.</item>
/// <item>GhosttyDalamud changes answer <c>{"queued":true,"request":N}</c>; the outcome appears in <c>window.list</c>
/// <c>requests[]</c> as <c>{request, method, ok, result|error}</c>. The proposed Ghostty change verbs keep that.</item>
/// <item>Almanac gets one JSON gate, <c>Almanac.v1.Call</c>, rather than a dozen discrete gates: the surface is wide
/// (threads, turns, benchmark, model), most payloads are structured anyway, one gate needs one registration and one
/// threading story, and new verbs can be probed without Dalamud type-mismatch errors. XivDesktop keeps its existing
/// discrete-gate style for its three small additions.</item>
/// </list>
/// </summary>
public static class SiblingIpcProposals
{
    public const string GhosttyGate = "GhosttyDalamud.v1.Call";
    public const string AlmanacGate = "Almanac.v1.Call";
    public const string DesktopPanelsGate = "XivDesktop.v1.Panels";
    public const string DesktopRescanGate = "XivDesktop.v1.Rescan";
    public const string DesktopApiVersionGate = "XivDesktop.v1.ApiVersion";

    // Source labels, usable in attributes. A test checks each equals the Source of exactly one row.
    public const string GhosttyApiVersion = "ipc:" + GhosttyGate + "#api.version";
    public const string GhosttyLayoutGet = "ipc:" + GhosttyGate + "#layout.get";
    public const string GhosttyLayoutSet = "ipc:" + GhosttyGate + "#layout.set";
    public const string GhosttyThemeList = "ipc:" + GhosttyGate + "#theme.list";
    public const string GhosttyThemeSet = "ipc:" + GhosttyGate + "#theme.set";
    public const string GhosttyCaptureShot = "ipc:" + GhosttyGate + "#capture.shot";
    public const string GhosttyCaptureClip = "ipc:" + GhosttyGate + "#capture.clip";
    public const string GhosttyCaptureStatus = "ipc:" + GhosttyGate + "#capture.status";
    public const string GhosttySelftestRun = "ipc:" + GhosttyGate + "#selftest.run";
    public const string GhosttySelftestReport = "ipc:" + GhosttyGate + "#selftest.report";
    public const string GhosttyLeakwatchReport = "ipc:" + GhosttyGate + "#leakwatch.report";
    public const string GhosttyGalleryShare = "ipc:" + GhosttyGate + "#gallery.share";
    public const string DesktopPanels = "ipc:" + DesktopPanelsGate;
    public const string DesktopRescan = "ipc:" + DesktopRescanGate;
    public const string DesktopApiVersion = "ipc:" + DesktopApiVersionGate;
    public const string AlmanacApiVersion = "ipc:" + AlmanacGate + "#api.version";
    public const string AlmanacThreadsList = "ipc:" + AlmanacGate + "#threads.list";
    public const string AlmanacThreadsGet = "ipc:" + AlmanacGate + "#threads.get";
    public const string AlmanacThreadNew = "ipc:" + AlmanacGate + "#thread.new";
    public const string AlmanacAsk = "ipc:" + AlmanacGate + "#ask";
    public const string AlmanacTurnStatus = "ipc:" + AlmanacGate + "#turn.status";
    public const string AlmanacBenchRun = "ipc:" + AlmanacGate + "#bench.run";
    public const string AlmanacBenchStatus = "ipc:" + AlmanacGate + "#bench.status";
    public const string AlmanacBenchResult = "ipc:" + AlmanacGate + "#bench.result";
    public const string AlmanacBenchList = "ipc:" + AlmanacGate + "#bench.list";
    public const string AlmanacModelStatus = "ipc:" + AlmanacGate + "#model.status";
    public const string AlmanacBackendsStatus = "ipc:" + AlmanacGate + "#backends.status";

    private const string JsonCall = "Func<string, string> (JSON in, JSON out)";
    private const string GhosttyRead = "Any thread; answers at once from the snapshot the frame publishes (at most one frame old). Never blocks.";
    private const string GhosttyChange =
        "Any thread; validated, given a request id and queued like the existing changes: answers {\"queued\":true,\"request\":N}. " +
        "The outcome appears in window.list requests[] as {request, method, ok, result|error}. Never blocks.";
    private const string AlmanacRead = "Callable from the framework thread or a task; answers from memory or one indexed SQLite read, no model call, returns within a frame.";
    private const string AlmanacChange = "Callable from the framework thread or a task; validates, starts the work in the background and returns at once. Never waits for the model.";

    /// <summary>Every proposal, grouped by sibling.</summary>
    public static IReadOnlyList<SiblingIpcProposal> All { get; } =
    [
        // GhosttyDalamud: new verbs on the existing GhosttyDalamud.v1.Call gate.
        new("ghostty", GhosttyGate, "api.version", JsonCall, SiblingIpcKind.Read,
            "{}",
            "{\"version\": 1, \"verbs\": [\"window.list\", \"layout.get\", …]} — version rises on additive changes; verbs lists every method this build answers.",
            GhosttyRead,
            "Lets a caller show which capabilities this build has without calling each one.",
            ["get_terminal_status"]),
        new("ghostty", GhosttyGate, "layout.get", JsonCall, SiblingIpcKind.Read,
            "{\"name\": \"raid\"} or {} for every saved layout",
            "{\"current\": \"raid\"|null, \"layouts\": [{\"name\": \"raid\", \"panels\": [{\"kind\": \"terminal\"|\"window\", \"profile\": \"bash\"?, " +
            "\"run\": \"foot htop\"?, \"match\": \"Firefox\"?, \"view\": \"pet|pin|hud|tab|dropdown|min\", \"pin\": \"hud 0.85 0.2\" (the exact /term pin arguments " +
            "that reproduce the place, with positions and distances), \"order\": 2?, \"w\": 640?, \"h\": 400?}]}]}",
            GhosttyRead,
            "The panel snapshot does not carry pin positions or distances, so a layout can only be approximated today (get_terminal_layout). " +
            "Named layouts with exact pins make \"put my screens back the way they were\" possible.",
            ["get_terminal_layouts"]),
        new("ghostty", GhosttyGate, "layout.set", JsonCall, SiblingIpcKind.Change,
            "{\"name\": \"raid\", \"apply\": true} applies a saved layout (moves existing panels, opens missing ones, closes nothing); " +
            "{\"name\": \"raid\", \"save\": true} saves the current arrangement under that name. Exactly one of apply/save.",
            "queued; requests[] result {\"name\": \"raid\", \"moved\": 3, \"opened\": 1, \"skipped\": [{\"panel\": …, \"why\": \"…\"}]}",
            GhosttyChange,
            "Applying a layout is one approved action instead of a dozen window.place calls.",
            ["apply_terminal_layout"]),
        new("ghostty", GhosttyGate, "theme.list", JsonCall, SiblingIpcKind.Read,
            "{}",
            "{\"current\": \"Gruvbox Light\", \"themes\": [{\"name\": \"Gruvbox Light\", \"builtin\": true, \"dark\": false}]}",
            GhosttyRead,
            "/term theme prints the list into the game's chat log only; an agent cannot see which names set_terminal_theme accepts.",
            ["list_terminal_themes"]),
        new("ghostty", GhosttyGate, "theme.set", JsonCall, SiblingIpcKind.Change,
            "{\"name\": \"Gruvbox Light\"}",
            "queued; requests[] result {\"name\": \"Gruvbox Light\"} or error \"no theme \\\"x\\\"\"",
            GhosttyChange,
            "Post \"theme NAME\" works today but a misspelt name fails silently; this reports the outcome.",
            ["set_terminal_theme"]),
        new("ghostty", GhosttyGate, "capture.shot", JsonCall, SiblingIpcKind.Change,
            "{\"target\": \"panel\"|\"full\", \"clean\": false}",
            "queued; requests[] result {\"path\": \"…/shot-0001.png\", \"width\": 2560, \"height\": 1440} once the file is written",
            GhosttyChange,
            "Post \"shot …\" returns nothing, so an agent cannot learn the file's path or whether the capture happened.",
            ["capture_terminal_screenshot"]),
        new("ghostty", GhosttyGate, "capture.clip", JsonCall, SiblingIpcKind.Change,
            "{\"seconds\": 6 (1-30), \"format\": \"gif\"|\"mp4\", \"panel\": false, \"clean\": false}",
            "queued; requests[] result {\"started\": true} at once; the finished file shows in capture.status (a clip outlives the 16-entry requests[] window)",
            GhosttyChange,
            "As capture.shot: the chat command gives no result.",
            ["capture_terminal_clip"]),
        new("ghostty", GhosttyGate, "capture.status", JsonCall, SiblingIpcKind.Read,
            "{}",
            "{\"recording\": false, \"remaining\": 0.0, \"last\": {\"kind\": \"shot\"|\"clip\", \"path\": \"…\", \"width\": 2560, \"height\": 1440, " +
            "\"at\": \"2026-01-01T00:00:00Z\", \"error\": null}, \"folder\": \"…\"}",
            GhosttyRead,
            "Confirms a capture finished and where it is.",
            ["get_terminal_capture_status"]),
        new("ghostty", GhosttyGate, "selftest.run", JsonCall, SiblingIpcKind.Change,
            "{\"suites\": [\"all\"]} or suite names as /term selftest takes them; never \"leakwatch\"",
            "queued; requests[] result {\"started\": true, \"suites\": [\"…\"]}; the report arrives through selftest.report",
            GhosttyChange,
            "Post \"selftest …\" starts a run today; this adds a refusal reason (already running, unknown suite).",
            ["run_terminal_selftest"]),
        new("ghostty", GhosttyGate, "selftest.report", JsonCall, SiblingIpcKind.Read,
            "{}",
            "{\"running\": false, \"report\": {the last selftest JSON report: build stamp, per-suite pass/fail/skip counts, failed cases with messages; " +
            "passing cases summarised so the whole stays under 64 KiB}|null, \"path\": \"…/selftest/….json\"}",
            GhosttyRead,
            "The report is a file in GhosttyDalamud's config folder; an MCP client on another machine cannot read it.",
            ["get_terminal_selftest_report"]),
        new("ghostty", GhosttyGate, "leakwatch.report", JsonCall, SiblingIpcKind.Read,
            "{}",
            "{\"enabled\": true, \"samples\": [{\"load\": 1, \"at\": \"…\", \"bytes\": 123456}], \"growthPerLoad\": 1024}",
            GhosttyRead,
            "/term selftest leakwatch logs growth per core load to the plugin log only.",
            ["get_terminal_leakwatch_report"]),
        new("ghostty", GhosttyGate, "gallery.share", JsonCall, SiblingIpcKind.Change,
            "{\"path\": \"…/shot-0001.png\"} or {\"last\": true}; only files inside GhosttyDalamud's own capture folder are accepted",
            "queued; requests[] result {\"url\": \"https://…\", \"path\": \"…\"} once uploaded, or error (sharing is off, not linked, file outside the folder)",
            GhosttyChange,
            "Uploading is the one capture step that leaves the machine; as IPC it can be put to the owner for approval with the file named.",
            ["share_terminal_capture"]),

        // XivDesktop: discrete gates, its existing style.
        new("xivdesktop", DesktopApiVersionGate, null, "Func<int>", SiblingIpcKind.Read,
            "(none)",
            "1, rising on additive changes",
            "Any thread; constant.",
            "A cheap probe that does not serialise the app catalogue or status.",
            ["get_desktop_status"]),
        new("xivdesktop", DesktopPanelsGate, null, "Func<string>", SiblingIpcKind.Read,
            "(none)",
            "{\"launcher\": {\"open\": true, \"search\": \"fire\", \"view\": \"grid|list\", \"category\": \"Internet\"|null, \"page\": 0, \"pages\": 3, " +
            "\"selected\": \"org.mozilla.firefox\"|null, \"visible\": [\"app ids on the current page, in grid order\"]}, \"favourites\": [\"app ids in order\"], " +
            "\"palette\": {\"open\": false}, \"assistant\": {\"summoned\": false, \"speaker\": \"npc:1000\"|null, \"busy\": false}}",
            "Any thread; a payload rebuilt on the framework thread after each change (as Windows is). Never blocks.",
            "What the owner currently sees in the launcher and whether the /ask speaker is busy, so an agent does not ask twice or toggle blind.",
            ["get_desktop_panels"]),
        new("xivdesktop", DesktopRescanGate, null, "Func<string>", SiblingIpcKind.Change,
            "(none)",
            "\"ok: scanning\" or \"error: REASON\" (already scanning); Status.scanning/scannedAt show progress",
            "Any thread; starts the background scan and returns at once.",
            "A newly installed application appears without reloading the plugin.",
            ["rescan_desktop_apps"]),

        // Almanac: one JSON gate, Almanac.v1.Call.
        new("almanac", AlmanacGate, "api.version", JsonCall, SiblingIpcKind.Read,
            "{}",
            "{\"version\": 1, \"verbs\": [\"threads.list\", …]}",
            AlmanacRead,
            "Capability listing for get_almanac_status.",
            ["get_almanac_status"]),
        new("almanac", AlmanacGate, "threads.list", JsonCall, SiblingIpcKind.Read,
            "{\"limit\": 25 (1-100), \"offset\": 0}",
            "{\"total\": 40, \"threads\": [{\"id\": \"t_…\", \"title\": \"…\", \"turns\": 6, \"updatedAt\": \"2026-01-01T00:00:00Z\", \"current\": true}]}",
            AlmanacRead,
            "Find an earlier conversation.",
            ["list_almanac_threads"]),
        new("almanac", AlmanacGate, "threads.get", JsonCall, SiblingIpcKind.Read,
            "{\"id\": \"t_…\", \"limit\": 20 (1-100, newest last), \"before\": turn?}",
            "{\"id\": \"t_…\", \"title\": \"…\", \"turns\": [{\"turn\": 3, \"role\": \"user|assistant\", \"text\": \"… (capped at 8000 chars, truncated: true when cut)\", " +
            "\"state\": \"done|running|failed\", \"tools\": [\"tool names called\"], \"at\": \"…\"}], \"truncated\": false}",
            AlmanacRead,
            "Read a conversation, including the answer to a question asked over IPC.",
            ["get_almanac_thread"]),
        new("almanac", AlmanacGate, "thread.new", JsonCall, SiblingIpcKind.Change,
            "{\"title\": \"…\"?}",
            "{\"threadId\": \"t_…\"} — created and made current, as /almanac new",
            AlmanacChange,
            "Keep an agent's questions out of the owner's running conversation.",
            ["ask_almanac"]),
        new("almanac", AlmanacGate, "ask", JsonCall, SiblingIpcKind.Change,
            "{\"text\": \"…\" (1-4000 chars), \"threadId\": \"t_…\"? (default: the current thread)}",
            "{\"accepted\": true, \"threadId\": \"t_…\", \"turn\": 7} or {\"accepted\": false, \"reason\": \"busy|not set up\"}; as Almanac.Ask, it opens the chat window",
            AlmanacChange,
            "Almanac.Ask answers only true/false; with threadId and turn the caller can fetch the answer through turn.status.",
            ["ask_almanac"]),
        new("almanac", AlmanacGate, "turn.status", JsonCall, SiblingIpcKind.Read,
            "{\"threadId\": \"t_…\", \"turn\": 7}",
            "{\"state\": \"queued|running|done|failed\", \"text\": \"the answer so far (capped at 8000 chars)\"?, \"truncated\": false, \"error\": \"…\"?, \"tools\": [\"…\"]}",
            AlmanacRead,
            "Poll for the answer instead of reading the owner's screen.",
            ["get_almanac_turn"]),
        new("almanac", AlmanacGate, "bench.run", JsonCall, SiblingIpcKind.Change,
            "{\"mode\": \"mock\"|\"live\", \"model\": \"…\"? (default: the configured model), \"tasks\": [\"task ids\"]? (default: all)}",
            "{\"runId\": \"b_…\", \"total\": 24} or error (a run is in progress, not set up). Results are stored locally only: there is deliberately no verb that submits to a leaderboard.",
            AlmanacChange,
            "Start the benchmark the Benchmark window runs, without the window.",
            ["run_almanac_benchmark"]),
        new("almanac", AlmanacGate, "bench.status", JsonCall, SiblingIpcKind.Read,
            "{\"runId\": \"b_…\"}",
            "{\"runId\": \"b_…\", \"state\": \"running|done|failed|cancelled\", \"done\": 5, \"total\": 24, \"currentTask\": \"…\"|null, \"error\": \"…\"?}",
            AlmanacRead,
            "Progress of a run.",
            ["get_almanac_benchmark"]),
        new("almanac", AlmanacGate, "bench.result", JsonCall, SiblingIpcKind.Read,
            "{\"runId\": \"b_…\"} or {\"last\": true}",
            "the v1 results JSON summary: {\"runId\", \"mode\", \"model\", \"startedAt\", \"finishedAt\", \"score\", \"successRate\", \"toolCallValidity\", \"tokensPerSecond\", " +
            "\"ttftMs\", \"tasks\": [{\"id\", \"passed\", \"score\", \"toolCalls\", \"validToolCalls\", \"ms\", \"error\"?}]}; prompts and transcripts are left out",
            AlmanacRead,
            "Compare models from an agent.",
            ["get_almanac_benchmark"]),
        new("almanac", AlmanacGate, "bench.list", JsonCall, SiblingIpcKind.Read,
            "{\"limit\": 10 (1-50)}",
            "{\"runs\": [{\"runId\", \"mode\", \"model\", \"finishedAt\", \"score\", \"successRate\", \"state\"}]}",
            AlmanacRead,
            "Earlier runs to compare against.",
            ["list_almanac_benchmarks"]),
        new("almanac", AlmanacGate, "model.status", JsonCall, SiblingIpcKind.Read,
            "{}",
            "{\"setupComplete\": true, \"model\": \"…\", \"source\": \"local|remote|xivmcp\", \"baseUrlHost\": \"localhost:11434\" (host and port only: never the path, query, user info or a key), " +
            "\"toolMode\": \"native|prompted|none\", \"xivMcpLinked\": true, \"busy\": false}",
            AlmanacRead,
            "Which model answers and whether Almanac is linked to XivMcp, without exposing credentials.",
            ["get_almanac_model_status"]),
        new("almanac", AlmanacGate, "backends.status", JsonCall, SiblingIpcKind.Read,
            "{}",
            "{\"backends\": [{\"name\": \"…\", \"kind\": \"ollama|openai-compatible|…\", \"host\": \"host:port\", \"reachable\": true, \"checkedAt\": \"…\", \"models\": 3, \"error\": \"…\"?}]} " +
            "from the last background check; this verb starts no network request itself",
            AlmanacRead,
            "Tell \"the model server is down\" from \"Almanac is busy\".",
            ["get_almanac_model_status"]),
    ];

    /// <summary>The row for a verb on a JSON gate, or for a discrete gate when <paramref name="verb"/> is null.</summary>
    public static SiblingIpcProposal? Find(string gate, string? verb) =>
        All.FirstOrDefault(p => p.Gate.Equals(gate, StringComparison.Ordinal) && string.Equals(p.Verb, verb, StringComparison.Ordinal));

    /// <summary>The row whose capability name ("Gate" or "Gate#verb") is <paramref name="capability"/>.</summary>
    public static SiblingIpcProposal? FindCapability(string capability) =>
        All.FirstOrDefault(p => p.Capability.Equals(capability, StringComparison.Ordinal));

    /// <summary>The tool error for a capability the sibling has not shipped: <c>unavailable</c>, naming the missing gate or verb.</summary>
    public static Core.McpToolException NotShipped(SiblingIpcProposal proposal)
    {
        var display = BridgeCatalog.Find(proposal.Sibling)?.DisplayName ?? proposal.Sibling;
        var what = proposal.Verb is null
            ? $"the IPC gate {proposal.Gate} ({proposal.Signature})"
            : $"the \"{proposal.Verb}\" verb on {proposal.Gate}";
        return Core.McpToolException.WithCode(
            Core.McpErrorCodes.Unavailable,
            $"{display} does not offer {what} yet. This tool is written against that proposed IPC and starts working when {display} ships it; " +
            "nothing on the XivMcp side needs to change.");
    }
}

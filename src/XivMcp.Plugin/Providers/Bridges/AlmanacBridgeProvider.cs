using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Bridges;

namespace XivMcp.Plugin.Providers.Bridges;

/// <summary>
/// Almanac, the in-game assistant. Today it can only be handed a question (Almanac.Ask); reading threads and answers,
/// the benchmark and the model status are clients for the proposed Almanac.v1.Call gate and answer "unavailable" until
/// Almanac ships it. There is deliberately no tool, and no proposed verb, that submits benchmark results anywhere.
/// </summary>
[McpProvider("bridges")]
public sealed class AlmanacBridgeProvider
{
    private const string Needs = " NEEDS A NEWER Almanac: written against the proposed Almanac.v1.Call gate; fails with unavailable, naming the missing verb, until Almanac ships it.";

    private readonly AlmanacBridge bridge;

    public AlmanacBridgeProvider(IDalamudPluginInterface pluginInterface, IPluginLog log) =>
        bridge = new AlmanacBridge(new DalamudBridgeInvoker(pluginInterface, log));

    [McpTool("get_almanac_status",
        Sources = ["dalamud:InstalledPlugins", "ipc:" + AlmanacBridge.ApiVersionGate, SiblingIpcProposals.AlmanacApiVersion],
        Title = "Almanac status",
        RequiresLogin = false,
        Description =
            "Whether Almanac (the in-game assistant plugin) is installed, loaded and answering IPC, its ApiVersion, whether ask_almanac can work (askGate) and " +
            "whether it offers the JSON call gate the thread, turn, benchmark and model tools need (callGate, with its verb list in api). Never fails for an " +
            "absent plugin: bridge.unavailable says why.")]
    public AlmanacStatusDto GetAlmanacStatus() => bridge.GetStatus();

    [McpTool("ask_almanac",
        Sources = ["ipc:" + AlmanacBridge.AskGate, SiblingIpcProposals.AlmanacAsk, SiblingIpcProposals.AlmanacThreadNew],
        Title = "Ask Almanac",
        Permission = ToolPermission.Action,
        Idempotent = false,
        OpenWorld = true,
        RequiresLogin = false,
        ApprovalSummary = "Open Almanac's chat window and ask its model this (new conversation: {newThread}): {question}",
        Description =
            "Hands a question to Almanac: its chat window opens on the player's screen and its configured model (possibly a remote service) starts answering. " +
            "Returns accepted (false when Almanac is busy or not set up), NOT the answer: with today's Almanac (via=ask) the answer only appears in its " +
            "window. With an Almanac that offers the JSON gate (via=call) threadId and turn come back and get_almanac_turn reads the answer. " +
            "newThread=true starts a fresh conversation first and needs that newer Almanac.")]
    public AlmanacAskDto AskAlmanac(
        [McpParam("The question, shown verbatim to the player (up to 4000 bytes).")] string question,
        [McpParam("Start a new conversation instead of continuing the current one (needs the JSON gate).")] bool newThread = false) =>
        bridge.Ask(question, newThread);

    [McpTool("get_almanac_turn",
        Sources = [SiblingIpcProposals.AlmanacTurnStatus],
        Title = "Read an Almanac answer",
        RequiresLogin = false,
        Description =
            "The state of one Almanac turn (queued|running|done|failed) and its answer text so far, by the threadId and turn ask_almanac returned. Poll it " +
            "every second or two until done." + Needs)]
    public BridgeDataDto GetAlmanacTurn(
        [McpParam("Thread id from ask_almanac.")] string threadId,
        [McpParam("Turn number from ask_almanac.", Minimum = 0)] long turn) =>
        bridge.Call("turn.status", new JsonObject { ["threadId"] = BridgeJson.CheckArgument("threadId", threadId, maxBytes: 100), ["turn"] = NonNegative(turn, "turn") });

    [McpTool("list_almanac_threads",
        Sources = [SiblingIpcProposals.AlmanacThreadsList],
        Title = "List Almanac conversations",
        RequiresLogin = false,
        Description = "Almanac's conversations, newest first: id, title, turns, updatedAt and which is current. These are the player's private conversations with their assistant." + Needs)]
    public BridgeDataDto ListAlmanacThreads(
        [McpParam("Maximum threads (1-100).", Minimum = 1, Maximum = 100)] int limit = 25,
        [McpParam("Threads to skip.", Minimum = 0)] int offset = 0) =>
        bridge.Call("threads.list", new JsonObject { ["limit"] = Math.Clamp(limit, 1, 100), ["offset"] = Math.Max(0, offset) });

    [McpTool("get_almanac_thread",
        Sources = [SiblingIpcProposals.AlmanacThreadsGet],
        Title = "Read an Almanac conversation",
        RequiresLogin = false,
        Description = "The turns of one Almanac conversation (role, text capped at 8000 characters, state, tools called, time), newest last." + Needs)]
    public BridgeDataDto GetAlmanacThread(
        [McpParam("Thread id from list_almanac_threads.")] string id,
        [McpParam("Maximum turns (1-100).", Minimum = 1, Maximum = 100)] int limit = 20) =>
        bridge.Call("threads.get", new JsonObject { ["id"] = BridgeJson.CheckArgument("id", id, maxBytes: 100), ["limit"] = Math.Clamp(limit, 1, 100) });

    [McpTool("run_almanac_benchmark",
        Sources = [SiblingIpcProposals.AlmanacBenchRun],
        Title = "Run Almanac's benchmark",
        Permission = ToolPermission.Action,
        Idempotent = false,
        OpenWorld = true,
        RequiresLogin = false,
        ApprovalSummary = "Run Almanac's benchmark in {mode} mode with model {model} (tasks: {tasks}). Live mode calls your model for every task. Results stay on this computer.",
        Description =
            "Starts Almanac's model benchmark: mode mock (no model is called; checks the harness) or live (the model answers every task, which takes minutes " +
            "and, with a paid remote model, costs money). model overrides the configured one; tasks limits the run to some task ids. Returns runId and total; " +
            "follow it with get_almanac_benchmark. Results are stored locally only: nothing here, and no proposed verb, submits to a leaderboard." + Needs)]
    public BridgeDataDto RunAlmanacBenchmark(
        [McpParam("mock or live.", Enum = ["mock", "live"])] string mode = "mock",
        [McpParam("Model name. Omit for the configured model.")] string? model = null,
        [McpParam("Task ids separated by spaces or commas. Omit for all.")] string? tasks = null) =>
        bridge.Call("bench.run", AlmanacBridge.BenchmarkParameters(mode, model, tasks), "Started. Poll get_almanac_benchmark with the runId.");

    [McpTool("get_almanac_benchmark",
        Sources = [SiblingIpcProposals.AlmanacBenchStatus, SiblingIpcProposals.AlmanacBenchResult],
        Title = "Almanac benchmark progress or result",
        RequiresLogin = false,
        Description =
            "For a runId from run_almanac_benchmark: its progress (state, done, total, currentTask) while it runs, or once done the results summary (score, " +
            "successRate, toolCallValidity, tokensPerSecond, ttftMs and per-task rows). runId \"last\" (default) returns the most recent finished run." + Needs)]
    public BridgeDataDto GetAlmanacBenchmark(
        [McpParam("Run id, or \"last\".")] string runId = "last") =>
        bridge.GetBenchmark(runId);

    [McpTool("list_almanac_benchmarks",
        Sources = [SiblingIpcProposals.AlmanacBenchList],
        Title = "List Almanac benchmark runs",
        RequiresLogin = false,
        Description = "Earlier benchmark runs, newest first: runId, mode, model, finishedAt, score, successRate, state. Use get_almanac_benchmark for one run's rows." + Needs)]
    public BridgeDataDto ListAlmanacBenchmarks(
        [McpParam("Maximum runs (1-50).", Minimum = 1, Maximum = 50)] int limit = 10) =>
        bridge.Call("bench.list", new JsonObject { ["limit"] = Math.Clamp(limit, 1, 50) });

    [McpTool("get_almanac_model_status",
        Sources = [SiblingIpcProposals.AlmanacModelStatus, SiblingIpcProposals.AlmanacBackendsStatus],
        Title = "Almanac model status",
        RequiresLogin = false,
        Description =
            "Which model answers in Almanac: setupComplete, model, source, baseUrlHost (host and port only, never a key), toolMode, xivMcpLinked (whether it " +
            "uses this XivMcp server for tools), busy; plus backends (each model server's reachability from Almanac's last check)." + Needs)]
    public AlmanacModelDto GetAlmanacModelStatus() => bridge.GetModelStatus();

    private static long NonNegative(long value, string name) =>
        value >= 0 ? value : throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"{name} cannot be negative.");
}

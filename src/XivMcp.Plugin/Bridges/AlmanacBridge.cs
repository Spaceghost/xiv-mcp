using System.Text.Json.Nodes;
using XivMcp.Core;

namespace XivMcp.Plugin.Bridges;

public sealed record AlmanacStatusDto(BridgeStatus Bridge, bool AskGate, bool CallGate, JsonNode? Api, string Note);

public sealed record AlmanacAskDto(bool Accepted, string Via, string? ThreadId, long? Turn, string? Reason, string Note);

public sealed record AlmanacModelDto(JsonNode? Model, JsonNode? Backends, string Note);

/// <summary>
/// Almanac (the in-game assistant). Today it offers two gates: <c>Almanac.ApiVersion</c> and <c>Almanac.Ask</c>, which
/// accepts a question and shows the answer in its own window. Everything else here is a client for the proposed
/// <c>Almanac.v1.Call</c> JSON gate (see <see cref="SiblingIpcProposals"/>).
/// </summary>
public sealed class AlmanacBridge
{
    public const string Bridge = "almanac";
    public const string Sibling = "Almanac";
    public const string ApiVersionGate = "Almanac.ApiVersion";
    public const string AskGate = "Almanac.Ask";
    public const string CallGate = SiblingIpcProposals.AlmanacGate;
    public const string Caller = "XivMcp";

    /// <summary>Longest question sent (the proposed ask verb's limit).</summary>
    public const int MaxQuestionBytes = 4000;

    public static readonly IReadOnlyList<string> BenchmarkModes = ["mock", "live"];

    private readonly IBridgeInvoker invoker;

    public AlmanacBridge(IBridgeInvoker invoker) => this.invoker = invoker;

    /// <summary>The request JSON for a proposed verb. Only verbs in the proposals table can be built.</summary>
    public static string BuildRequest(string method, JsonObject? parameters = null)
    {
        if (SiblingIpcProposals.Find(CallGate, method) is null)
        {
            throw McpToolException.WithCode(McpErrorCodes.Refused, $"\"{method}\" is not an Almanac verb XivMcp sends.");
        }

        var request = new JsonObject { ["method"] = method, ["caller"] = Caller };
        if (parameters is { Count: > 0 })
        {
            request["params"] = parameters;
        }

        return request.ToJsonString();
    }

    public static JsonObject BenchmarkParameters(string? mode, string? model, string? tasks)
    {
        var chosen = (mode ?? "").Trim().ToLowerInvariant();
        if (!BenchmarkModes.Contains(chosen, StringComparer.Ordinal))
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "mode is \"mock\" (no model is called) or \"live\" (the configured model answers every task).");
        }

        var parameters = new JsonObject { ["mode"] = chosen };
        var modelName = BridgeJson.CheckArgument("model", model, allowEmpty: true, maxBytes: 200);
        if (modelName.Length > 0)
        {
            parameters["model"] = modelName;
        }

        var ids = BridgeJson.CheckArgument("tasks", tasks, allowEmpty: true).Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        if (ids.Count > 100 || ids.Any(t => t.Length > 64 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':')))
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "tasks is up to 100 task ids (letters, digits, _ - . :) separated by spaces or commas.");
        }

        if (ids.Count > 0)
        {
            parameters["tasks"] = new JsonArray(ids.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        return parameters;
    }

    public AlmanacStatusDto GetStatus()
    {
        var status = invoker.Probe(Bridge) ?? throw new InvalidOperationException("almanac is not in the bridge catalog.");
        JsonNode? api = null;
        var call = false;
        if (status.IpcAvailable)
        {
            try
            {
                api = BridgeJson.Cap(Send("api.version", null)).Node;
                call = true;
            }
            catch (BridgeCapabilityMissingException ex)
            {
                // "unknown method" still proves the gate is there; "not registered" names the gate alone.
                call = ex.Capability.Contains('#', StringComparison.Ordinal);
            }
            catch (McpToolException)
            {
                call = true;
            }
        }

        return new AlmanacStatusDto(status, status.IpcAvailable, call, api,
            status.IpcAvailable
                ? "askGate: ask_almanac works (the answer appears in Almanac's chat window, it is not returned). callGate: Almanac offers the proposed " +
                  CallGate + " JSON gate, which the thread, turn, benchmark and model tools need; api lists its verbs when it does."
                : status.Unavailable ?? "Almanac is not available.");
    }

    public AlmanacAskDto Ask(string? question, bool newThread)
    {
        var text = BridgeJson.CheckArgument("question", question, maxBytes: MaxQuestionBytes);
        invoker.Require(Bridge);

        string? threadId = null;
        if (newThread)
        {
            threadId = BridgeJson.Text(Proposed("thread.new", null), "threadId")
                       ?? throw new McpToolException("Almanac answered thread.new without a threadId.");
        }

        try
        {
            var parameters = new JsonObject { ["text"] = text };
            if (threadId is not null)
            {
                parameters["threadId"] = threadId;
            }

            var result = Send("ask", parameters);
            var accepted = BridgeJson.Flag(result, "accepted") ?? false;
            return new AlmanacAskDto(accepted, "call", BridgeJson.Text(result, "threadId") ?? threadId, BridgeJson.Integer(result, "turn"),
                accepted ? null : BridgeJson.Clip(BridgeJson.Text(result, "reason") ?? "no reason given", BridgeJson.MaxErrorChars),
                accepted ? "Accepted. Read the answer with get_almanac_turn (threadId, turn); it also appears in Almanac's chat window." : "Almanac did not take the question.");
        }
        catch (BridgeCapabilityMissingException)
        {
            // No JSON gate yet: the gate Almanac has today.
        }

        bool taken;
        try
        {
            taken = invoker.CallBool(AskGate, text);
        }
        catch (BridgeCapabilityMissingException)
        {
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"Almanac is loaded but {AskGate} is not registered.", retryable: true);
        }

        return new AlmanacAskDto(taken, "ask", null, null, taken ? null : "busy, or Almanac's setup is not finished",
            taken
                ? "Accepted. The answer appears in Almanac's chat window in game; this version of Almanac has no IPC to read it back."
                : "Almanac did not take the question: it is answering something else, or its setup is not finished.");
    }

    /// <summary>Calls a proposed verb; <c>unavailable</c> until Almanac ships it.</summary>
    public BridgeDataDto Call(string method, JsonObject? parameters, string? note = null)
    {
        invoker.Require(Bridge);
        var capped = BridgeJson.Cap(Proposed(method, parameters));
        return new BridgeDataDto(Bridge, $"{CallGate}#{method}", capped.Node, capped.Truncated, capped.RawPrefix, note);
    }

    public BridgeDataDto GetBenchmark(string? runId)
    {
        var id = BridgeJson.CheckArgument("runId", runId, allowEmpty: true, maxBytes: 100);
        if (id.Length == 0 || id.Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            return Call("bench.result", new JsonObject { ["last"] = true }, "The most recent finished run.");
        }

        invoker.Require(Bridge);
        var status = Proposed("bench.status", new JsonObject { ["runId"] = id });
        if (!string.Equals(BridgeJson.Text(status, "state"), "done", StringComparison.Ordinal))
        {
            var capped = BridgeJson.Cap(status);
            return new BridgeDataDto(Bridge, $"{CallGate}#bench.status", capped.Node, capped.Truncated, capped.RawPrefix, "Not finished: this is the run's progress. Ask again for the result.");
        }

        return Call("bench.result", new JsonObject { ["runId"] = id }, "Finished: the results summary.");
    }

    public AlmanacModelDto GetModelStatus()
    {
        invoker.Require(Bridge);
        var model = BridgeJson.Cap(Proposed("model.status", null)).Node;
        JsonNode? backends = null;
        try
        {
            backends = BridgeJson.Cap(Send("backends.status", null)).Node;
        }
        catch (BridgeCapabilityMissingException)
        {
            // model.status alone is still useful.
        }

        return new AlmanacModelDto(model, backends, "baseUrlHost and backends[].host are host:port only. backends is null when Almanac does not offer backends.status.");
    }

    private JsonNode? Proposed(string method, JsonObject? parameters)
    {
        try
        {
            return Send(method, parameters);
        }
        catch (BridgeCapabilityMissingException)
        {
            throw SiblingIpcProposals.NotShipped(SiblingIpcProposals.Find(CallGate, method)!);
        }
    }

    private JsonNode? Send(string method, JsonObject? parameters) =>
        BridgeJson.ParseEnvelope(Sibling, CallGate, method, invoker.Call(CallGate, BuildRequest(method, parameters)));
}

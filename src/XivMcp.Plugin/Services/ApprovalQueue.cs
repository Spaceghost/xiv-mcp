using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>Lifecycle of a deferred approval ticket. Executed, Failed, Denied, Cancelled and Expired are final.</summary>
public enum TicketState
{
    /// <summary>Waiting for the player. Never turns into Denied by itself; only an explicit client expiry ends it.</summary>
    Pending,

    /// <summary>Approved and queued or running.</summary>
    Approved,

    /// <summary>The tool ran; <see cref="Ticket.ResultJson"/> holds its result.</summary>
    Executed,

    /// <summary>Approved, but the tool did not run or reported an error (see <see cref="Ticket.Error"/>).</summary>
    Failed,

    Denied,

    /// <summary>Withdrawn by the client that requested it.</summary>
    Cancelled,

    /// <summary>The client's own expiry passed before the player decided.</summary>
    Expired,
}

/// <summary>One queued Action/Chat call. Immutable; the queue replaces it on every change.</summary>
public sealed record Ticket
{
    public required string Id { get; init; }

    public required string ToolName { get; init; }

    /// <summary>The tool's declared tier.</summary>
    public required ToolPermission Permission { get; init; }

    /// <summary>What the call effectively does (Chat for an execute_command line that posts chat).</summary>
    public required ToolPermission Tier { get; init; }

    /// <summary>The arguments object as compact JSON, kept to run the call. Never written to logs or the activity feed.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>What the call will do, in one sentence, as the tool describes it (absent on tickets from older builds).</summary>
    public string? Summary { get; init; }

    public required string Reason { get; init; }

    public string? ResumeToken { get; init; }

    /// <summary>Client-reported name and version (not authenticated). Owner of the ticket.</summary>
    public string? ClientName { get; init; }

    public string? SessionId { get; init; }

    /// <summary>Per-client token name the request authenticated with (not self-reported), or null.</summary>
    public string? AuthenticatedClient { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public TicketState State { get; init; }

    /// <summary>"player", "policy", "session", "grant", "confirmation off", "client" (cancel) or "system".</summary>
    public string? DecidedBy { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The tools/call result object (content, structuredContent, isError) as JSON, possibly truncated.</summary>
    public string? ResultJson { get; init; }

    public string? Error { get; init; }

    [JsonIgnore]
    public bool IsFinal => State is TicketState.Executed or TicketState.Failed or TicketState.Denied or TicketState.Cancelled or TicketState.Expired;

    /// <summary>Owner key used for per-client limits and ownership checks.</summary>
    [JsonIgnore]
    public string Owner => ApprovalQueue.OwnerOf(ClientName);

    /// <summary>Activity-feed target: tool and ticket id, never arguments.</summary>
    [JsonIgnore]
    public string ActivityTarget => $"{ToolName} #{Id}";
}

/// <summary>What <see cref="ApprovalQueue"/> needs from the MCP server; <see cref="ServerToolRunner"/> in the plugin.</summary>
public interface IToolRunner
{
    /// <summary>Null when the call could run now, otherwise the error a client would get. Runs nothing.</summary>
    string? CheckToolCall(string toolName, JsonObject? arguments, out ToolPermission permission);

    /// <summary>Whether the call goes through the approval gate, and what it will do in one sentence. Null when unknown.</summary>
    ToolApprovalInfo? DescribeApproval(string toolName, JsonObject? arguments) => null;

    /// <summary>Runs an approved call with the server's normal validation and call timeout.</summary>
    Task<ToolExecutionResult> ExecuteApprovedToolAsync(string toolName, JsonObject? arguments, string? clientName, string? sessionId, string? authenticatedClient, CancellationToken cancellationToken);
}

/// <summary><see cref="IToolRunner"/> over the plugin's <see cref="McpServer"/>.</summary>
public sealed class ServerToolRunner(McpServer server) : IToolRunner
{
    public string? CheckToolCall(string toolName, JsonObject? arguments, out ToolPermission permission) =>
        server.CheckToolCall(toolName, arguments, out permission);

    public ToolApprovalInfo? DescribeApproval(string toolName, JsonObject? arguments) => server.DescribeApproval(toolName, arguments);

    public Task<ToolExecutionResult> ExecuteApprovedToolAsync(string toolName, JsonObject? arguments, string? clientName, string? sessionId, string? authenticatedClient, CancellationToken cancellationToken) =>
        server.ExecuteApprovedToolAsync(toolName, arguments, clientName, sessionId, cancellationToken, authenticatedClient);
}

/// <summary>A ticket event for the activity feed. Carries the ticket for identity only; sinks must not log its arguments.</summary>
public sealed record TicketActivity(string Method, Ticket Ticket, bool Success, string? Error, double DurationMs);

/// <summary>What a client asks for through request_action.</summary>
public sealed record TicketRequest(
    string ToolName,
    JsonObject? Arguments,
    string Reason,
    string? ResumeToken,
    int? ExpiresInSeconds,
    string? ClientName,
    string? SessionId,
    string? AuthenticatedClient = null);

/// <summary>
/// The deferred approval queue. Clients file Action/Chat calls as tickets (request_action) and get an id back at once;
/// the player approves or denies them in the XivMcp window whenever they like, and approved tickets run one at a time,
/// in approval order, through <see cref="IToolRunner"/> with the server's normal validation and call timeout. Tickets
/// persist in the plugin config directory, so they survive plugin reloads and game restarts. A pending ticket never
/// times out into Deny; only a client-set expiry, a cancel, the player, or disabling the Action tier ends it.
/// Thread-safe. <see cref="Changed"/> and <see cref="ActivityRecorded"/> are raised outside the lock on any thread.
/// </summary>
public sealed class ApprovalQueue : IDisposable
{
    public const int MaxPendingPerClient = 50;
    public const int MaxPendingTotal = 200;
    public const int MaxFinishedKept = 300;
    public const int MaxReasonLength = 300;
    public const int MaxResumeTokenLength = 512;
    public const int MaxArgumentsLength = 16 * 1024;
    public const int MaxResultLength = 32 * 1024;
    public const int MinExpirySeconds = 30;
    public const int MaxExpirySeconds = 7 * 24 * 60 * 60;
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(7);

    private readonly Configuration config;
    private readonly TicketStore store;
    private readonly IToolRunner runner;
    private readonly ConfirmationService confirmations;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly List<Ticket> tickets = [];
    private readonly Channel<string> runQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource disposing = new();
    private readonly Task worker;
    private readonly object saveGate = new();
    private int saveQueued;
    private bool saveClosed;
    private bool disposed;

    public ApprovalQueue(Configuration config, TicketStore store, IToolRunner runner, ConfirmationService confirmations, TimeProvider? time = null)
    {
        this.config = config;
        this.store = store;
        this.runner = runner;
        this.confirmations = confirmations;
        this.time = time ?? TimeProvider.System;

        var now = this.time.GetUtcNow();
        foreach (var loaded in store.Load())
        {
            // A ticket that was running when the plugin went away has an unknown outcome. Never run it twice.
            tickets.Add(loaded.State == TicketState.Approved
                ? loaded with
                {
                    State = TicketState.Failed,
                    CompletedAt = now,
                    Error = "Interrupted: the plugin unloaded while this ticket was approved or running, so its outcome is unknown. " +
                            "It was not retried; check the game state before requesting it again.",
                }
                : loaded);
        }

        worker = Task.Run(RunWorkerAsync);
        EnforcePermissions();
        Tick();
    }

    /// <summary>Raised when tickets change; the argument is the changed ticket, or null for bulk changes.</summary>
    public event Action<Ticket?>? Changed;

    public event Action<TicketActivity>? ActivityRecorded;

    public static string OwnerOf(string? clientName) => clientName ?? "";

    public int PendingCount
    {
        get
        {
            lock (gate)
                return tickets.Count(t => t.State == TicketState.Pending);
        }
    }

    /// <summary>Every ticket, oldest first.</summary>
    public IReadOnlyList<Ticket> Snapshot()
    {
        lock (gate)
            return tickets.ToArray();
    }

    public Ticket? Get(string id)
    {
        lock (gate)
            return tickets.FirstOrDefault(t => t.Id == id);
    }

    /// <summary>1-based position among pending tickets (oldest first), or null when the ticket is not pending.</summary>
    public int? PendingPosition(string id)
    {
        lock (gate)
        {
            var position = 0;
            foreach (var t in tickets)
            {
                if (t.State != TicketState.Pending)
                    continue;
                position++;
                if (t.Id == id)
                    return position;
            }
        }

        return null;
    }

    // ---- client side --------------------------------------------------------------------------------

    /// <summary>
    /// Files a ticket. Throws <see cref="McpToolException"/> with a message for the client when the call cannot be queued.
    /// When the call would run without a prompt anyway (an approval session or grant covers it, or confirmation is off),
    /// the ticket is approved at once and queued to run.
    /// </summary>
    public Ticket Submit(TicketRequest request)
    {
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length == 0)
            throw new McpToolException("reason must not be empty: tell the player in one line why you want to do this.");
        if (reason.Length > MaxReasonLength)
            throw new McpToolException($"reason is too long ({reason.Length} characters, max {MaxReasonLength}).");
        var resumeToken = string.IsNullOrEmpty(request.ResumeToken) ? null : request.ResumeToken;
        if (resumeToken is { Length: > MaxResumeTokenLength })
            throw new McpToolException($"resumeToken is too long ({resumeToken.Length} characters, max {MaxResumeTokenLength}).");
        if (request.ExpiresInSeconds is { } expires && (expires < MinExpirySeconds || expires > MaxExpirySeconds))
            throw new McpToolException($"expiresInSeconds must be between {MinExpirySeconds} and {MaxExpirySeconds} (7 days); omit it to wait until the player decides.");

        var argumentsJson = request.Arguments is null or { Count: 0 } ? null : request.Arguments.ToJsonString(McpJson.Options);
        if (argumentsJson is { Length: > MaxArgumentsLength })
            throw new McpToolException($"arguments are too large ({argumentsJson.Length} characters of JSON, max {MaxArgumentsLength}).");

        if (runner.CheckToolCall(request.ToolName, request.Arguments, out var permission) is { } problem)
            throw new McpToolException(problem);
        var approval = runner.DescribeApproval(request.ToolName, request.Arguments);
        var gatedUi = permission == ToolPermission.Ui && approval?.NeedsApproval == true;
        if (permission < ToolPermission.Action && !gatedUi)
            throw new McpToolException($"Tool '{request.ToolName}' is a {permission} tool and needs no approval; call it directly.");
        if (!config.AllowAction && !gatedUi)
            throw new McpToolException("The approval queue is closed: the Action permission tier is disabled in the xiv-mcp plugin settings (/xivmcp).");
        if (ConfirmationService.EffectiveTier(request.ToolName, permission, argumentsJson, config.AllowChat) is not { } tier)
            throw new McpToolException($"Tool '{request.ToolName}' would refuse this call by itself (a blocked or automation command, or chat while the Chat tier is off); it cannot be queued.");
        if (tier == ToolPermission.Chat && !config.AllowChat)
            throw new McpToolException("This call posts chat, and the Chat permission tier is disabled in the xiv-mcp plugin settings.");

        var now = time.GetUtcNow();
        var ticket = new Ticket
        {
            Id = NewId(),
            ToolName = request.ToolName,
            Permission = permission,
            Tier = tier,
            ArgumentsJson = argumentsJson,
            Summary = approval?.Summary,
            Reason = reason,
            ResumeToken = resumeToken,
            ClientName = request.ClientName,
            SessionId = request.SessionId,
            AuthenticatedClient = request.AuthenticatedClient,
            CreatedAt = now,
            ExpiresAt = request.ExpiresInSeconds is { } seconds ? now + TimeSpan.FromSeconds(seconds) : null,
            State = TicketState.Pending,
        };

        var call = new ToolCallApprovalRequest(ticket.ToolName, permission, ticket.ClientName, ticket.SessionId, argumentsJson, ticket.AuthenticatedClient) { Summary = approval?.Summary };
        if (confirmations.PassesWithoutPrompt(call, tier, out var how))
            ticket = ticket with { State = TicketState.Approved, DecidedBy = how, DecidedAt = now };

        var owner = ticket.Owner;
        lock (gate)
        {
            if (disposed)
                throw new McpToolException("The approval queue is shutting down (plugin unloading).");
            var pendingForClient = tickets.Count(t => t.State == TicketState.Pending && t.Owner == owner);
            if (ticket.State == TicketState.Pending && pendingForClient >= MaxPendingPerClient)
            {
                throw new McpToolException(
                    $"Approval queue full for client '{request.ClientName ?? "(unnamed)"}': {pendingForClient} tickets are already waiting (max {MaxPendingPerClient}). " +
                    "Wait for the player to decide, or cancel_ticket ones you no longer need, before requesting more.");
            }

            if (ticket.State == TicketState.Pending && tickets.Count(t => t.State == TicketState.Pending) >= MaxPendingTotal)
                throw new McpToolException($"The approval queue is full ({MaxPendingTotal} tickets waiting across all clients). Try again after the player has gone through it.");
            tickets.Add(ticket);
            PruneLocked(now);
        }

        Record("tickets/request", ticket, true, null);
        AfterChange(ticket);
        if (ticket.State == TicketState.Approved)
        {
            Record("tickets/approve", ticket, true, null);
            runQueue.Writer.TryWrite(ticket.Id);
        }

        return ticket;
    }

    /// <summary>Withdraws a pending ticket. Only the client that filed it may cancel it.</summary>
    public Ticket Cancel(string id, string? clientName)
    {
        var owner = OwnerOf(clientName);
        Ticket updated;
        lock (gate)
        {
            var index = tickets.FindIndex(t => t.Id == id && t.Owner == owner);
            if (index < 0)
                throw new McpToolException($"No ticket '{id}' from this client.");
            var current = tickets[index];
            if (current.State != TicketState.Pending)
                throw new McpToolException($"Ticket '{id}' is {Name(current.State)}, not pending; only pending tickets can be cancelled.");
            updated = current with { State = TicketState.Cancelled, DecidedBy = "client", DecidedAt = time.GetUtcNow() };
            tickets[index] = updated;
        }

        Record("tickets/cancel", updated, true, null);
        AfterChange(updated);
        return updated;
    }

    // ---- player side --------------------------------------------------------------------------------

    /// <summary>Approves a pending ticket; it runs as soon as the tickets approved before it have finished.</summary>
    public bool Approve(string id) => Decide(id, approve: true, "player", null) != null;

    public bool Deny(string id) => Decide(id, approve: false, "player", null) != null;

    /// <summary>Approves every pending ticket, oldest first. Returns how many.</summary>
    public int ApproveAll() => DecideAll(approve: true, null);

    /// <summary>Approves every pending ticket from one client, oldest first. Returns how many.</summary>
    public int ApproveAllFrom(string? clientName) => DecideAll(approve: true, OwnerOf(clientName));

    public int DenyAll() => DecideAll(approve: false, null);

    public int DenyAllFrom(string? clientName) => DecideAll(approve: false, OwnerOf(clientName));

    /// <summary>Removes final tickets from the list (clients polling them afterwards get "not found").</summary>
    public int ClearFinished()
    {
        int removed;
        lock (gate)
            removed = tickets.RemoveAll(t => t.IsFinal);
        if (removed > 0)
            AfterChange(null);
        return removed;
    }

    /// <summary>
    /// Denies what may no longer run: every pending ticket while the Action tier is off, Chat-tier ones while the Chat tier
    /// is off. Call after permission changes.
    /// </summary>
    public int EnforcePermissions()
    {
        if (config.AllowAction && config.AllowChat)
            return 0;
        var error = config.AllowAction
            ? "Denied automatically: the Chat permission tier was disabled."
            : "Denied automatically: the Action permission tier was disabled, which closes the approval queue.";
        var denied = new List<Ticket>();
        var now = time.GetUtcNow();
        lock (gate)
        {
            for (var i = 0; i < tickets.Count; i++)
            {
                var t = tickets[i];
                // A Ui tool that asks for approval (the map flag, opening a window) does not depend on the Action tier.
                if (t.State != TicketState.Pending || t.Permission == ToolPermission.Ui || (config.AllowAction && t.Tier != ToolPermission.Chat))
                    continue;
                tickets[i] = t with { State = TicketState.Denied, DecidedBy = "system", DecidedAt = now, Error = error };
                denied.Add(tickets[i]);
            }
        }

        foreach (var t in denied)
            Record("tickets/deny", t, true, error);
        if (denied.Count > 0)
            AfterChange(null);
        return denied.Count;
    }

    /// <summary>Expires pending tickets past their client-set expiry and drops old final tickets. Call about once a second.</summary>
    public void Tick()
    {
        var now = time.GetUtcNow();
        var expired = new List<Ticket>();
        bool pruned;
        lock (gate)
        {
            for (var i = 0; i < tickets.Count; i++)
            {
                var t = tickets[i];
                if (t.State == TicketState.Pending && t.ExpiresAt is { } at && at <= now)
                {
                    tickets[i] = t with { State = TicketState.Expired, DecidedBy = "system", DecidedAt = now, Error = "Expired: the player did not decide before the expiry the client set." };
                    expired.Add(tickets[i]);
                }
            }

            pruned = PruneLocked(now);
        }

        foreach (var t in expired)
            Record("tickets/expire", t, true, null);
        if (expired.Count > 0 || pruned)
            AfterChange(expired.Count == 1 ? expired[0] : null);
    }

    /// <summary>Writes the queue to disk now (normally saves happen in the background after each change).</summary>
    public void Flush() => SaveNow();

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
        }

        runQueue.Writer.TryComplete();
        disposing.Cancel();
        try
        {
            worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Cancellation of a running ticket; it is saved as Approved and marked interrupted on the next load.
        }

        SaveNow(final: true);
        disposing.Dispose();
    }

    // ---- internals ----------------------------------------------------------------------------------

    private Ticket? Decide(string id, bool approve, string by, string? error)
    {
        Ticket? updated = null;
        lock (gate)
        {
            if (disposed)
                return null;
            var index = tickets.FindIndex(t => t.Id == id && t.State == TicketState.Pending);
            if (index >= 0)
            {
                updated = tickets[index] with
                {
                    State = approve ? TicketState.Approved : TicketState.Denied,
                    DecidedBy = by,
                    DecidedAt = time.GetUtcNow(),
                    Error = error,
                };
                tickets[index] = updated;
            }
        }

        if (updated == null)
            return null;
        Record(approve ? "tickets/approve" : "tickets/deny", updated, true, null);
        AfterChange(updated);
        if (approve)
            runQueue.Writer.TryWrite(updated.Id);
        return updated;
    }

    private int DecideAll(bool approve, string? owner)
    {
        string[] ids;
        lock (gate)
            ids = tickets.Where(t => t.State == TicketState.Pending && (owner == null || t.Owner == owner)).Select(t => t.Id).ToArray();
        return ids.Count(id => Decide(id, approve, "player", null) != null);
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var id in runQueue.Reader.ReadAllAsync(disposing.Token).ConfigureAwait(false))
                await RunOneAsync(id).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunOneAsync(string id)
    {
        var ticket = Get(id);
        if (ticket is not { State: TicketState.Approved })
            return;

        var started = Stopwatch.GetTimestamp();
        ToolExecutionResult? result = null;
        string? error = null;
        if (!config.AllowAction)
        {
            error = "Not run: the Action permission tier was disabled after approval.";
        }
        else
        {
            try
            {
                var arguments = ticket.ArgumentsJson is null ? null : JsonNode.Parse(ticket.ArgumentsJson) as JsonObject;
                result = await runner.ExecuteApprovedToolAsync(ticket.ToolName, arguments, ticket.ClientName, ticket.SessionId, ticket.AuthenticatedClient, disposing.Token).ConfigureAwait(false);
                error = result.IsError ? result.Error ?? "The tool reported an error." : null;
            }
            catch (OperationCanceledException) when (disposing.IsCancellationRequested)
            {
                return; // unloading: stays Approved, and the next load marks it interrupted
            }
            catch (Exception ex)
            {
                error = $"Not run: {ex.GetType().Name}: {ex.Message}";
            }
        }

        Ticket updated;
        lock (gate)
        {
            var index = tickets.FindIndex(t => t.Id == id);
            if (index < 0)
                return;
            updated = tickets[index] with
            {
                State = error == null ? TicketState.Executed : TicketState.Failed,
                CompletedAt = time.GetUtcNow(),
                ResultJson = result is null ? null : CapResult(result.Result),
                Error = error,
            };
            tickets[index] = updated;
        }

        Record("tickets/execute", updated, error == null, error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        AfterChange(updated);
    }

    private static string CapResult(JsonObject result)
    {
        var json = result.ToJsonString(McpJson.Options);
        if (json.Length <= MaxResultLength)
            return json;

        // Keep the shape clients parse (content + isError) and say what was cut.
        var text = result["content"]?[0]?["text"]?.GetValue<string>() ?? json;
        var capped = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text[..Math.Min(text.Length, MaxResultLength - 512)] + "…" }),
            ["truncated"] = true,
        };
        if (result["isError"] is { } isError)
            capped["isError"] = isError.DeepClone();
        return capped.ToJsonString(McpJson.Options);
    }

    /// <summary>Drops final tickets older than <see cref="FinishedRetention"/> and beyond <see cref="MaxFinishedKept"/>. Returns whether any went.</summary>
    private bool PruneLocked(DateTimeOffset now)
    {
        var removed = tickets.RemoveAll(t => t.IsFinal && (t.CompletedAt ?? t.DecidedAt ?? t.CreatedAt) + FinishedRetention <= now);
        var finished = tickets.Count(t => t.IsFinal);
        if (finished > MaxFinishedKept)
        {
            var drop = tickets.Where(t => t.IsFinal).OrderBy(t => t.CompletedAt ?? t.DecidedAt ?? t.CreatedAt).Take(finished - MaxFinishedKept).ToHashSet();
            removed += tickets.RemoveAll(drop.Contains);
        }

        return removed > 0;
    }

    private void Record(string method, Ticket ticket, bool success, string? error, double durationMs = 0)
    {
        try
        {
            ActivityRecorded?.Invoke(new TicketActivity(method, ticket, success, error, durationMs));
        }
        catch
        {
            // Activity sinks are logging plumbing.
        }
    }

    private void AfterChange(Ticket? ticket)
    {
        RequestSave();
        try
        {
            Changed?.Invoke(ticket);
        }
        catch
        {
            // UI and notification plumbing must not break the queue.
        }
    }

    private void RequestSave()
    {
        if (Interlocked.Exchange(ref saveQueued, 1) == 0)
            ThreadPool.QueueUserWorkItem(static self => self.SaveNow(), this, preferLocal: false);
    }

    private void SaveNow(bool final = false)
    {
        lock (saveGate)
        {
            // After the final save on unload, a late background save must not overwrite what a new instance wrote.
            if (saveClosed)
                return;
            saveClosed = final;
            Interlocked.Exchange(ref saveQueued, 0);
            store.Save(Snapshot());
        }
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return "t_" + Convert.ToHexStringLower(bytes);
    }

    public static string Name(TicketState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
}

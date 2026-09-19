using System.Text.Json.Nodes;
using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Providers.Meta;

/// <summary>A deferred approval ticket as MCP clients see it.</summary>
public sealed record TicketDto(
    string Id,
    string State,
    string Tool,
    string Tier,
    string Reason,
    string? ResumeToken,
    string? Client,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? CompletedAt,
    int? QueuePosition,
    JsonNode? Arguments,
    JsonNode? Result,
    string? Error,
    string Uri);

public sealed record TicketListDto(int Count, int Pending, DateTimeOffset? SessionActiveUntil, IReadOnlyList<TicketDto> Tickets);

/// <summary>
/// The deferred approval queue for MCP clients: request_action files an Action/Chat call as a ticket and returns at once;
/// the player decides in game whenever they like; clients poll get_ticket/list_tickets or subscribe to the ticket
/// resources, match their resume token and continue. Every tool here only sees the calling client's own tickets.
/// </summary>
[McpProvider("approvals")]
public sealed class TicketProvider : IDisposable
{
    public const string TicketsUri = "ffxiv://tickets";

    private readonly ApprovalQueue queue;
    private readonly ApprovalSessionService sessions;
    private readonly IMcpNotifier notifier;

    public TicketProvider(ApprovalQueue queue, ApprovalSessionService sessions, IMcpNotifier notifier)
    {
        this.queue = queue;
        this.sessions = sessions;
        this.notifier = notifier;
        queue.Changed += OnChanged;
    }

    public static string TicketUri(string id) => $"{TicketsUri}/{id}";

    [McpTool("request_action",
        Title = "Queue an Action/Chat call for the player to approve later",
        Description =
            "Files an Action- or Chat-tier tool call (e.g. teleport, execute_command, send_chat) as an approval ticket and returns " +
            "immediately with its id and state pending; nothing runs until the player approves it in the XivMcp window, which may be " +
            "minutes or hours later (tickets survive game restarts and never time out into a denial unless you set expiresInSeconds). " +
            "On approval the plugin runs the call with the normal validation and call timeout and stores the tool result on the ticket. " +
            "If the player has an approval session open for you, the ticket is approved at once (state approved, then executed). " +
            "Poll get_ticket / list_tickets, or subscribe to the ticket's uri (resources/subscribe) for notifications/resources/updated, " +
            "then match resumeToken and continue the steps that depended on it. Use tools/call directly when the player is at the " +
            "keyboard. At most 50 pending tickets per client. Arguments are shown to the player as sent.",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false,
        Idempotent = false)]
    public TicketDto RequestAction(
        [McpParam("Name of the Action/Chat tool to run, e.g. \"teleport\".")] string tool,
        [McpParam("One line shown to the player: why you want to do this. Max 300 chars.")] string reason,
        [McpParam("Arguments object for that tool, exactly as for tools/call.")] JsonObject? arguments = null,
        [McpParam("Opaque string returned on the ticket so you can match it to the step of your plan that waits on it. Max 512 chars.")] string? resumeToken = null,
        [McpParam("Optional: expire the ticket if the player has not decided within this many seconds (30 to 604800). Omit to wait indefinitely.", Minimum = ApprovalQueue.MinExpirySeconds, Maximum = ApprovalQueue.MaxExpirySeconds)] int? expiresInSeconds = null,
        ToolContext? ctx = null)
    {
        if (string.IsNullOrWhiteSpace(tool))
            throw new McpToolException("tool must not be empty.");
        var ticket = queue.Submit(new TicketRequest(tool.Trim(), arguments, reason, resumeToken, expiresInSeconds, ctx?.ClientName, ctx?.SessionId));
        return ToDto(ticket);
    }

    [McpTool("get_ticket",
        Title = "Get an approval ticket",
        Description =
            "Returns one of your approval tickets: state (pending, approved, executed, failed, denied, cancelled, expired), who decided, " +
            "your resumeToken, and once it ran the tool result (result, same shape as a tools/call result) or error. Final states are " +
            "executed, failed, denied, cancelled and expired. Finished tickets are kept for 7 days.",
        GameThread = false,
        RequiresLogin = false)]
    public TicketDto GetTicket(
        [McpParam("Ticket id from request_action, e.g. \"t_0123abcd4567ef89\".")] string id,
        ToolContext? ctx = null) => ToDto(Owned(id, ctx?.ClientName));

    [McpTool("list_tickets",
        Title = "List your approval tickets",
        Description =
            "Lists your approval tickets, oldest first. state filters: open (pending or approved, the default), pending, final, all. " +
            "Pass resumeToken to find the tickets you filed with it. sessionActiveUntil is set while the player has an approval session " +
            "open for you (Action calls, and Chat calls if they allowed it, then run without asking).",
        GameThread = false,
        RequiresLogin = false)]
    public TicketListDto ListTickets(
        [McpParam("Which tickets to return.", Enum = ["open", "pending", "final", "all"])] string state = "open",
        [McpParam("Only tickets filed with this resume token.")] string? resumeToken = null,
        ToolContext? ctx = null)
    {
        var filter = (state ?? "open").Trim().ToLowerInvariant();
        Func<Ticket, bool> keep = filter switch
        {
            "open" => t => !t.IsFinal,
            "pending" => t => t.State == TicketState.Pending,
            "final" => t => t.IsFinal,
            "all" => _ => true,
            _ => throw new McpToolException("state must be one of open, pending, final, all."),
        };
        return BuildList(ctx, t => keep(t) && (resumeToken == null || t.ResumeToken == resumeToken));
    }

    [McpTool("cancel_ticket",
        Title = "Cancel a pending approval ticket",
        Description = "Withdraws one of your pending tickets so the player is no longer asked about it. Approved or finished tickets cannot be cancelled.",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false)]
    public TicketDto CancelTicket(
        [McpParam("Ticket id from request_action.")] string id,
        ToolContext? ctx = null) => ToDto(queue.Cancel(id, ctx?.ClientName));

    [McpResource(TicketsUri,
        Name = "Approval tickets",
        Description = "Your approval tickets (same shape as list_tickets with state all). Subscribe for notifications/resources/updated on every change.",
        GameThread = false,
        RequiresLogin = false)]
    public TicketListDto ReadTickets(ToolContext? ctx = null) => BuildList(ctx, static _ => true);

    [McpResourceTemplate(TicketsUri + "/{id}",
        Name = "Approval ticket",
        Description = "One of your approval tickets (same shape as get_ticket). Subscribe to learn when it is decided or has run.",
        GameThread = false,
        RequiresLogin = false)]
    public TicketDto? ReadTicket(string id, ToolContext? ctx = null)
    {
        var ticket = queue.Get(id);
        return ticket != null && ticket.Owner == ApprovalQueue.OwnerOf(ctx?.ClientName) ? ToDto(ticket) : null;
    }

    private Ticket Owned(string id, string? clientName)
    {
        queue.Tick();
        var ticket = queue.Get(id);
        if (ticket == null || ticket.Owner != ApprovalQueue.OwnerOf(clientName))
            throw new McpToolException($"No ticket '{id}' from this client. Finished tickets are removed after 7 days or when the player clears them.");
        return ticket;
    }

    private TicketListDto BuildList(ToolContext? ctx, Func<Ticket, bool> keep)
    {
        queue.Tick();
        var owner = ApprovalQueue.OwnerOf(ctx?.ClientName);
        var mine = queue.Snapshot().Where(t => t.Owner == owner).ToArray();
        var items = mine.Where(keep).Select(ToDto).ToArray();
        var session = sessions.Active()
            .Where(s => s.ClientName == ctx?.ClientName && (s.SessionId == null || s.SessionId == ctx?.SessionId))
            .Select(s => (DateTimeOffset?)s.Expires)
            .Max();
        return new TicketListDto(items.Length, mine.Count(t => t.State == TicketState.Pending), session, items);
    }

    private TicketDto ToDto(Ticket t) => new(
        t.Id,
        ApprovalQueue.Name(t.State),
        t.ToolName,
        t.Tier == ToolPermission.Chat ? "chat" : "action",
        t.Reason,
        t.ResumeToken,
        t.ClientName,
        t.CreatedAt,
        t.ExpiresAt,
        t.DecidedBy,
        t.DecidedAt,
        t.CompletedAt,
        t.State == TicketState.Pending ? queue.PendingPosition(t.Id) : null,
        Parse(t.ArgumentsJson),
        Parse(t.ResultJson),
        t.Error,
        TicketUri(t.Id));

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(json);
        }
    }

    private void OnChanged(Ticket? ticket)
    {
        try
        {
            notifier.ResourceUpdated(TicketsUri);
            if (ticket != null)
            {
                notifier.ResourceUpdated(TicketUri(ticket.Id));
                return;
            }

            foreach (var t in queue.Snapshot())
                notifier.ResourceUpdated(TicketUri(t.Id));
        }
        catch
        {
            // Never throw from an event handler.
        }
    }

    public void Dispose() => queue.Changed -= OnChanged;
}

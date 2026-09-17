using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Providers.Meta;

/// <summary>Board entry as returned to MCP clients and the ffxiv://agents resource.</summary>
public sealed record AgentStatusDto(
    string Agent,
    string Status,
    string State,
    double? Progress,
    string? Detail,
    string? ClientName,
    DateTimeOffset UpdatedAt,
    DateTimeOffset CreatedAt,
    long AgeSeconds);

public sealed record AgentBoardDto(int Count, IReadOnlyList<AgentStatusDto> Agents);

public sealed record ClearStatusResult(int Removed, int Remaining);

/// <summary>
/// The agent board: lets AI agents show what they are doing inside the game (plugin window,
/// Umbra widget, optional notification on completion).
/// </summary>
[McpProvider("meta")]
public sealed class AgentBoardProvider : IDisposable
{
    public const string BoardUri = "ffxiv://agents";

    private readonly AgentBoard board;
    private readonly IMcpNotifier notifier;
    private readonly INotificationManager notifications;
    private readonly Configuration config;

    public AgentBoardProvider(AgentBoard board, IMcpNotifier notifier, INotificationManager notifications, Configuration config)
    {
        this.board = board;
        this.notifier = notifier;
        this.notifications = notifications;
        this.config = config;
        board.Changed += OnBoardChanged;
        board.Completed += OnCompleted;
    }

    [McpTool("post_status",
        Title = "Post agent status to the in-game board",
        Description =
            "Shows your progress inside the player's game: creates or replaces the board entry for `agent` " +
            "(one entry per agent name, case-insensitive) in the XivMcp window and Umbra toolbar widget. " +
            "Call it when you start a multi-step task (state running), update it as you go (optionally with progress 0..1), " +
            "and finish with state done or failed so the player sees the outcome. Use info for a one-off note. " +
            "Keep status to a short line; put longer context in detail. Returns the stored entry.",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false)]
    public AgentStatusDto PostStatus(
        [McpParam("Short stable name identifying you, e.g. \"claude-code\" or \"gear-audit\". Max 64 chars.")] string agent,
        [McpParam("One-line status shown on the board, e.g. \"Checking retainer inventories\". Max 200 chars.")] string status,
        [McpParam("Lifecycle state.", Enum = ["running", "done", "failed", "info"])] string state = "running",
        [McpParam("Completion fraction from 0 to 1; omit when unknown.", Minimum = 0, Maximum = 1)] double? progress = null,
        [McpParam("Optional longer detail shown on hover/expand. Max 2000 chars.")] string? detail = null,
        ToolContext? ctx = null)
    {
        if (string.IsNullOrWhiteSpace(agent))
            throw new McpToolException("agent must not be empty.");
        if (string.IsNullOrWhiteSpace(status))
            throw new McpToolException("status must not be empty.");

        AgentState parsed;
        try
        {
            parsed = AgentBoard.ParseState(state);
        }
        catch (ArgumentException ex)
        {
            throw new McpToolException(ex.Message);
        }

        return ToDto(board.Upsert(agent, status, parsed, progress, detail, ctx?.ClientName), DateTimeOffset.UtcNow);
    }

    [McpTool("list_status",
        Title = "List the in-game agent board",
        Description =
            "Returns every entry on the in-game agent board (newest update first) with agent, status, state " +
            "(running|done|failed|info), progress, detail, the MCP client that posted it and seconds since its last update (ageSeconds). " +
            "Use it to see what other agents are doing or to resume your own entry. Entries expire after the " +
            "player's configured idle time.",
        GameThread = false,
        RequiresLogin = false)]
    public AgentBoardDto ListStatus() => BuildBoard();

    [McpTool("clear_status",
        Title = "Clear agent board entries",
        Description =
            "Removes your entry (pass agent) or every entry (omit agent) from the in-game agent board. " +
            "Prefer posting state done/failed over clearing so the player sees the outcome; clear only stale entries. " +
            "Returns how many entries were removed and how many remain.",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false)]
    public ClearStatusResult ClearStatus(
        [McpParam("Agent name to remove; omit to clear the whole board.")] string? agent = null)
    {
        var removed = board.Clear(agent);
        return new ClearStatusResult(removed, board.Count);
    }

    [McpResource(BoardUri,
        Name = "Agent board",
        Description = "JSON snapshot of the in-game agent board (same shape as list_status). Subscribe for notifications/resources/updated on every change.",
        GameThread = false,
        RequiresLogin = false)]
    public AgentBoardDto ReadBoard() => BuildBoard();

    private AgentBoardDto BuildBoard()
    {
        var now = DateTimeOffset.UtcNow;
        var items = board.Snapshot().Select(p => ToDto(p, now)).ToArray();
        return new AgentBoardDto(items.Length, items);
    }

    private static AgentStatusDto ToDto(AgentPost post, DateTimeOffset now) => new(
        post.Agent,
        post.Status,
        post.StateName,
        post.Progress,
        post.Detail,
        post.ClientName,
        post.UpdatedAt,
        post.CreatedAt,
        (long)Math.Max(0, (now - post.UpdatedAt).TotalSeconds));

    private void OnBoardChanged()
    {
        try
        {
            notifier.ResourceUpdated(BoardUri);
        }
        catch
        {
            // Never throw from an event handler.
        }
    }

    private void OnCompleted(AgentPost post)
    {
        try
        {
            if (!config.NotifyAgentCompletion)
                return;
            notifications.AddNotification(new Notification
            {
                Title = $"{post.Agent}: {post.StateName}",
                Content = post.Status,
                Type = post.State == AgentState.Failed ? NotificationType.Error : NotificationType.Success,
                Minimized = false,
            });
        }
        catch
        {
            // Never throw from an event handler.
        }
    }

    public void Dispose()
    {
        board.Changed -= OnBoardChanged;
        board.Completed -= OnCompleted;
    }
}

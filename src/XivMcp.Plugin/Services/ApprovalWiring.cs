using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>
/// Connects the approval queue and approval sessions to the rest of the plugin: activity feed and /xllog entries (tool,
/// ticket id, client, outcome; never arguments), toasts when a session starts or ends, permission changes (revoke
/// sessions, deny what may no longer run) and the 1 Hz tick. Glue only; no decisions are made here.
/// </summary>
public sealed class ApprovalWiring : IDisposable
{
    private readonly ServerHost host;
    private readonly ApprovalQueue queue;
    private readonly ApprovalSessionService sessions;
    private readonly INotificationManager notifications;
    private readonly IPluginLog log;

    public ApprovalWiring(ServerHost host, ApprovalQueue queue, ApprovalSessionService sessions, INotificationManager notifications, IPluginLog log)
    {
        this.host = host;
        this.queue = queue;
        this.sessions = sessions;
        this.notifications = notifications;
        this.log = log;
        queue.ActivityRecorded += OnTicketActivity;
        sessions.Started += OnSessionStarted;
        sessions.Ended += OnSessionEnded;
        host.PermissionsChanged += OnPermissionsChanged;
    }

    /// <summary>Call about once a second from the framework update.</summary>
    public void Tick()
    {
        sessions.Tick();
        queue.Tick();
    }

    private void OnTicketActivity(TicketActivity a)
    {
        var t = a.Ticket;
        host.Server.RecordHostActivity(t.SessionId, t.ClientName, a.Method, t.ActivityTarget, a.Success, a.Error, a.DurationMs);
        if (a.Method == "tickets/execute")
        {
            if (a.Success)
                log.Information("MCP ticket {Target} from {Client} executed (approved by {By})", t.ActivityTarget, t.ClientName ?? "?", t.DecidedBy ?? "?");
            else
                log.Warning("MCP ticket {Target} from {Client} failed: {Error}", t.ActivityTarget, t.ClientName ?? "?", a.Error ?? "");
        }
    }

    private void OnSessionStarted(ApprovalSession s)
    {
        var target = $"{s.Describe()} for {(s.Expires - s.StartedAt).TotalMinutes:0} min";
        host.Server.RecordHostActivity(s.SessionId, s.ClientName, "sessions/start", target, true, null);
        log.Information("MCP approval session started: {Target}", target);
        Toast("Approval session started", $"{s.Describe()}: Action{(s.IncludeChat ? " and Chat" : "")} calls run without asking for {(s.Expires - s.StartedAt).TotalMinutes:0} min.", NotificationType.Warning);
    }

    private void OnSessionEnded(ApprovalSession s, ApprovalSessionEnd reason)
    {
        var why = reason switch
        {
            ApprovalSessionEnd.Expired => "expired",
            ApprovalSessionEnd.Revoked => "revoked",
            ApprovalSessionEnd.Replaced => "replaced by a new session",
            ApprovalSessionEnd.PermissionsChanged => "permissions changed",
            _ => "plugin unloaded",
        };
        host.Server.RecordHostActivity(s.SessionId, s.ClientName, "sessions/end", s.Describe(), true, why);
        log.Information("MCP approval session ended ({Why}): {Target}", why, s.Describe());
        if (reason is not (ApprovalSessionEnd.Unloaded or ApprovalSessionEnd.Replaced))
            Toast("Approval session ended", $"{s.Describe()} ({why}). Action/Chat calls ask again.", NotificationType.Info);
    }

    private void OnPermissionsChanged()
    {
        // A session was granted under the old settings; ask again under the new ones, like the per-tool grants.
        sessions.RevokeAll(ApprovalSessionEnd.PermissionsChanged);
        queue.EnforcePermissions();
    }

    private void Toast(string title, string content, NotificationType type)
    {
        try
        {
            notifications.AddNotification(new Notification { Title = title, Content = content, Type = type, Minimized = false });
        }
        catch (Exception ex)
        {
            log.Debug(ex, "approval toast failed");
        }
    }

    public void Dispose()
    {
        host.PermissionsChanged -= OnPermissionsChanged;
        sessions.Started -= OnSessionStarted;
        sessions.Ended -= OnSessionEnded;
        queue.ActivityRecorded -= OnTicketActivity;
    }
}

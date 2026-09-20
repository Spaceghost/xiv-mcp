using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

/// <summary>Approvals tab, approval-session banner and dialog. Drawing only; the logic lives in ApprovalQueue/ApprovalSessionService.</summary>
public sealed partial class MainWindow
{
    public const string ApprovalsTab = "Approvals";
    private const string SessionPopup = "Allow everything from this client?##approval-session";
    private const int FinishedShown = 50;

    // Session dialog draft.
    private string? sessionDraftClient;
    private string? sessionDraftSessionId;
    private bool sessionDraftChat;
    private bool openSessionPopup;

    /// <summary>Set by the plugin after construction (keeps the constructor unchanged).</summary>
    public ApprovalQueue? Approvals { get; set; }

    public ApprovalSessionService? ApprovalSessions { get; set; }

    /// <summary>The record of every state-changing call that ran, asked or not.</summary>
    public ActionLog? Actions { get; set; }

    private string ApprovalsTabLabel => Approvals is { PendingCount: > 0 and var n } ? $"{ApprovalsTab} ({n})###{ApprovalsTab}" : $"{ApprovalsTab}###{ApprovalsTab}";

    /// <summary>Countdown banner for every running approval session, with one-click revoke. Drawn in the header.</summary>
    private void DrawApprovalSessionBanner()
    {
        if (ApprovalSessions is not { } sessions)
            return;
        var now = DateTimeOffset.UtcNow;
        foreach (var session in sessions.Active())
        {
            var left = session.Remaining(now);
            ImGui.TextColored(session.IncludeChat ? ImGuiColors.DalamudRed : ImGuiColors.DalamudOrange,
                $"Approval session: {session.Describe()} runs Action{(session.IncludeChat ? " and Chat" : "")} calls without asking — {(int)left.TotalMinutes}:{left.Seconds:00} left");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Revoke##session-{session.Id}"))
                sessions.Revoke(session.Id);
        }
    }

    private void DrawApprovalsTab()
    {
        if (Approvals is not { } queue)
        {
            ImGui.TextDisabled("The approval queue is not available.");
            return;
        }

        if (!config.AllowAction)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "The Action tier is off, so the queue is closed: new requests are refused and waiting ones were denied.");
        else if (!config.ConfirmActions)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Confirmation is off: queued Action/Chat calls are approved and run as soon as they arrive.");

        var tickets = queue.Snapshot();
        var pending = tickets.Where(t => t.State == TicketState.Pending).ToArray();
        var now = DateTimeOffset.UtcNow;

        ImGui.BeginDisabled(pending.Length == 0);
        if (ImGui.Button($"Approve all ({pending.Length})"))
            queue.ApproveAll();
        ImGui.SameLine();
        if (ImGui.Button("Deny all"))
            queue.DenyAll();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Clear finished"))
            queue.ClearFinished();
        HelpMarker("Approved tickets run one at a time, oldest approval first, with the normal checks and call timeout. " +
                   "Tickets wait until you decide; they survive reloads and game restarts. A ticket that was running when the game closed is marked failed, never re-run.");

        ImGui.Spacing();
        if (pending.Length == 0)
        {
            ImGui.TextDisabled("Nothing is waiting for you.");
            ImGui.TextWrapped("Agents call request_action to queue Action/Chat calls here while you are away.");
        }

        foreach (var ticket in pending)
        {
            ImGui.PushID(ticket.Id);
            try
            {
                DrawPendingTicket(queue, ticket, now);
            }
            finally
            {
                ImGui.PopID();
            }
        }

        DrawSessionPopup();
        DrawActionLog(now);

        var active = tickets.Where(t => t.State == TicketState.Approved).ToArray();
        var finished = tickets.Where(t => t.IsFinal).OrderByDescending(t => t.CompletedAt ?? t.DecidedAt ?? t.CreatedAt).Take(FinishedShown).ToArray();
        if (active.Length + finished.Length == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Recent");
        ImGui.Separator();
        foreach (var ticket in active.Concat(finished))
            DrawTicketLine(ticket, now);
    }

    /// <summary>The newest lines of the action log: every state-changing call that ran, whether or not the player was asked.</summary>
    private void DrawActionLog(DateTimeOffset now)
    {
        if (Actions is not { } actions)
            return;
        var entries = actions.Recent();
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader($"Action log ({entries.Count})###actionlog"))
            return;
        ImGui.TextDisabled(actions.Path is { } path ? $"Everything that changed something, asked or not. Also written to {System.IO.Path.GetFileName(path)} in this plugin's config folder." : "Everything that changed something, asked or not.");
        foreach (var entry in entries.Take(50))
        {
            ImGui.TextColored(entry.Success ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudRed, $"{FormatAge(now - entry.Time)} ago");
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, entry.Tool);
            ImGui.SameLine();
            ImGui.TextDisabled($"[{entry.Tier}, approval {entry.Approval}] {entry.Token ?? entry.Client ?? "(unnamed client)"}");
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(ConfirmationService.ShowInvisible(entry.Error is { } error ? $"  {entry.Summary} - {error}" : "  " + entry.Summary));
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawPendingTicket(ApprovalQueue queue, Ticket ticket, DateTimeOffset now)
    {
        var chat = ticket.Tier == ToolPermission.Chat;
        ImGui.Separator();
        ImGui.TextColored(TierColor(ticket.Tier), chat ? "CHAT" : "ACTION");
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudYellow, ticket.ToolName);
        ImGui.SameLine();
        ImGui.TextDisabled($"from {ticket.ClientName ?? "(unnamed client)"} · {FormatAge(now - ticket.CreatedAt)} ago{(ticket.ExpiresAt is { } at ? $" · expires in {FormatAge(at - now)}" : "")}");
        ImGui.PushTextWrapPos(0);
        if (ticket.Summary is { Length: > 0 } summary)
            ImGui.TextUnformatted(summary);
        ImGui.TextUnformatted(ConfirmationService.ShowInvisible(ticket.Reason));
        ImGui.PopTextWrapPos();

        var (arguments, truncated) = ConfirmationService.FormatArguments(ticket.ArgumentsJson);
        if (arguments != null && ImGui.TreeNode("Arguments"))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(arguments);
            ImGui.PopTextWrapPos();
            if (truncated)
                ImGui.TextColored(ImGuiColors.DalamudOrange, $"Truncated to {ConfirmationService.MaxArgumentsLength} characters for display.");
            ImGui.TreePop();
        }
        else if (arguments == null)
        {
            ImGui.TextDisabled("No arguments.");
        }

        ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.HealerGreen with { W = 0.6f });
        if (ImGui.Button("Approve", new Vector2(100, 0)))
            queue.Approve(ticket.Id);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed with { W = 0.6f });
        if (ImGui.Button("Deny", new Vector2(100, 0)))
            queue.Deny(ticket.Id);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.Button($"Allow everything from this client for {ApprovalSessions?.Duration.TotalMinutes ?? ApprovalSessionService.DefaultMinutes:0} min…"))
        {
            sessionDraftClient = ticket.ClientName;
            sessionDraftSessionId = ticket.SessionId;
            sessionDraftChat = false;
            openSessionPopup = true;
        }
    }

    private void DrawSessionPopup()
    {
        if (openSessionPopup)
        {
            ImGui.OpenPopup(SessionPopup);
            openSessionPopup = false;
        }

        if (!ImGui.BeginPopupModal(SessionPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var minutes = ApprovalSessions?.Duration.TotalMinutes ?? ApprovalSessionService.DefaultMinutes;
        ImGui.TextUnformatted($"For {minutes:0} minutes, run Action calls from");
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"  {sessionDraftClient ?? "(unnamed client)"}");
        ImGui.TextUnformatted("without asking (direct calls and queued tickets).");
        ImGui.TextDisabled("The client name is reported by the client itself. Tickets already waiting stay waiting.");
        ImGui.Checkbox("Also allow Chat: text OTHER PLAYERS can see", ref sessionDraftChat);
        if (sessionDraftChat)
            ImGui.TextColored(ImGuiColors.DalamudRed, "Chat-tier calls will post without asking you.");
        if (ImGui.Button("Start session"))
        {
            ApprovalSessions?.Start(sessionDraftClient, sessionDraftSessionId, sessionDraftChat);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##session"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private static void DrawTicketLine(Ticket ticket, DateTimeOffset now)
    {
        var color = ticket.State switch
        {
            TicketState.Executed => ImGuiColors.HealerGreen,
            TicketState.Failed => ImGuiColors.DalamudRed,
            TicketState.Approved => ImGuiColors.TankBlue,
            _ => ImGuiColors.DalamudGrey,
        };
        ImGui.TextColored(color, ApprovalQueue.Name(ticket.State));
        ImGui.SameLine(90);
        ImGui.TextUnformatted(ticket.ToolName);
        ImGui.SameLine();
        ImGui.TextDisabled($"{ticket.ClientName ?? "(unnamed)"} · {ticket.DecidedBy ?? "?"} · {FormatAge(now - (ticket.CompletedAt ?? ticket.DecidedAt ?? ticket.CreatedAt))} ago");
        if (ImGui.IsItemHovered())
            Tooltip($"{ticket.Reason}{(ticket.Error is { } e ? "\n\n" + e : "")}");
    }

    /// <summary>Settings: approval session length.</summary>
    private void DrawApprovalSettings()
    {
        var minutes = config.ApprovalSessionMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Approval session length (min)", ref minutes, 1, 5))
            config.ApprovalSessionMinutes = Math.Clamp(minutes, ApprovalSessionService.MinMinutes, ApprovalSessionService.MaxMinutes);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(apply: false);
        HelpMarker("1-60, default 5. Length of \"Allow everything from this client\" sessions started from the Approvals tab. Sessions are never saved: a reload or restart ends them.");
    }
}

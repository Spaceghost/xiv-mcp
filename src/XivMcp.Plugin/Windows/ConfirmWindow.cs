using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

/// <summary>
/// Allow/Deny prompt for Action and Chat calls. Always "open" but only drawn while a request is pending; resolving
/// happens here on the framework thread, which completes the server-side await.
/// </summary>
public sealed class ConfirmWindow : Window
{
    private readonly ConfirmationService confirmations;

    public ConfirmWindow(ConfirmationService confirmations)
        : base("XivMcp — approve tool call###XivMcpConfirm", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.confirmations = confirmations;
        IsOpen = true;
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        PositionCondition = ImGuiCond.Appearing;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 0), MaximumSize = new Vector2(720, 900) };
    }

    public override bool DrawConditions() => confirmations.HasPending;

    public override void PreDraw()
    {
        IsOpen = true;
        var viewport = ImGui.GetMainViewport();
        Position = viewport.Pos + (viewport.Size / 2) - new Vector2(220, 160);
    }

    public override void OnClose() => IsOpen = true;

    public override void Draw()
    {
        var pending = confirmations.Snapshot();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < pending.Count; i++)
        {
            var request = pending[i];
            ImGui.PushID(request.Id.ToString());
            try
            {
                if (i > 0)
                    ImGui.Separator();
                DrawRequest(request, now);
            }
            finally
            {
                ImGui.PopID();
            }
        }

        if (pending.Count > 1)
        {
            ImGui.Separator();
            if (ImGui.Button("Deny all"))
                confirmations.DenyAll();
        }
    }

    private void DrawRequest(PendingConfirmation request, DateTimeOffset now)
    {
        var chat = request.Tier == ToolPermission.Chat;
        ImGui.TextColored(chat ? ImGuiColors.DalamudRed : ImGuiColors.DalamudOrange, chat ? "CHAT — other players will see this" : "ACTION — changes your game client");

        if (request.Summary is { Length: > 0 } summary)
        {
            ImGui.PushTextWrapPos(640);
            ImGui.TextUnformatted(summary);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
        }

        if (ImGui.BeginTable("##request", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
            Row("Tool", request.ToolName, ImGuiColors.DalamudYellow);
            Row("Client", request.ClientName ?? "(unnamed client)", null);
            if (request.AuthenticatedClient is { } tokenClient)
                Row("Token", tokenClient, ImGuiColors.ParsedBlue);
            if (request.Tier != request.Permission)
                Row("Tier", $"{request.Tier} (declared {request.Permission})", null);
            ImGui.EndTable();
        }

        ImGui.TextDisabled("The client name is reported by the client itself.");

        if (request.Arguments is { } arguments)
        {
            ImGui.TextUnformatted("Arguments");
            var lines = Math.Min(arguments.Count(c => c == '\n') + 1, 14);
            var height = (ImGui.GetTextLineHeightWithSpacing() * lines) + (ImGui.GetStyle().WindowPadding.Y * 2);
            if (ImGui.BeginChild("##arguments", new Vector2(640, height), true))
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(arguments);
                ImGui.PopTextWrapPos();
            }

            ImGui.EndChild();
            if (request.ArgumentsTruncated)
                ImGui.TextColored(ImGuiColors.DalamudOrange, $"Arguments truncated to {ConfirmationService.MaxArgumentsLength} characters for display.");
        }
        else
        {
            ImGui.TextDisabled("No arguments.");
        }

        var remaining = request.Deadline - now;
        var total = request.Deadline - request.CreatedAt;
        var fraction = total.TotalSeconds <= 0 ? 0f : (float)Math.Clamp(remaining.TotalSeconds / total.TotalSeconds, 0, 1);
        ImGui.ProgressBar(fraction, new Vector2(-1, ImGui.GetTextLineHeight()), $"denied automatically in {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))} s");

        ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.HealerGreen with { W = 0.6f });
        try
        {
            if (ImGui.Button("Allow", new Vector2(110, 0)))
                confirmations.Resolve(request.Id, ConfirmationDecision.Allow);
        }
        finally
        {
            ImGui.PopStyleColor();
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed with { W = 0.6f });
        try
        {
            if (ImGui.Button("Deny", new Vector2(110, 0)))
                confirmations.Resolve(request.Id, ConfirmationDecision.Deny);
        }
        finally
        {
            ImGui.PopStyleColor();
        }

        ImGui.SameLine();
        if (ImGui.Button($"Allow this tool for {ConfirmationService.GrantDuration.TotalMinutes:0} min"))
            confirmations.Resolve(request.Id, ConfirmationDecision.AllowForAWhile);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Runs this call, and further {request.ToolName} calls from this client{(chat ? " that post chat" : "")} without asking, for {ConfirmationService.GrantDuration.TotalMinutes:0} minutes. " +
                "Changing permissions revokes it; Settings lists active grants.");
        }
    }

    private static void Row(string label, string value, Vector4? color)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        if (color is { } c)
            ImGui.TextColored(c, value);
        else
            ImGui.TextUnformatted(value);
    }
}

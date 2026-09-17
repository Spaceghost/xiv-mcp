using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

/// <summary>
/// Approve/Deny prompt for Action and Chat calls. Always "open" but only drawn while a request is
/// pending; resolving happens here on the framework thread, which completes the server-side await.
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
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 0), MaximumSize = new Vector2(640, 800) };
    }

    public override bool DrawConditions() => confirmations.HasPending;

    public override void PreDraw()
    {
        IsOpen = true;
        var viewport = ImGui.GetMainViewport();
        Position = viewport.Pos + viewport.Size / 2 - new Vector2(200, 120);
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
            if (i > 0)
                ImGui.Separator();

            var tierColor = request.Permission == ToolPermission.Chat ? ImGuiColors.DalamudRed : ImGuiColors.DalamudOrange;
            ImGui.TextColored(tierColor, request.Permission == ToolPermission.Chat ? "CHAT — other players will see this" : "ACTION — changes your client");
            ImGui.TextUnformatted($"{request.ClientName ?? "An MCP client"} wants to call");
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, request.ToolName);

            if (!string.IsNullOrWhiteSpace(request.Summary))
            {
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 36f);
                ImGui.TextDisabled(request.Summary);
                ImGui.PopTextWrapPos();
            }

            var remaining = request.Deadline - now;
            var total = request.Deadline - request.CreatedAt;
            var fraction = total.TotalSeconds <= 0 ? 0f : (float)Math.Clamp(remaining.TotalSeconds / total.TotalSeconds, 0, 1);
            ImGui.ProgressBar(fraction, new Vector2(-1, ImGui.GetTextLineHeight()), $"auto-deny in {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))}s");

            ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.HealerGreen with { W = 0.6f });
            if (ImGui.Button("Approve", new Vector2(120, 0)))
                confirmations.Resolve(request.Id, true);
            ImGui.PopStyleColor();

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed with { W = 0.6f });
            if (ImGui.Button("Deny", new Vector2(120, 0)))
                confirmations.Resolve(request.Id, false);
            ImGui.PopStyleColor();

            ImGui.PopID();
        }

        if (pending.Count > 1)
        {
            ImGui.Separator();
            if (ImGui.Button("Deny all"))
                confirmations.DenyAll();
        }
    }
}

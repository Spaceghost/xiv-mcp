using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

/// <summary>Main window: Status, Agents, Activity, Tools and Settings tabs.</summary>
public sealed partial class MainWindow : Window
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration config;
    private readonly ServerHost host;
    private readonly AgentBoard board;
    private readonly ConfirmationService confirmations;

    private string? requestedTab;

    public MainWindow(IDalamudPluginInterface pluginInterface, Configuration config, ServerHost host, AgentBoard board, ConfirmationService confirmations)
        : base("XivMcp###XivMcpMain")
    {
        this.pluginInterface = pluginInterface;
        this.config = config;
        this.host = host;
        this.board = board;
        this.confirmations = confirmations;

        Size = new Vector2(720, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 360), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        ResetSettingsDraft();
    }

    public const string StatusTab = "Status";
    public const string SettingsTab = "Settings";

    /// <summary>Opens the window on a specific tab.</summary>
    public void OpenTab(string tab)
    {
        requestedTab = tab;
        IsOpen = true;
        BringToFront();
    }

    public override void OnOpen() => ResetSettingsDraft();

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##xivmcp-tabs"))
            return;

        DrawTab(StatusTab, DrawStatusTab);
        DrawTab($"Agents ({board.Count})###Agents", DrawAgentsTab);
        DrawTab(ApprovalsTabLabel, DrawApprovalsTab);
        DrawTab("Activity", DrawActivityTab);
        DrawTab("Tools", DrawToolsTab);
        DrawTab(SettingsTab, DrawSettingsTab);
        requestedTab = null;

        ImGui.EndTabBar();
    }

    private void DrawTab(string label, Action body)
    {
        var id = label.Contains("###") ? label[(label.IndexOf("###", StringComparison.Ordinal) + 3)..] : label;
        var flags = requestedTab == id ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(label, flags))
            return;
        try
        {
            try
            {
                if (ImGui.BeginChild($"##tab-{id}", Vector2.Zero, false))
                    body();
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        finally
        {
            ImGui.EndTabItem();
        }
    }

    private void DrawHeader()
    {
        if (host.IsTransitioning)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "● Working…");
        else if (host.IsRunning)
            ImGui.TextColored(ImGuiColors.HealerGreen, $"● Running  {host.Endpoint}");
        else
            ImGui.TextColored(ImGuiColors.DalamudGrey, "○ Stopped");

        ImGui.SameLine();
        ImGui.TextDisabled($"  sessions {host.Status.ActiveSessions} · requests {host.Status.TotalRequests} · failed {host.Status.FailedRequests}");

        if (!config.HostIsLoopback)
            ImGui.TextColored(ImGuiColors.DalamudRed, $"WARNING: listening host {config.Host} is not loopback — other machines can reach this server.");

        if (host.LastError is { } error)
            ImGui.TextColored(ImGuiColors.DalamudRed, error);

        if (confirmations.HasPending)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "A tool call is waiting for your approval (see the confirmation window).");

        DrawApprovalSessionBanner();
    }

    // ---- shared helpers ------------------------------------------------------------------------

    private void SaveConfig(bool apply = true)
    {
        config.Normalize();
        pluginInterface.SavePluginConfig(config);
        if (apply)
            _ = host.ApplyConfigAsync();
    }

    private static Vector4 TierColor(ToolPermission tier) => tier switch
    {
        ToolPermission.Read => ImGuiColors.ParsedBlue,
        ToolPermission.Ui => ImGuiColors.HealerGreen,
        ToolPermission.Action => ImGuiColors.DalamudOrange,
        ToolPermission.Chat => ImGuiColors.DalamudRed,
        _ => ImGuiColors.DalamudGrey,
    };

    private static Vector4 StateColor(AgentState state) => state switch
    {
        AgentState.Running => ImGuiColors.TankBlue,
        AgentState.Done => ImGuiColors.HealerGreen,
        AgentState.Failed => ImGuiColors.DalamudRed,
        _ => ImGuiColors.DalamudGrey,
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalSeconds < 60)
            return $"{(int)age.TotalSeconds}s";
        if (age.TotalMinutes < 60)
            return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24)
            return $"{(int)age.TotalHours}h {age.Minutes}m";
        return $"{(int)age.TotalDays}d";
    }

    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            Tooltip(text);
    }

    private static void Tooltip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Core;

namespace XivMcp.Plugin.Windows;

public sealed partial class MainWindow
{
    private static readonly string[] LogLevelNames = Enum.GetNames<ActivityLogLevel>();

    private string draftHost = "";
    private int draftPort;
    private string draftOrigins = "";
    private int draftCallTimeout;
    private bool regenerateArmed;

    private void ResetSettingsDraft()
    {
        draftHost = config.Host;
        draftPort = config.Port;
        draftOrigins = string.Join('\n', config.AllowedOrigins);
        draftCallTimeout = config.CallTimeoutSeconds;
        regenerateArmed = false;
    }

    private bool EndpointDraftDirty =>
        draftHost.Trim() != config.Host
        || draftPort != config.Port
        || draftCallTimeout != config.CallTimeoutSeconds
        || !ParseOrigins(draftOrigins).SequenceEqual(config.AllowedOrigins);

    private static List<string> ParseOrigins(string text) =>
        text.Split(['\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void DrawSettingsTab()
    {
        DrawServerSettings();
        ImGui.Spacing();
        DrawPermissionSettings();
        ImGui.Spacing();
        DrawCategorySettings();
        ImGui.Spacing();
        DrawInterfaceSettings();
    }

    private void DrawServerSettings()
    {
        if (!ImGui.CollapsingHeader("Server", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Start server when the plugin loads", ref enabled))
        {
            config.Enabled = enabled;
            SaveConfig(apply: false);
        }

        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Host", ref draftHost, 64);
        HelpMarker("127.0.0.1 keeps the server reachable only from this machine. Under Wine, 127.0.0.1 in game is the host's loopback.");
        if (!Configuration.IsLoopbackHost(draftHost.Trim()))
            ImGui.TextColored(ImGuiColors.DalamudRed, "NOT LOOPBACK: other devices on your network could reach the game through this server. The bearer token is then mandatory.");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Port", ref draftPort);
        draftPort = Math.Clamp(draftPort, 1, 65535);

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Call timeout (s)", ref draftCallTimeout);
        draftCallTimeout = Math.Clamp(draftCallTimeout, 5, 600);
        HelpMarker("Upper bound for one tool/resource/prompt call.");

        ImGui.TextUnformatted("Allowed browser origins (one per line)");
        HelpMarker("Only matters for requests that carry an Origin header (browsers). Loopback origins are always accepted. Leave empty unless a web client needs access.");
        ImGui.InputTextMultiline("##origins", ref draftOrigins, 2048, new System.Numerics.Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 3.5f));

        ImGui.BeginDisabled(!EndpointDraftDirty);
        if (ImGui.Button(host.IsRunning ? "Apply and restart" : "Apply"))
        {
            config.Host = draftHost.Trim();
            config.Port = draftPort;
            config.CallTimeoutSeconds = draftCallTimeout;
            config.AllowedOrigins = ParseOrigins(draftOrigins);
            SaveConfig();
            ResetSettingsDraft();
        }

        ImGui.SameLine();
        if (ImGui.Button("Revert"))
            ResetSettingsDraft();
        ImGui.EndDisabled();

        ImGui.Spacing();
        var requireToken = config.RequireToken;
        if (ImGui.Checkbox("Require bearer token", ref requireToken))
        {
            config.RequireToken = requireToken;
            SaveConfig();
        }

        if (!config.RequireToken)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Without a token, any local program can call every enabled tool.");

        if (!regenerateArmed)
        {
            if (ImGui.Button("Regenerate token…"))
                regenerateArmed = true;
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Connected clients with a stored token will be rejected until updated.");
            if (ImGui.Button("Confirm regenerate"))
            {
                config.BearerToken = Configuration.GenerateToken();
                SaveConfig();
                regenerateArmed = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##regen"))
                regenerateArmed = false;
        }
    }

    private void DrawPermissionSettings()
    {
        if (!ImGui.CollapsingHeader("Permissions", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        TierCheckbox(ToolPermission.Read, "Observe game state and static game data.");
        TierCheckbox(ToolPermission.Ui, "Local-only visible effects: echo, toasts, map flags, opening windows, agent board.");
        TierCheckbox(ToolPermission.Action, "Changes your client: targeting, gearsets, teleport, slash commands.");
        TierCheckbox(ToolPermission.Chat, "Sends text OTHER PLAYERS can see (say, party, tells, FC...).");

        ImGui.Spacing();
        var confirm = config.ConfirmActions;
        if (ImGui.Checkbox("Ask me before every Action/Chat call", ref confirm))
        {
            config.ConfirmActions = confirm;
            SaveConfig();
        }

        var timeout = config.ConfirmTimeoutSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Auto-deny after (s)", ref timeout, 5, 120))
            config.ConfirmTimeoutSeconds = timeout;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig();

        if (!config.ConfirmActions && (config.AllowAction || config.AllowChat))
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Action/Chat calls run without asking you.");

        var grants = host.Confirmations.Grants();
        if (grants.Count > 0)
        {
            ImGui.TextUnformatted($"Temporary approvals ({grants.Count}):");
            var now = DateTimeOffset.UtcNow;
            foreach (var grant in grants)
                ImGui.TextDisabled($"  {grant.ToolName} [{grant.Tier}] from {grant.ClientName ?? "(unnamed client)"} — {Math.Max(0, (int)Math.Ceiling((grant.Expires - now).TotalMinutes))} min left");
            if (ImGui.SmallButton("Revoke all##grants"))
                host.Confirmations.RevokeGrants();
        }
    }

    private void TierCheckbox(ToolPermission tier, string description)
    {
        var on = config.IsPermitted(tier);
        ImGui.PushStyleColor(ImGuiCol.Text, TierColor(tier));
        var changed = ImGui.Checkbox($"{tier}##tier", ref on);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled(description);
        if (!changed)
            return;
        config.SetPermitted(tier, on);
        SaveConfig();
    }

    private void DrawCategorySettings()
    {
        if (!ImGui.CollapsingHeader("Categories"))
            return;

        var categories = host.Categories;
        if (categories.Count == 0)
        {
            ImGui.TextDisabled("No providers loaded.");
            return;
        }

        foreach (var category in categories)
        {
            var on = config.IsCategoryEnabled(category);
            if (ImGui.Checkbox($"{category}##cat", ref on))
            {
                config.SetCategoryEnabled(category, on);
                SaveConfig();
            }
        }
    }

    private void DrawInterfaceSettings()
    {
        if (!ImGui.CollapsingHeader("Interface and logging"))
            return;

        var dtr = config.ShowDtrEntry;
        if (ImGui.Checkbox("Show \"MCP ● n\" in the server info bar", ref dtr))
        {
            config.ShowDtrEntry = dtr;
            SaveConfig(apply: false);
        }

        var notify = config.NotifyAgentCompletion;
        if (ImGui.Checkbox("Notify when an agent posts done/failed", ref notify))
        {
            config.NotifyAgentCompletion = notify;
            SaveConfig(apply: false);
        }

        var expiry = config.AgentBoardExpiryMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Board idle expiry (min, 0 = never)", ref expiry))
            config.AgentBoardExpiryMinutes = Math.Clamp(expiry, 0, 7 * 24 * 60);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(apply: false);

        var chatBuffer = config.ChatBufferSize;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Chat buffer lines", ref chatBuffer, 50, 500))
            config.ChatBufferSize = Math.Clamp(chatBuffer, 50, 5000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(apply: false);
        HelpMarker("How many recent chat lines read_chat can return. May need a plugin reload to take effect.");

        var level = (int)config.ActivityLogLevel;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Dalamud log verbosity", ref level, LogLevelNames))
        {
            config.ActivityLogLevel = (ActivityLogLevel)level;
            SaveConfig(apply: false);
        }

        HelpMarker("What each handled request writes to /xllog. Tool arguments and the token are never logged. The Activity tab is not affected.");
    }
}

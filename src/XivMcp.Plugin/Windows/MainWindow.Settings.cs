using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using XivMcp.Core;

namespace XivMcp.Plugin.Windows;

public sealed partial class MainWindow
{
    private static readonly string[] LogLevelNames = Enum.GetNames<ActivityLogLevel>();

    // Bind mode, address, port, path and origins restart the server, so they are edited as a draft
    // and applied explicitly. Applying restarts the listener in place: no plugin reload is needed.
    private BindMode draftMode;
    private string draftCustomHost = "";
    private string draftPath = "";
    private int draftPort;
    private string draftOrigins = "";
    private bool regenerateArmed;

    private void ResetSettingsDraft()
    {
        draftMode = config.BindMode;
        draftCustomHost = config.CustomHost;
        draftPath = config.Path;
        draftPort = config.Port;
        draftOrigins = string.Join('\n', config.AllowedOrigins);
        regenerateArmed = false;
    }

    private bool EndpointDraftDirty =>
        draftMode != config.BindMode
        || draftCustomHost.Trim() != config.CustomHost
        || NormalizePath(draftPath) != config.Path
        || draftPort != config.Port
        || !ParseOrigins(draftOrigins).SequenceEqual(config.AllowedOrigins);

    /// <summary>Same normalisation the configuration applies, so the draft is not "dirty" over a trailing slash.</summary>
    private static string NormalizePath(string text)
    {
        var p = text.Trim();
        if (p.Length == 0)
            return Configuration.DefaultPath;
        if (!p.StartsWith('/'))
            p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    private static List<string> ParseOrigins(string text) =>
        text.Split(['\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void DrawSettingsTab()
    {
        DrawProvisionBanner();
        DrawMainSettings();
        ImGui.Spacing();
        DrawCategorySettings();
        ImGui.Spacing();
        DrawPolicySettings();
        ImGui.Spacing();
        DrawLocalModelSettings();
        ImGui.Spacing();
        DrawAdvancedSettings();
    }

    // ---- main ----------------------------------------------------------------------------------

    private void DrawMainSettings()
    {
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Server");
        ImGui.Separator();

        var enabled = config.Enabled;
        ImGui.BeginDisabled(config.IsProvisioned(nameof(Services.ProvisionDocument.Enabled)));
        var enabledChanged = ImGui.Checkbox("Enabled", ref enabled);
        ImGui.EndDisabled();
        if (enabledChanged)
        {
            config.Enabled = enabled;
            SaveConfig(apply: false);
            _ = enabled ? host.StartAsync() : host.StopAsync();
        }

        HelpMarker("Runs the MCP server now and whenever the plugin loads. Off stops it and keeps it stopped.");

        ImGui.Spacing();
        DrawBindSettings();

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "What clients may do");
        ImGui.Separator();
        TierCheckbox(ToolPermission.Read, "Read game state and game data (player, location, items, chat log).");
        TierCheckbox(ToolPermission.Ui, "Show things only you see: echo, toasts, map flags, windows, agent board.");
        TierCheckbox(ToolPermission.Action, "Act in your client: target, gearsets, teleport, slash commands.");
        TierCheckbox(ToolPermission.Chat, "Send chat OTHER PLAYERS can see: say, party, tells, FC.");

        ImGui.Spacing();
        var confirm = config.ConfirmActions;
        if (ImGui.Checkbox("Ask me in game before Action/Chat calls", ref confirm))
        {
            config.ConfirmActions = confirm;
            SaveConfig();
        }

        HelpMarker("Each Action/Chat call waits for Allow / Deny / Allow this tool for 10 min in a game window.");

        ImGui.BeginDisabled(!config.ConfirmActions || config.IsProvisioned(nameof(Services.ProvisionDocument.ConfirmTimeoutSeconds)));
        var timeout = config.ConfirmTimeoutSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Deny automatically after (s)", ref timeout, 5, 30))
            config.ConfirmTimeoutSeconds = Math.Clamp(timeout, 5, 300);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig();
        ImGui.EndDisabled();
        HelpMarker("5-300 seconds without an answer counts as Deny.");

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

        DrawApprovalSettings();
    }

    private void TierCheckbox(ToolPermission tier, string description)
    {
        var on = config.IsPermitted(tier);
        ImGui.PushStyleColor(ImGuiCol.Text, TierColor(tier));
        var changed = ImGui.Checkbox($"{tier}##tier", ref on);
        ImGui.PopStyleColor();
        ImGui.SameLine(110 * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled(description);
        if (!changed)
            return;
        config.SetPermitted(tier, on);
        SaveConfig();
    }

    /// <summary>Apply/Revert for the endpoint draft (host, port, origins). Applying restarts a running server.</summary>
    private void DrawEndpointApply(string id)
    {
        if (!EndpointDraftDirty)
            return;
        if (ImGui.Button($"{(host.IsRunning ? "Apply and restart server" : "Apply")}##apply-{id}"))
        {
            config.BindMode = draftMode;
            var nextHost = draftCustomHost.Trim();
            config.CustomHost = nextHost.Length == 0 ? Configuration.DefaultHost : nextHost;
            config.Path = NormalizePath(draftPath);
            config.Port = draftPort;
            config.AllowedOrigins = ParseOrigins(draftOrigins);

            // A bind anything can reach is only allowed with the token on; force it rather than
            // letting the start fail with an error the player has to go and find.
            if (Services.BindPlanner.Resolve(draftMode, config.CustomHost, host.TailnetAddress).RequiresToken)
                config.RequireToken = true;

            SaveConfig();
            ResetSettingsDraft();
        }

        ImGui.SameLine();
        if (ImGui.Button($"Revert##revert-{id}"))
            ResetSettingsDraft();
    }

    // ---- categories ----------------------------------------------------------------------------

    private void DrawCategorySettings()
    {
        if (!ImGui.CollapsingHeader("Categories"))
            return;

        ImGui.TextDisabled("Switch off whole groups of tools, resources and prompts.");
        var categories = host.Categories;
        if (categories.Count == 0)
        {
            ImGui.TextDisabled("No providers loaded.");
            return;
        }

        foreach (var category in categories)
        {
            var on = config.IsCategoryEnabled(category);
            ImGui.BeginDisabled(config.IsProvisioned(nameof(Services.ProvisionDocument.DisabledCategories)));
            var changed = ImGui.Checkbox($"{category}##cat", ref on);
            ImGui.EndDisabled();
            if (changed)
            {
                config.SetCategoryEnabled(category, on);
                SaveConfig();
            }
        }
    }

    // ---- advanced ------------------------------------------------------------------------------

    private void DrawAdvancedSettings()
    {
        if (!ImGui.CollapsingHeader("Advanced"))
            return;

        ImGui.TextDisabled("The listen address, port and endpoint path are under Server, above.");

        ImGui.TextUnformatted("Allowed browser origins (one per line)");
        HelpMarker("Only for requests with an Origin header (browsers). Loopback origins are always accepted — a tailnet origin is not, so list it here if a browser on another tailnet machine needs access.");
        ImGui.BeginDisabled(config.IsProvisioned(nameof(Services.ProvisionDocument.AllowedOrigins)));
        ImGui.InputTextMultiline("##origins", ref draftOrigins, 2048, new System.Numerics.Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 3.5f));
        ImGui.EndDisabled();
        DrawEndpointApply("advanced");

        ImGui.Spacing();
        var requireToken = config.RequireToken;

        // Off loopback the token is not optional: the checkbox is held on rather than allowed to
        // produce a bind that then refuses to start.
        var tokenForced = !host.Plan.LoopbackOnly;
        ImGui.BeginDisabled(tokenForced || config.IsProvisioned(nameof(Services.ProvisionDocument.RequireToken)));
        var requireChanged = ImGui.Checkbox("Require bearer token", ref requireToken);
        ImGui.EndDisabled();
        if (requireChanged)
        {
            config.RequireToken = requireToken;
            SaveConfig();
        }

        HelpMarker("Default on. Changing it restarts a running server.");
        if (tokenForced)
            ImGui.TextDisabled("  required: the server listens beyond this machine");
        if (!config.RequireToken)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Without a token, any local program can call every enabled tool.");

        if (config.IsProvisioned(nameof(Services.ProvisionDocument.BearerToken)))
        {
            ImGui.TextDisabled("The bearer token comes from the provisioning file.");
        }
        else if (!regenerateArmed)
        {
            if (ImGui.Button("Regenerate token…"))
                regenerateArmed = true;
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Clients with a stored token are rejected until updated (headersHelper clients reconnect on their own).");
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

        ImGui.Spacing();
        var callTimeout = config.CallTimeoutSeconds;
        ImGui.SetNextItemWidth(160);
        ImGui.BeginDisabled(config.IsProvisioned(nameof(Services.ProvisionDocument.CallTimeoutSeconds)));
        if (ImGui.InputInt("Call timeout (s)", ref callTimeout, 5, 30))
            config.CallTimeoutSeconds = Math.Clamp(callTimeout, 5, 600);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig();
        ImGui.EndDisabled();
        HelpMarker("5-600, default 30. Upper bound for one tool/resource/prompt call; for Action/Chat it starts after your approval. Applies immediately.");

        var chatBuffer = config.ChatBufferSize;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Chat buffer lines", ref chatBuffer, 50, 500))
            config.ChatBufferSize = Math.Clamp(chatBuffer, 50, 5000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(apply: false);
        HelpMarker("50-5000, default 500. Recent chat lines read_chat can return. Applies with the next chat line.");

        var level = (int)config.ActivityLogLevel;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Activity log verbosity", ref level, LogLevelNames))
        {
            config.ActivityLogLevel = (ActivityLogLevel)level;
            SaveConfig(apply: false);
        }

        HelpMarker("What each request writes to /xllog (default Failures). Arguments and the token are never logged. The Activity tab always shows everything.");

        var dtr = config.ShowDtrEntry;
        if (ImGui.Checkbox("Server info bar entry (\"MCP ● n\")", ref dtr))
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
        if (ImGui.InputInt("Agent board expiry (min)", ref expiry, 10, 60))
            config.AgentBoardExpiryMinutes = Math.Clamp(expiry, 0, 7 * 24 * 60);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(apply: false);
        HelpMarker("Entries idle this long are removed. 0 = keep until cleared. Default 120.");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Custom objectives");
        ImGui.Separator();
        SettingCheckbox("Show custom objectives", config.ShowObjectives, v => config.ShowObjectives = v,
            "Objectives posted by agents (post_objective) or loaded from a pack (/xivmcp quests load <file>).");
        SettingCheckbox("Pin under the Duty List", config.ObjectivesFollowDutyList, v => config.ObjectivesFollowDutyList = v,
            "Off: a small movable window instead (drag it anywhere).");
        SettingCheckbox("Toast when an objective becomes ready", config.NotifyObjectiveReady, v => config.NotifyObjectiveReady = v, null);
        SettingCheckbox("Keep completed objectives listed", config.ShowCompletedObjectives, v => config.ShowCompletedObjectives = v,
            "Greyed out until cleared (/xivmcp quests clear-done).");
    }

    private void SettingCheckbox(string label, bool value, Action<bool> set, string? help)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            SaveConfig(apply: false);
        }

        if (help is not null)
            HelpMarker(help);
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

/// <summary>Settings: per-client tokens and auto-approve rules. Drawing and drafts only; matching is AutoApprovePolicy.</summary>
public sealed partial class MainWindow
{
    private string newTokenName = "";
    private string? tokenError;
    private (string Name, string Token)? revealedToken;
    private List<(AutoApproveRule Rule, string Prefixes)>? ruleDrafts;

    private void DrawPolicySettings()
    {
        if (!ImGui.CollapsingHeader("Client tokens and auto-approve rules (CI)"))
            return;

        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(
            "A client token is a second bearer token bound to a name you choose. Rules below pre-approve specific Action calls for the " +
            "client that connects with that token; its clientInfo name is not trusted. Everything else from that client is prompted or queued as usual. " +
            "A client token has the same server access as the main token.");
        ImGui.PopTextWrapPos();

        DrawClientTokens();
        ImGui.Spacing();
        DrawAutoApproveRules();
    }

    private void DrawClientTokens()
    {
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Client tokens");
        foreach (var entry in config.ClientTokens)
        {
            ImGui.TextUnformatted($"  {entry.Name}");
            ImGui.SameLine();
            ImGui.TextDisabled($"created {entry.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Revoke##token-{entry.Name}") && config.RevokeClientToken(entry.Name))
            {
                SaveConfig();
                break;
            }
        }

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##new-token-name", ref newTokenName, AutoApprovePolicy.MaxClientNameLength);
        ImGui.SameLine();
        if (ImGui.Button("Generate token for this client"))
        {
            try
            {
                var token = config.AddClientToken(newTokenName, DateTimeOffset.UtcNow);
                revealedToken = (newTokenName.Trim(), token);
                newTokenName = "";
                tokenError = null;
                SaveConfig();
            }
            catch (ArgumentException ex)
            {
                tokenError = ex.Message;
            }
        }

        if (tokenError != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, tokenError);

        if (revealedToken is { } shown)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, $"Token for {shown.Name}: shown once, only its hash is saved. Copy it now.");
            var text = shown.Token;
            ImGui.SetNextItemWidth(420);
            ImGui.InputText("##revealed-token", ref text, 128, ImGuiInputTextFlags.ReadOnly);
            ImGui.SameLine();
            if (ImGui.Button("Copy##revealed"))
                ImGui.SetClipboardText(shown.Token);
            ImGui.SameLine();
            if (ImGui.Button("Done##revealed"))
                revealedToken = null;
        }
    }

    private void DrawAutoApproveRules()
    {
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Auto-approve rules");
        ruleDrafts ??= config.AutoApproveRules.Select(r => (r.Clone(), string.Join('\n', r.Prefixes))).ToList();
        var clients = config.ClientTokens.Select(t => t.Name).ToArray();
        var remove = -1;
        for (var i = 0; i < ruleDrafts.Count; i++)
        {
            var (rule, prefixes) = ruleDrafts[i];
            ImGui.PushID($"rule-{i}");
            ImGui.Separator();
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled))
                rule.Enabled = enabled;
            ImGui.SameLine();
            var client = rule.Client;
            ImGui.SetNextItemWidth(160);
            if (ImGui.BeginCombo("Client token", client.Length == 0 ? "(choose)" : client))
            {
                foreach (var name in clients)
                {
                    if (ImGui.Selectable(name, name == client))
                        rule.Client = name;
                }

                ImGui.EndCombo();
            }

            if (rule.Client.Length > 0 && !clients.Contains(rule.Client))
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudOrange, "no such token: never matches");
            }

            var tool = rule.Tool;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputText("Tool", ref tool, 64))
                rule.Tool = tool.Trim();
            ImGui.SameLine();
            var argument = rule.Argument;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputText("Argument", ref argument, 64))
                rule.Argument = argument.Trim();
            ImGui.SameLine();
            var chat = rule.IncludeChat;
            if (ImGui.Checkbox("Include Chat", ref chat))
                rule.IncludeChat = chat;

            ImGui.TextUnformatted("Allowed prefixes (one per line)");
            HelpMarker("The argument must equal a prefix, or continue after it with a space and plain ASCII arguments only " +
                       "(no ; | & $ ` < > or backslash, no newlines or invisible characters). Case-sensitive. " +
                       "Empty means any arguments, except for execute_command, where an empty list never matches.");
            ImGui.InputTextMultiline("##prefixes", ref prefixes, 4096, new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 3.5f));
            ruleDrafts[i] = (rule, prefixes);
            if (string.IsNullOrWhiteSpace(prefixes) && rule.Tool != ConfirmationService.ExecuteCommandTool && rule.Tool.Length > 0)
                ImGui.TextColored(ImGuiColors.DalamudOrange, $"No prefixes: every {rule.Tool} call from this client runs without asking.");
            if (ImGui.SmallButton("Delete rule"))
                remove = i;
            ImGui.PopID();
        }

        if (remove >= 0)
            ruleDrafts.RemoveAt(remove);

        if (ImGui.Button("Add rule"))
            ruleDrafts.Add((new AutoApproveRule { Tool = ConfirmationService.ExecuteCommandTool, Client = clients.FirstOrDefault() ?? "" }, ""));
        ImGui.SameLine();
        if (ImGui.Button("Save rules"))
        {
            config.AutoApproveRules = ruleDrafts
                .Select(d =>
                {
                    var r = d.Rule.Clone();
                    r.Prefixes = d.Prefixes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p.TrimEnd('\r')).Where(p => p.Length > 0).ToList();
                    return r;
                })
                .ToList();
            SaveConfig(apply: false);
            ruleDrafts = null;
        }

        ImGui.SameLine();
        if (ImGui.Button("Revert##rules"))
            ruleDrafts = null;
    }
}

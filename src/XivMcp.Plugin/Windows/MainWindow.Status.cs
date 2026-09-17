using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

public sealed partial class MainWindow
{
    private bool revealToken;

    private void DrawStatusTab()
    {
        var status = host.Status;

        // ---- controls
        ImGui.BeginDisabled(host.IsTransitioning);
        if (host.IsRunning)
        {
            if (ImGui.Button("Stop"))
                _ = host.StopAsync();
            ImGui.SameLine();
            if (ImGui.Button("Restart"))
                _ = host.RestartAsync();
        }
        else if (ImGui.Button("Start"))
        {
            _ = host.StartAsync();
        }

        ImGui.EndDisabled();

        if (host.RestartPending)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Settings changed — restart to apply.");
        }

        ImGui.Spacing();

        // ---- facts
        if (ImGui.BeginTable("##status-facts", 2, ImGuiTableFlags.SizingFixedFit))
        {
            Row("Endpoint", host.Endpoint);
            Row("Autostart", config.Enabled ? "on" : "off");
            Row("Uptime", host.StartedAt is { } started ? FormatAge(DateTimeOffset.UtcNow - started) : "—");
            Row("Sessions", status.ActiveSessions.ToString());
            Row("Requests", $"{status.TotalRequests} total, {status.FailedRequests} failed");
            Row("Clients", status.ConnectedClients.Count == 0 ? "none" : string.Join(", ", status.ConnectedClients));
            Row("Auth", config.RequireToken ? "bearer token required" : "NO TOKEN (any local process can call tools)");
            if (status.LastError is { } serverError)
                Row("Server error", serverError);
            ImGui.EndTable();
        }

        if (!config.RequireToken)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Token check is off. Any program on this machine (and browsers, subject to Origin checks) can use the enabled tools.");

        var failed = host.Providers.Where(p => !p.Loaded).ToArray();
        if (failed.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudRed, $"{failed.Length} provider(s) failed to load:");
            foreach (var p in failed)
                ImGui.TextWrapped($"  {p.Type.Name} [{p.Category}] — {p.Error}");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ---- token
        ImGui.TextUnformatted("Bearer token");
        ImGui.SameLine();
        ImGui.TextDisabled(revealToken ? config.BearerToken : "••••••••••••••••••••••••••••••••");
        if (ImGui.SmallButton(revealToken ? "Hide##token" : "Reveal##token"))
            revealToken = !revealToken;
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy token"))
            ImGui.SetClipboardText(config.BearerToken);
        HelpMarker("Clients send it as \"Authorization: Bearer <token>\". Regenerate it in Settings if it leaks.");

        ImGui.Spacing();
        ImGui.Separator();

        // ---- client snippets
        ImGui.TextUnformatted("Connect a client");
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudViolet, "Claude Code (recommended: token read at connect time, never stored)");
        Snippet("claude-helper", ClientSnippets.ClaudeCodeHelper(config), null);
        ImGui.TextDisabled("Run from the xiv-mcp repository on the host. It uses a headersHelper that reads the token from pluginConfigs/XivMcp.json.");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Claude Code (static header)");
        Snippet(
            "claude-static",
            ClientSnippets.ClaudeCodeStatic(config, revealToken ? TokenDisplay.Real : TokenDisplay.Masked),
            ClientSnippets.ClaudeCodeStatic(config, TokenDisplay.Real));

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Generic JSON (mcpServers)");
        Snippet(
            "generic-json",
            ClientSnippets.GenericJson(config, revealToken ? TokenDisplay.Real : TokenDisplay.Masked),
            ClientSnippets.GenericJson(config, TokenDisplay.Real));
    }

    private static void Row(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGui.TextWrapped(value);
    }

    /// <param name="shown">Text displayed (token masked).</param>
    /// <param name="copied">Text copied; null copies <paramref name="shown"/>.</param>
    private static void Snippet(string id, string shown, string? copied)
    {
        var lines = shown.Count(c => c == '\n') + 1;
        var height = ImGui.GetTextLineHeightWithSpacing() * Math.Min(lines, 12) + ImGui.GetStyle().FramePadding.Y * 2;
        var text = shown;
        ImGui.InputTextMultiline($"##{id}", ref text, Math.Max(text.Length + 1, 64), new Vector2(-1, height), ImGuiInputTextFlags.ReadOnly);
        if (ImGui.SmallButton($"Copy{(copied != null ? " (with token)" : "")}##{id}"))
            ImGui.SetClipboardText(copied ?? shown);
    }
}

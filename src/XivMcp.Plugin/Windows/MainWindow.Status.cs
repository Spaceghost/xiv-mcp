using System.Globalization;
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
        // The value column stretches: with a fixed-fit column, wrapped text has no width to
        // wrap into and prints one character per line.
        if (ImGui.BeginTable("##status-facts", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
            Row("Endpoint", string.Join("   ", host.Endpoints));
            Row("Bind mode", BindModeLabel(config.BindMode) + (host.Plan.Notice is null ? "" : "  — " + host.Plan.Notice));
            if (host.Plan.TailnetHost is { } tailnetHost)
                Row("Tailnet", host.Plan.MagicDnsName is { } dns ? $"{tailnetHost}  ({dns})" : tailnetHost);
            Row("Enabled", config.Enabled ? "yes" : "no (server stays stopped on load)");
            Row("Uptime", host.StartedAt is { } started ? FormatAge(DateTimeOffset.UtcNow - started) : "—");
            Row("Sessions", status.ActiveSessions.ToString(CultureInfo.InvariantCulture));
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

        // ---- client setup
        ImGui.TextUnformatted("Connect a client");
        ImGui.SameLine();
        if (ImGui.SmallButton(revealToken ? "Hide token##token" : "Reveal token##token"))
            revealToken = !revealToken;
        HelpMarker("The token stays hidden (shown and copied as <token>) until you reveal it. Regenerate it in Settings > Advanced if it leaks.");

        var display = revealToken ? TokenDisplay.Real : TokenDisplay.Placeholder;

        // With the tailnet bound, the tailnet address is the one that works from this machine AND from
        // the rest of the tailnet, so that is what the snippets use.
        var endpoint = host.PreferredEndpoint;
        if (host.Endpoints.Count > 1)
            ImGui.TextDisabled($"Also reachable at {string.Join(", ", host.Endpoints.Where(e => e != endpoint))}.");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Claude Code");
        Snippet("claude", ClientSnippets.ClaudeCode(config, display, endpoint));
        ImGui.TextDisabled("To keep the token out of Claude's config, run tools/claude-mcp-add.sh from the xiv-mcp repository instead.");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Other clients (mcpServers JSON)");
        Snippet("json", ClientSnippets.GenericJson(config, display, endpoint));
    }

    private static string BindModeLabel(BindMode mode) => mode switch
    {
        BindMode.Loopback => "this machine only",
        BindMode.LoopbackAndTailnet => "this machine + tailnet",
        BindMode.TailnetOnly => "tailnet only",
        BindMode.Custom => "custom address",
        _ => mode.ToString(),
    };

    private static void Row(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGui.TextWrapped(value);
    }

    private static void Snippet(string id, string text)
    {
        var lines = text.Count(c => c == '\n') + 1;
        var height = ImGui.GetTextLineHeightWithSpacing() * Math.Min(lines, 12) + ImGui.GetStyle().FramePadding.Y * 2;
        var shown = text;
        ImGui.InputTextMultiline($"##{id}", ref shown, Math.Max(text.Length + 1, 64), new Vector2(-1, height), ImGuiInputTextFlags.ReadOnly);
        if (ImGui.SmallButton($"Copy##{id}"))
            ImGui.SetClipboardText(text);
    }
}

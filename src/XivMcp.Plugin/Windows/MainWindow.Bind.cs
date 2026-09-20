using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Core.Net;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

/// <summary>
/// The "Where the server listens" section: bind mode, the live tailnet address, the endpoints that
/// result, and the safety rules that come with leaving loopback. Kept in its own file so the rest of
/// the settings window stays untouched.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly (BindMode Mode, string Label, string Help)[] BindModes =
    [
        (BindMode.Loopback, "This machine only (127.0.0.1)",
            "The default. Only programs running on this computer can connect — under Wine that includes clients on the Linux host."),
        (BindMode.LoopbackAndTailnet, "This machine + my tailnet",
            "Binds 127.0.0.1 and this machine's Tailscale address. Anything else on your tailnet that has the bearer token can drive the game."),
        (BindMode.TailnetOnly, "Tailnet only",
            "Binds only the Tailscale address. Local clients must use that address too. Falls back to 127.0.0.1 if no Tailscale address is found, so the server always starts."),
        (BindMode.Custom, "Custom address…",
            "Any address of this machine. Use it for a LAN bind or an address you manage yourself."),
    ];

    /// <summary>Background re-detection so the draw loop never blocks on adapter enumeration or DNS.</summary>
    private Task? tailnetRefresh;

    /// <summary>The provisioning file watcher, when the plugin shell wired one up.</summary>
    public ProvisionStore? Provision { get; set; }

    /// <summary>
    /// Explains, at the top of the Settings tab, that an outside file is in charge of some settings —
    /// otherwise a greyed-out control looks like a bug.
    /// </summary>
    private void DrawProvisionBanner()
    {
        if (Provision is not { } store)
            return;

        if (store.Error is { } error)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Provisioning file problem: {error}");
            ImGui.TextDisabled($"The last values that did load are still in force. File: {store.Path}");
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
        }

        if (store.Current is not { } document)
            return;

        ImGui.PushTextWrapPos();
        ImGui.TextColored(ImGuiColors.DalamudOrange, $"{document.Present.Count} setting(s) come from a provisioning file and cannot be changed here.");
        ImGui.TextDisabled($"{document.Source} — {string.Join(", ", document.Present.Order(StringComparer.Ordinal))}");
        ImGui.TextDisabled("Remove or edit that file to take these settings back. The plugin only ever reads it.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>Kicks off tailnet detection unless one is already in flight. Safe to call from Draw.</summary>
    private void RequestTailnetRefresh()
    {
        if (tailnetRefresh is { IsCompleted: false })
            return;
        tailnetRefresh = Task.Run(() => host.RefreshTailnet());
    }

    private void DrawBindSettings()
    {
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Where the server listens");
        ImGui.Separator();

        var locked = config.IsProvisioned(nameof(ProvisionDocument.BindMode));
        ImGui.BeginDisabled(locked);
        foreach (var (mode, label, help) in BindModes)
        {
            if (ImGui.RadioButton($"{label}##bind-{mode}", draftMode == mode))
                draftMode = mode;
            HelpMarker(help);
        }

        ImGui.EndDisabled();
        if (locked)
            ProvisionedNote();

        if (draftMode == BindMode.Custom)
        {
            ImGui.SetNextItemWidth(220);
            ImGui.BeginDisabled(config.IsProvisioned(nameof(ProvisionDocument.CustomHost)));
            ImGui.InputText("Address", ref draftCustomHost, 64);
            ImGui.EndDisabled();
            HelpMarker($"An address of this machine, or a name that resolves to one. Default {Configuration.DefaultHost}.");
            if (draftCustomHost.Trim().Length > 0
                && !BindPlanner.IsBindableLiteral(draftCustomHost)
                && !draftCustomHost.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextColored(ImGuiColors.DalamudOrange, "Not an IP address — it will be resolved by name when the server starts.");
            }
        }

        DrawTailnetStatus();
        DrawPortAndPath();
        DrawPlannedEndpoints();
        DrawExposureWarning();
        DrawEndpointApply("bind");
    }

    // ---- detection -----------------------------------------------------------------------------

    private void DrawTailnetStatus()
    {
        var needsTailnet = draftMode is BindMode.LoopbackAndTailnet or BindMode.TailnetOnly;
        if (!needsTailnet && host.TailnetAddress is null)
            return;

        ImGui.Spacing();
        var tailnet = host.TailnetAddress;
        if (tailnet is null)
        {
            ImGui.TextColored(
                host.TailnetProbed ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudGrey,
                host.TailnetProbed ? "Tailscale address: none found" : "Tailscale address: looking…");
            if (host.TailnetProbed)
            {
                ImGui.PushTextWrapPos();
                ImGui.TextDisabled("Is Tailscale running on this machine? Without it these modes listen on 127.0.0.1 only; the server still starts.");
                ImGui.PopTextWrapPos();
            }
        }
        else
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, $"Tailscale address: {tailnet.HostText}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy##tailnet-ip"))
                ImGui.SetClipboardText(tailnet.HostText);
            ImGui.TextDisabled(tailnet.MagicDnsName is { } dns
                ? $"  {dns}  (found on {tailnet.AdapterName})"
                : $"  found on {tailnet.AdapterName}; no MagicDNS name");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Re-detect##tailnet"))
            RequestTailnetRefresh();
    }

    // ---- port / path ---------------------------------------------------------------------------

    private void DrawPortAndPath()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(160);
        ImGui.BeginDisabled(config.IsProvisioned(nameof(ProvisionDocument.Port)));
        ImGui.InputInt("Port", ref draftPort, 0, 0);
        ImGui.EndDisabled();
        draftPort = Math.Clamp(draftPort, 1, 65535);
        HelpMarker($"TCP port (1-65535, default {Configuration.DefaultPort}).");

        ImGui.SetNextItemWidth(160);
        ImGui.BeginDisabled(config.IsProvisioned(nameof(ProvisionDocument.Path)));
        ImGui.InputText("Endpoint path", ref draftPath, 64);
        ImGui.EndDisabled();
        HelpMarker($"Default {Configuration.DefaultPath}. Clients must use the same path.");
    }

    // ---- resulting endpoints -------------------------------------------------------------------

    private void DrawPlannedEndpoints()
    {
        var plan = BindPlanner.Resolve(draftMode, draftCustomHost, host.TailnetAddress);
        var port = Math.Clamp(draftPort, 1, 65535);
        var endpoints = BindPlanner.Endpoints(plan, port, draftPath);

        ImGui.Spacing();
        ImGui.TextUnformatted(endpoints.Count == 1 ? "Endpoint:" : "Endpoints:");
        foreach (var endpoint in endpoints)
        {
            ImGui.TextDisabled("  " + endpoint);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Copy##ep-{endpoint}"))
                ImGui.SetClipboardText(endpoint);
        }

        if (plan.Notice is { } notice)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ImGuiColors.DalamudOrange, notice);
            ImGui.PopTextWrapPos();
        }
    }

    // ---- exposure warning and the token rule ---------------------------------------------------

    private void DrawExposureWarning()
    {
        var plan = BindPlanner.Resolve(draftMode, draftCustomHost, host.TailnetAddress);
        if (!plan.RequiresToken)
            return;

        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        ImGui.TextColored(
            ImGuiColors.DalamudRed,
            $"This exposes your game beyond this machine. Anyone who can reach {BindPlanner.Describe(plan)} AND has the bearer token can use every tool "
            + "you have enabled — including, if you switch them on, Action and Chat tools that other players see. Keep the token secret and keep "
            + "Action/Chat confirmation on.");
        ImGui.PopTextWrapPos();

        if (!config.RequireToken)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "The bearer token is required for this bind mode; applying it switches 'Require bearer token' on.");
        }
    }

    /// <summary>Shown under a control the provisioning file owns.</summary>
    private void ProvisionedNote()
    {
        ImGui.TextDisabled("  set by the provisioning file");
        if (!ImGui.IsItemHovered())
            return;
        Tooltip(Provision?.Path is { } path
            ? $"This setting comes from {path} and cannot be changed here. Edit that file (or remove it) to take control back."
            : "This setting comes from the provisioning file and cannot be changed here.");
    }
}

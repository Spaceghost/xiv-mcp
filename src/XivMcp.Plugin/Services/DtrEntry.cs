using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

namespace XivMcp.Plugin.Services;

/// <summary>
/// Optional server info bar entry: "MCP ● n" while running (n = active sessions), "MCP ○" when
/// stopped. Click toggles the main window. Updated from the framework thread about twice a second.
/// </summary>
public sealed class DtrEntry : IDisposable
{
    private const string Title = "XivMcp";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly IDtrBar dtrBar;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ServerHost host;
    private readonly ConfirmationService confirmations;
    private readonly Action toggleWindow;

    private IDtrBarEntry? entry;
    private DateTime nextUpdate;
    private string lastText = "";
    private bool failed;

    public DtrEntry(IDtrBar dtrBar, IFramework framework, IPluginLog log, Configuration config, ServerHost host, ConfirmationService confirmations, Action toggleWindow)
    {
        this.dtrBar = dtrBar;
        this.framework = framework;
        this.log = log;
        this.config = config;
        this.host = host;
        this.confirmations = confirmations;
        this.toggleWindow = toggleWindow;
        framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework fw)
    {
        if (failed)
            return;
        try
        {
            var now = DateTime.UtcNow;
            if (now < nextUpdate)
                return;
            nextUpdate = now + Interval;

            if (!config.ShowDtrEntry)
            {
                RemoveEntry();
                return;
            }

            if (entry == null)
            {
                entry = dtrBar.Get(Title);
                entry.OnClick = _ =>
                {
                    try
                    {
                        toggleWindow();
                    }
                    catch
                    {
                        // Click handlers run inside game UI callbacks.
                    }
                };
                lastText = "";
            }

            var status = host.Status;
            var pending = confirmations.HasPending;
            var text = host.IsRunning ? $"MCP ● {status.ActiveSessions}" : "MCP ○";
            if (pending)
                text += " ?";
            var tooltip = host.IsRunning
                ? $"XivMcp listening on {host.Endpoint}\n{status.ActiveSessions} session(s), {status.TotalRequests} request(s)" +
                  (pending ? "\nA call is waiting for your approval." : "") + "\nClick to open."
                : $"XivMcp stopped{(host.LastError is { } err ? ": " + err : "")}\nClick to open.";
            if (text + tooltip == lastText)
                return;
            lastText = text + tooltip;

            entry.Text = text;
            entry.Tooltip = tooltip;
            entry.Shown = true;
        }
        catch (Exception ex)
        {
            failed = true;
            log.Warning(ex, "DTR entry disabled after an error");
            RemoveEntry();
        }
    }

    private void RemoveEntry()
    {
        try
        {
            entry?.Remove();
        }
        catch
        {
            // Already gone.
        }

        entry = null;
        lastText = "";
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        RemoveEntry();
    }
}

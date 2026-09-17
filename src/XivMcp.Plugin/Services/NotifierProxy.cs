using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>
/// The <see cref="IMcpNotifier"/> handed to providers. It exists before the server does, survives
/// server restarts, and swallows (and logs once) any failure, so a provider event handler that
/// raises a notification can never throw into the game.
/// </summary>
public sealed class NotifierProxy : IMcpNotifier
{
    private readonly IPluginLog log;
    private volatile McpServer? server;
    private int loggedFailure;

    public NotifierProxy(IPluginLog log) => this.log = log;

    internal void Attach(McpServer? target) => server = target;

    public void ResourceUpdated(string uri) => Forward(n => n.ResourceUpdated(uri));

    public void ResourceListChanged() => Forward(static n => n.ResourceListChanged());

    public void ToolListChanged() => Forward(static n => n.ToolListChanged());

    public void PromptListChanged() => Forward(static n => n.PromptListChanged());

    public void Log(McpLogLevel level, string logger, object? data) => Forward(n => n.Log(level, logger, data));

    /// <summary>tools/prompts/resources list_changed together (after permission or category changes).</summary>
    public void AllListsChanged()
    {
        ToolListChanged();
        PromptListChanged();
        ResourceListChanged();
    }

    private void Forward(Action<IMcpNotifier> send)
    {
        var target = server;
        if (target == null)
            return;
        try
        {
            send(target.Notifier);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref loggedFailure, 1) == 0)
                log.Warning(ex, "MCP notification failed (further failures are not logged)");
        }
    }
}

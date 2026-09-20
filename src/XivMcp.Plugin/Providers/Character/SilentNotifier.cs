using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Character;

/// <summary>
/// Drops every notification. Composite providers hand it to the private provider instances they use for reads, so
/// only the registered instance of a provider announces resource changes.
/// </summary>
internal sealed class SilentNotifier : IMcpNotifier
{
    public static readonly SilentNotifier Instance = new();

    private SilentNotifier()
    {
    }

    public void ResourceUpdated(string uri)
    {
    }

    public void ResourceListChanged()
    {
    }

    public void ToolListChanged()
    {
    }

    public void PromptListChanged()
    {
    }

    public void Log(McpLogLevel level, string logger, object? data)
    {
    }
}

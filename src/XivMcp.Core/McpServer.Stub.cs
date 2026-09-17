// TEMPORARY compile stub so dependent projects build while the core runtime is written.
// The core agent deletes this file and replaces it with the real implementation.
using System.Reflection;

namespace XivMcp.Core;

public sealed partial class McpServer
{
    private partial void InitializeCore() { }
    public partial void RegisterProvider(object provider) => throw new NotImplementedException();
    public partial void RegisterProviders(Assembly assembly, Func<Type, object> factory) => throw new NotImplementedException();
    public partial Task StartAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
    public partial Task StopAsync() => Task.CompletedTask;
    public partial ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public partial IMcpNotifier Notifier => throw new NotImplementedException();
    public partial ServerStatus GetStatus() => new(false, "", 0, 0, 0, null, []);
    public partial IReadOnlyList<ActivityEntry> GetActivity(int max) => [];
    public partial IReadOnlyList<(string Name, string? Title, string Description, ToolPermission Permission, string Category)> ListRegisteredTools() => [];
}

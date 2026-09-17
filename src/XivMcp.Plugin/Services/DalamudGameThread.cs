using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>
/// <see cref="IGameThread"/> over Dalamud's framework scheduler. Calls already on the framework
/// thread run inline (never queue-and-wait on the thread you are blocking).
/// </summary>
public sealed class DalamudGameThread : IGameThread
{
    private readonly IFramework framework;

    public DalamudGameThread(IFramework framework) => this.framework = framework;

    public bool IsOnGameThread => framework.IsInFrameworkUpdateThread;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        if (framework.IsFrameworkUnloading)
            return Task.FromException<T>(new McpToolException("The game is shutting down."));

        if (framework.IsInFrameworkUpdateThread)
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        // RunOnTick queues onto the next framework tick and honours cancellation while queued.
        return framework.RunOnTick(func, cancellationToken: cancellationToken);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync<object?>(() =>
        {
            action();
            return null;
        }, cancellationToken);
    }
}

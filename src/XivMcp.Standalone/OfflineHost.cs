using XivMcp.Core;

namespace XivMcp.Standalone;

/// <summary>There is no framework thread without the game: "game thread" work runs inline on the caller.</summary>
internal sealed class OfflineGameThread : IGameThread
{
    public bool IsOnGameThread => true;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(func());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}

/// <summary>
/// Every tier and category is "on" so that a live tool answers with <see cref="McpErrorCodes.GameNotRunning"/> rather than
/// with "tier disabled": the player's real switches live in the plugin and apply once the game is up. Nothing here can
/// change anything — the only tools that run are read-only lookups over the game data files.
/// </summary>
internal sealed class OfflineHostState : IHostState
{
    public bool IsLoggedIn => false;

    public bool IsPermitted(ToolPermission permission) => true;

    public bool IsCategoryEnabled(string category) => true;
}

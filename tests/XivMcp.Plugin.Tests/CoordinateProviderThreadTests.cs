using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Character;
using XivMcp.Plugin.Providers.World;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>
/// find_nearest_aetheryte with no point uses the player's position. Seen in game on v0.1.1-test.1: it read the object
/// table from a thread-pool thread and Dalamud threw "Not on main thread!". The read has to hop to the framework.
/// </summary>
public class CoordinateProviderThreadTests
{
    /// <summary>A game thread that knows when a call is running "on" it, like IFramework.IsInFrameworkUpdateThread.</summary>
    private sealed class TrackingGameThread : IGameThread
    {
        public bool Inside { get; private set; }

        public int Hops { get; private set; }

        public bool IsOnGameThread => Inside;

        public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
        {
            Hops++;
            Inside = true;
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
            finally
            {
                Inside = false;
            }
        }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
            InvokeAsync<object?>(() =>
            {
                action();
                return null;
            }, cancellationToken);
    }

    [Fact]
    public async Task ThePlayersPositionIsReadOnTheFrameworkThread()
    {
        var game = new TrackingGameThread();
        var readsOnThread = 0;
        var objects = FakeProxy.Create<IObjectTable>(new()
        {
            ["get_LocalPlayer"] = _ =>
            {
                if (!game.Inside)
                    throw new InvalidOperationException("Not on main thread!");
                readsOnThread++;
                return null;
            },
        });
        var provider = new CoordinateProvider(FakeProxy.Create<IDataManager>(), FakeProxy.Create<IClientState>(), objects);
        var ctx = new ToolContext { Game = game, Notifier = SilentNotifier.Instance, CancellationToken = CancellationToken.None };

        // Not logged in and in no zone: a clear tool error, not "Not on main thread!".
        var ex = await Assert.ThrowsAsync<McpToolException>(() => provider.FindNearestAetheryte(ctx: ctx));
        Assert.Contains("territoryId", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, game.Hops);
        Assert.Equal(1, readsOnThread);
    }

    [Fact]
    public async Task AGivenPointNeedsNoFrameworkHop()
    {
        var game = new TrackingGameThread();
        var provider = new CoordinateProvider(FakeProxy.Create<IDataManager>(), FakeProxy.Create<IClientState>(), FakeProxy.Create<IObjectTable>());
        var ctx = new ToolContext { Game = game, Notifier = SilentNotifier.Instance, CancellationToken = CancellationToken.None };

        await Assert.ThrowsAsync<McpToolException>(() => provider.FindNearestAetheryte(x: 10, y: 10, ctx: ctx));
        Assert.Equal(0, game.Hops);
    }
}

using XivMcp.Core;
using XivMcp.Core.Net;

namespace XivMcp.Standalone;

/// <summary>What <see cref="HandoffCoordinator"/> needs from the listener; the real one is <see cref="McpServer"/>.</summary>
internal interface IListener
{
    Task StartAsync();

    Task StopAsync();
}

internal enum HandoffState
{
    /// <summary>This host holds the port.</summary>
    Serving,

    /// <summary>The plugin (or something else) holds the port; this host keeps trying to get it back.</summary>
    Yielded,
}

/// <summary>
/// Owns the port while the game is closed and gives it up the moment the plugin asks. Chosen over proxying to the
/// plugin because it has no moving parts while the game runs: whoever holds the port answers, sessions and SSE streams
/// are never relayed, and the approval flow sees the real client. Getting the port back needs no knowledge of the
/// game either: once yielded, this host simply tries to bind every few seconds, and the first bind that succeeds
/// after <see cref="StableFreeProbes"/> consecutive free checks means the plugin has gone (the wait keeps a plugin
/// restart, which frees the port for an instant, from being mistaken for the game closing). If it does grab the port
/// during such a gap, the plugin's next start just asks again.
/// </summary>
internal sealed class HandoffCoordinator(IListener listener, Func<bool> portIsFree, Action<string> log, TimeSpan? pollInterval = null) : IDisposable
{
    public const int StableFreeProbes = 3;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(2);
    private int freeProbes;

    public void Dispose() => gate.Dispose();

    public HandoffState State { get; private set; } = HandoffState.Yielded;

    /// <summary>First bind. When the port is taken (the game is already running) the host starts out yielded.</summary>
    public async Task StartAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TryServeLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>POST {path}/handoff from the plugin. The response is sent before the listener stops.</summary>
    public ControlResponse OnHandoffRequested(ControlRequest request, string version)
    {
        if (!request.FromThisMachine)
            return new ControlResponse(403, new System.Text.Json.Nodes.JsonObject { ["error"] = "handoff is only accepted from this machine" });

        var body = HostHandoff.Identity(HostKind.Standalone, version, gameRunning: false);
        body["yielding"] = true;
        _ = Task.Run(async () =>
        {
            // Let the response leave first; the plugin retries its bind for a few seconds.
            await Task.Delay(150).ConfigureAwait(false);
            await YieldAsync().ConfigureAwait(false);
        });
        return new ControlResponse(200, body);
    }

    public async Task YieldAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != HandoffState.Serving)
                return;
            await listener.StopAsync().ConfigureAwait(false);
            State = HandoffState.Yielded;
            freeProbes = 0;
            log("handed the port to the plugin; will take it back when the game exits");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One poll: while yielded, count consecutive "port is free" probes and bind again once it has stayed free.</summary>
    public async Task TickAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == HandoffState.Serving)
                return;
            freeProbes = portIsFree() ? freeProbes + 1 : 0;
            if (freeProbes >= StableFreeProbes)
                await TryServeLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await TickAsync().ConfigureAwait(false);
        }
    }

    private async Task TryServeLockedAsync()
    {
        try
        {
            await listener.StartAsync().ConfigureAwait(false);
            State = HandoffState.Serving;
            freeProbes = 0;
            log("serving");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            try
            {
                await listener.StopAsync().ConfigureAwait(false);
            }
            catch (Exception stop) when (stop is System.Net.Sockets.SocketException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Nothing was bound.
            }

            State = HandoffState.Yielded;
            freeProbes = 0;
            log($"the port is in use ({ex.Message}); waiting for it");
        }
    }
}

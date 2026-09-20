using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using XivMcp.Core;
using XivMcp.Core.Net;

namespace XivMcp.Standalone.Tests;

internal sealed class FakeListener : IListener
{
    public int Starts;
    public int Stops;
    public bool PortTaken;

    public Task StartAsync()
    {
        if (PortTaken)
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        Starts++;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stops++;
        return Task.CompletedTask;
    }
}

public sealed class HandoffCoordinatorTests
{
    [Fact]
    public async Task StartsServingAndYieldsOnlyToThisMachine()
    {
        var listener = new FakeListener();
        using var coordinator = new HandoffCoordinator(listener, () => true, _ => { });
        await coordinator.StartAsync();
        Assert.Equal(HandoffState.Serving, coordinator.State);

        var remote = coordinator.OnHandoffRequested(new ControlRequest("POST", "handoff", null, FromThisMachine: false, null), "1.0.0");
        Assert.Equal(403, remote.Status);
        Assert.Equal(HandoffState.Serving, coordinator.State);

        var local = coordinator.OnHandoffRequested(new ControlRequest("POST", "handoff", null, FromThisMachine: true, null), "1.0.0");
        Assert.Equal(200, local.Status);
        Assert.True(local.Body["yielding"]!.GetValue<bool>());
        Assert.Equal("standalone", local.Body["host"]!.GetValue<string>());

        for (var i = 0; i < 200 && coordinator.State != HandoffState.Yielded; i++)
            await Task.Delay(10);
        Assert.Equal(HandoffState.Yielded, coordinator.State);
        Assert.Equal(1, listener.Stops);
    }

    [Fact]
    public async Task TakesThePortBackOnlyAfterItStayedFree()
    {
        var listener = new FakeListener();
        var free = false;
        using var coordinator = new HandoffCoordinator(listener, () => free, _ => { });
        await coordinator.StartAsync();
        await coordinator.YieldAsync();

        await coordinator.TickAsync(); // the plugin holds it
        free = true;
        await coordinator.TickAsync();
        await coordinator.TickAsync();
        Assert.Equal(HandoffState.Yielded, coordinator.State);

        free = false; // a plugin restart: free for an instant, then taken again
        await coordinator.TickAsync();
        free = true;
        await coordinator.TickAsync();
        await coordinator.TickAsync();
        Assert.Equal(HandoffState.Yielded, coordinator.State);
        await coordinator.TickAsync();
        Assert.Equal(HandoffState.Serving, coordinator.State);
        Assert.Equal(2, listener.Starts);
    }

    [Fact]
    public async Task StartsOutYieldedWhenTheGameAlreadyHoldsThePort()
    {
        var listener = new FakeListener { PortTaken = true };
        using var coordinator = new HandoffCoordinator(listener, () => true, _ => { });
        await coordinator.StartAsync();
        Assert.Equal(HandoffState.Yielded, coordinator.State);

        // A bind that loses the race keeps waiting instead of crashing.
        await coordinator.TickAsync();
        await coordinator.TickAsync();
        await coordinator.TickAsync();
        Assert.Equal(HandoffState.Yielded, coordinator.State);

        listener.PortTaken = false;
        for (var i = 0; i < HandoffCoordinator.StableFreeProbes; i++)
            await coordinator.TickAsync();
        Assert.Equal(HandoffState.Serving, coordinator.State);
    }
}

/// <summary>The real thing on a real port: standalone serves, a stand-in for the plugin asks for the port, gets it, leaves, and the standalone returns.</summary>
public sealed class HandoffEndToEndTests
{
    private const string Token = "handoff-test-token";

    internal static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class InlineGame : IGameThread
    {
        public bool IsOnGameThread => true;

        public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default) => Task.FromResult(func());

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class AllowAll : IHostState
    {
        public bool IsLoggedIn => true;

        public bool IsPermitted(ToolPermission permission) => true;

        public bool IsCategoryEnabled(string category) => true;
    }

    [Fact]
    public async Task ThePluginTakesThePortAndTheStandaloneGetsItBack()
    {
        var port = FreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        await using var standalone = new StandaloneHost(new StandaloneHostOptions { Settings = new SharedSettings { Port = port, BearerToken = Token } });
        await standalone.Coordinator.StartAsync();
        Assert.Equal(HandoffState.Serving, standalone.Coordinator.State);
        Assert.Equal(HostKind.Standalone, await HostHandoff.ProbeAsync(endpoint, Token, TimeSpan.FromSeconds(5)));

        // Without the token nobody can push the standalone off its port.
        Assert.False(await HostHandoff.RequestYieldAsync(endpoint, "wrong", TimeSpan.FromSeconds(5)));
        Assert.Equal(HandoffState.Serving, standalone.Coordinator.State);

        // The plugin's start: the bind fails, it asks, then binds.
        await using var plugin = new McpServer(new McpServerOptions { Port = port, BearerToken = Token }, new InlineGame(), new AllowAll())
        {
            ControlHandler = static request => Task.FromResult<ControlResponse?>(
                request is { Method: "GET", SubPath: HostHandoff.HostRoute } ? new ControlResponse(200, HostHandoff.Identity(HostKind.Plugin, "test", true)) : null),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => plugin.StartAsync());
        await plugin.StopAsync();
        Assert.True(await HostHandoff.RequestYieldAsync(endpoint, Token, TimeSpan.FromSeconds(5)));
        await HostHandoff.RetryAsync(() => plugin.StartAsync(), attempts: 20, delay: TimeSpan.FromMilliseconds(200));
        Assert.Equal(HostKind.Plugin, await HostHandoff.ProbeAsync(endpoint, Token, TimeSpan.FromSeconds(5)));
        Assert.Equal(HandoffState.Yielded, standalone.Coordinator.State);

        // While the plugin holds the port the standalone stays away.
        for (var i = 0; i < HandoffCoordinator.StableFreeProbes + 1; i++)
            await standalone.Coordinator.TickAsync();
        Assert.Equal(HandoffState.Yielded, standalone.Coordinator.State);

        // The game exits.
        await plugin.StopAsync();
        for (var i = 0; i < HandoffCoordinator.StableFreeProbes; i++)
            await standalone.Coordinator.TickAsync();
        Assert.Equal(HandoffState.Serving, standalone.Coordinator.State);
        Assert.Equal(HostKind.Standalone, await HostHandoff.ProbeAsync(endpoint, Token, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ProbingNothingIsUnknown() =>
        Assert.Equal(HostKind.Unknown, await HostHandoff.ProbeAsync(new Uri($"http://127.0.0.1:{FreePort()}/mcp"), null, TimeSpan.FromSeconds(2)));
}

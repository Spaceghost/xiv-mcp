using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

/// <summary>
/// What a plugin unload must give back, for the part that runs without the game: a server that was
/// started, served requests and was disposed must be collectable (nothing static, no timer, task or
/// handler still points at it), must have closed its port, and must not have left threads or handles behind.
/// The Dalamud-facing layer cannot be constructed here; ReloadLeakAuditTests reads its sources instead.
/// </summary>
[Collection(ProcessWideMeasurements.Name)]
public sealed class UnloadLeakTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Server, int Port)> ServeAndDisposeAsync()
    {
        var s = await TestServer.StartAsync();
        var port = s.Port;
        using (var request = new HttpRequestMessage(HttpMethod.Post, s.Endpoint)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"" + TestServer.Modern + "\"}}}", Encoding.UTF8, "application/json"),
        })
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            using var response = await s.Http.SendAsync(request);
        }

        var weak = new WeakReference(s.Server);
        await s.DisposeAsync();
        return (weak, port);
    }

    private static void Collect()
    {
        for (var i = 0; i < 5; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public async Task ADisposedServerIsCollectedAndItsPortIsClosed()
    {
        var (weak, port) = await ServeAndDisposeAsync();
        for (var i = 0; i < 20 && weak.IsAlive; i++)
        {
            await Task.Delay(100);
            Collect();
        }

        Assert.False(weak.IsAlive, "a disposed McpServer is still reachable: a static, a timer, a task or an event handler holds it");
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync("127.0.0.1", port));
    }

    [Fact]
    public async Task StartAndDisposeCyclesLeaveNoThreadsHandlesOrMemoryBehind()
    {
        for (var i = 0; i < 3; i++)
            await ServeAndDisposeAsync(); // warm-up: thread pool, JIT, HttpClient statics

        Collect();
        using var me = Process.GetCurrentProcess();
        var threads = me.Threads.Count;
        var handles = me.HandleCount;
        var memory = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < 25; i++)
            await ServeAndDisposeAsync();

        await Task.Delay(500);
        Collect();
        me.Refresh();
        Assert.True(me.Threads.Count <= threads + 8, $"threads grew from {threads} to {me.Threads.Count} over 25 start/dispose cycles");
        Assert.True(me.HandleCount <= handles + 50, $"handles grew from {handles} to {me.HandleCount} over 25 start/dispose cycles");
        var grown = GC.GetTotalMemory(forceFullCollection: true) - memory;
        Assert.True(grown < 2 * 1024 * 1024, $"managed memory grew by {grown} bytes over 25 start/dispose cycles");
    }
}

/// <summary>
/// Thread, handle and heap counts are process-wide, so a test that compares them before and after must not share
/// the process with other test classes opening sockets at the same moment.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideMeasurements
{
    public const string Name = "process-wide measurements";
}

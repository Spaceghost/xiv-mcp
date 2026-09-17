using System.Collections.Concurrent;

namespace XivMcp.Core.Tests.Infrastructure;

/// <summary>Dedicated "framework" thread that runs queued work, like Dalamud's IFramework.</summary>
public sealed class FakeGameThread : IGameThread, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public FakeGameThread()
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
        })
        { IsBackground = true, Name = "FakeFramework" };
        _thread.Start();
    }

    public int InvocationCount;

    /// <summary>When set, work items wait for this before running (simulates a stalled frame).</summary>
    public volatile ManualResetEventSlim? Gate;

    public bool IsOnGameThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref InvocationCount);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsOnGameThread)
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }

            return tcs.Task;
        }

        _queue.Add(() =>
        {
            Gate?.Wait(TimeSpan.FromSeconds(30));
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(func());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            action();
            return 0;
        }, cancellationToken);

    public void Dispose() => _queue.CompleteAdding();
}

public sealed class FakeHostState : IHostState
{
    public volatile bool LoggedIn = true;

    public readonly ConcurrentDictionary<ToolPermission, bool> Permissions = new()
    {
        [ToolPermission.Read] = true,
        [ToolPermission.Ui] = true,
        [ToolPermission.Action] = false,
        [ToolPermission.Chat] = false,
    };

    public readonly ConcurrentDictionary<string, bool> DisabledCategories = new();

    public int LoginChecksOnWrongThread;

    public FakeGameThread? GameThread { get; set; }

    public bool IsLoggedIn
    {
        get
        {
            if (GameThread is not null && !GameThread.IsOnGameThread)
                Interlocked.Increment(ref LoginChecksOnWrongThread);
            return LoggedIn;
        }
    }

    public bool IsPermitted(ToolPermission permission) => Permissions.TryGetValue(permission, out var v) && v;

    public bool IsCategoryEnabled(string category) => !DisabledCategories.ContainsKey(category);
}

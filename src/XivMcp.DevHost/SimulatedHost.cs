using System.Collections.Concurrent;
using XivMcp.Core;

namespace XivMcp.DevHost;

/// <summary>
/// Stand-in for Dalamud's framework thread: one dedicated thread that drains queued work once per
/// "frame" (60 Hz), like IFramework.RunOnFrameworkThread does in game.
/// </summary>
public sealed class SimulatedFrameworkThread : IGameThread, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly TimeSpan _frame;
    private volatile bool _stopping;

    public SimulatedFrameworkThread(double framesPerSecond = 60)
    {
        _frame = TimeSpan.FromSeconds(1 / framesPerSecond);
        _thread = new Thread(Run) { IsBackground = true, Name = "SimulatedFramework" };
        _thread.Start();
    }

    public long FrameCount { get; private set; }

    public int ManagedThreadId => _thread.ManagedThreadId;

    public bool IsOnGameThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        if (IsOnGameThread)
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

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            tcs.SetCanceled(cancellationToken);
            return tcs.Task;
        }

        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        _queue.Add(() =>
        {
            registration.Dispose();
            if (tcs.Task.IsCompleted)
                return;
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
            return true;
        }, cancellationToken);

    private void Run()
    {
        while (!_stopping)
        {
            var frameStart = DateTime.UtcNow;
            FrameCount++;
            while (_queue.TryTake(out var work))
                work();
            var remaining = _frame - (DateTime.UtcNow - frameStart);
            if (remaining > TimeSpan.Zero)
            {
                if (_queue.TryTake(out var next, remaining))
                    next();
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _queue.Add(() => { });
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}

/// <summary>Mutable host state so tools can flip login/permissions at runtime.</summary>
public sealed class DevHostState : IHostState
{
    private readonly ConcurrentDictionary<ToolPermission, bool> _permissions = new()
    {
        [ToolPermission.Read] = true,
        [ToolPermission.Ui] = true,
        [ToolPermission.Action] = false,
        [ToolPermission.Chat] = false,
    };

    private readonly ConcurrentDictionary<string, bool> _disabledCategories = new(StringComparer.Ordinal);

    public volatile bool LoggedIn = true;

    public bool IsLoggedIn => LoggedIn;

    public bool IsPermitted(ToolPermission permission) => _permissions.TryGetValue(permission, out var allowed) && allowed;

    public bool IsCategoryEnabled(string category) => !_disabledCategories.ContainsKey(category);

    public void SetPermission(ToolPermission permission, bool enabled) => _permissions[permission] = enabled;

    public void SetCategory(string category, bool enabled)
    {
        if (enabled)
            _disabledCategories.TryRemove(category, out _);
        else
            _disabledCategories[category] = true;
    }
}

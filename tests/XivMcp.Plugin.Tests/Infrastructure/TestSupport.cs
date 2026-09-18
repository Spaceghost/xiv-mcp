using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace XivMcp.Plugin.Tests.Infrastructure;

/// <summary>Resolves Dalamud, Lumina and friends from the XIVLauncher dev hooks directory the plugin was built against.</summary>
internal static class DalamudAssemblyResolver
{
    public static string LibPath { get; } = typeof(DalamudAssemblyResolver).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "DalamudLibPath")?.Value ?? "";

    [ModuleInitializer]
    internal static void Install()
    {
        AssemblyLoadContext.Default.Resolving += static (context, name) =>
        {
            if (string.IsNullOrEmpty(LibPath) || name.Name is null)
                return null;
            var path = Path.Combine(LibPath, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}

/// <summary>Interface fakes: named members return the handler's value, everything else returns default.</summary>
public class FakeProxy : DispatchProxy
{
    public Dictionary<string, Func<object?[]?, object?>> Handlers { get; set; } = new();

    public static T Create<T>(Dictionary<string, Func<object?[]?, object?>>? handlers = null)
        where T : class
    {
        var proxy = Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handlers = handlers ?? new();
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is not null && Handlers.TryGetValue(targetMethod.Name, out var handler))
            return handler(args);
        var type = targetMethod?.ReturnType;
        return type is null || type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
    }
}

/// <summary>A TimeProvider whose clock and timers only move when the test advances them.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return now;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            now += by;
            due = timers.Where(t => t.DueAt is { } at && at <= now).ToList();
            foreach (var t in due)
                t.DueAt = null;
        }

        foreach (var t in due)
            t.Callback(t.State);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (gate)
            timers.Add(timer);
        return timer;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (owner.gate)
                owner.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

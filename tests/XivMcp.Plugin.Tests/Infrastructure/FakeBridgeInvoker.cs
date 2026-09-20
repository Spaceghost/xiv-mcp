using XivMcp.Core;
using XivMcp.Plugin.Bridges;

namespace XivMcp.Plugin.Tests.Infrastructure;

/// <summary>IPC without Dalamud: gates are lambdas, an unregistered gate throws what the real invoker throws, every call is recorded.</summary>
public sealed class FakeBridgeInvoker : IBridgeInvoker
{
    public Dictionary<string, Func<string?, string>> Gates { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Func<string, bool>> BoolGates { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> IntGates { get; } = new(StringComparer.Ordinal);

    public HashSet<string> PostGates { get; } = new(StringComparer.Ordinal);

    public List<(string Gate, string? Argument)> Calls { get; } = [];

    public bool Loaded { get; set; } = true;

    public BridgeStatus? Probe(string key) =>
        BridgeCatalog.Find(key) is { } d
            ? new BridgeStatus(d.Key, d.DisplayName, d.Purpose, Loaded, Loaded, Loaded ? "1.0.0.0" : null, Loaded, Loaded ? "1" : null,
                Loaded ? null : $"{d.DisplayName} is not installed in this game client.", d.Verified)
            : null;

    public string Call(string gate) => Invoke(gate, null);

    public string Call(string gate, string argument) => Invoke(gate, argument);

    public string Call(string gate, int argument) => Invoke(gate, argument.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public bool CallBool(string gate, string argument)
    {
        Calls.Add((gate, argument));
        return BoolGates.TryGetValue(gate, out var f) ? f(argument) : throw new BridgeCapabilityMissingException(gate);
    }

    public int CallInt(string gate)
    {
        Calls.Add((gate, null));
        return IntGates.TryGetValue(gate, out var v) ? v : throw new BridgeCapabilityMissingException(gate);
    }

    public void Post(string gate, string argument)
    {
        Calls.Add((gate, argument));
        if (!PostGates.Contains(gate))
            throw new BridgeCapabilityMissingException(gate);
    }

    private string Invoke(string gate, string? argument)
    {
        Calls.Add((gate, argument));
        return Gates.TryGetValue(gate, out var f) ? f(argument) : throw new BridgeCapabilityMissingException(gate);
    }
}

/// <summary>Runs "framework thread" work inline and counts the hops.</summary>
public sealed class InlineGameThread : IGameThread
{
    public int Hops { get; private set; }

    public bool IsOnGameThread => true;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        Hops++;
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
        Hops++;
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

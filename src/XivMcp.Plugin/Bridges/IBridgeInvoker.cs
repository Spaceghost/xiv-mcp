using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Bridges;

/// <summary>
/// The seam between the typed bridge tools and Dalamud IPC. The real implementation is
/// <see cref="DalamudBridgeInvoker"/>; tests supply a fake, so everything above this interface (allow-lists, request
/// building, response parsing, layout reconstruction, the "sibling has not shipped this yet" path) runs without the game.
/// Every member throws <see cref="BridgeCapabilityMissingException"/> when the gate is not registered and
/// <see cref="McpToolException"/> when the other plugin failed; nothing else escapes.
/// </summary>
public interface IBridgeInvoker
{
    /// <summary>Status of a catalogued bridge, never throwing for an absent plugin. Null for an unknown key.</summary>
    BridgeStatus? Probe(string key);

    /// <summary>Func&lt;string&gt;.</summary>
    string Call(string gate);

    /// <summary>Func&lt;string, string&gt;.</summary>
    string Call(string gate, string argument);

    /// <summary>Func&lt;int, string&gt;.</summary>
    string Call(string gate, int argument);

    /// <summary>Func&lt;string, bool&gt;.</summary>
    bool CallBool(string gate, string argument);

    /// <summary>Func&lt;int&gt;.</summary>
    int CallInt(string gate);

    /// <summary>Action&lt;string&gt;: fire and forget, nothing comes back.</summary>
    void Post(string gate, string argument);
}

/// <summary>The other plugin is there but does not offer this gate (or, for a JSON call gate, this verb).</summary>
public sealed class BridgeCapabilityMissingException : Exception
{
    public BridgeCapabilityMissingException(string capability)
        : base($"{capability} is not offered by the other plugin.") => Capability = capability;

    public BridgeCapabilityMissingException(string capability, Exception inner)
        : base($"{capability} is not offered by the other plugin.", inner) => Capability = capability;

    /// <summary>The gate name, or "gate#verb".</summary>
    public string Capability { get; }
}

/// <summary>Helpers shared by the bridge engines for turning "cannot" into the right tool error.</summary>
public static class BridgeInvokerExtensions
{
    /// <summary>Throws <see cref="McpErrorCodes.Unavailable"/> with the registry's own reason unless the bridge answers IPC.</summary>
    public static BridgeStatus Require(this IBridgeInvoker invoker, string key)
    {
        var status = invoker.Probe(key)
                     ?? throw McpToolException.WithCode(McpErrorCodes.InvalidArguments,
                         $"Unknown bridge \"{key}\". Known bridges: {string.Join(", ", BridgeCatalog.Keys)}.");
        if (!status.IpcAvailable)
        {
            throw McpToolException.WithCode(McpErrorCodes.Unavailable,
                status.Unavailable ?? $"{status.Name} is not available.", retryable: true);
        }

        return status;
    }
}

/// <summary>Dalamud IPC behind <see cref="IBridgeInvoker"/>. Thin on purpose: no decisions live here.</summary>
public sealed class DalamudBridgeInvoker : IBridgeInvoker
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly BridgeRegistry registry;

    public DalamudBridgeInvoker(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        registry = new BridgeRegistry(pluginInterface, log);
    }

    public BridgeStatus? Probe(string key) => BridgeCatalog.Find(key) is { } definition ? registry.Probe(definition) : null;

    public string Call(string gate) => Guard(gate, () => pluginInterface.GetIpcSubscriber<string>(gate).InvokeFunc() ?? "");

    public string Call(string gate, string argument) =>
        Guard(gate, () => pluginInterface.GetIpcSubscriber<string, string>(gate).InvokeFunc(argument) ?? "");

    public string Call(string gate, int argument) =>
        Guard(gate, () => pluginInterface.GetIpcSubscriber<int, string>(gate).InvokeFunc(argument) ?? "");

    public bool CallBool(string gate, string argument) =>
        Guard(gate, () => pluginInterface.GetIpcSubscriber<string, bool>(gate).InvokeFunc(argument));

    public int CallInt(string gate) => Guard(gate, () => pluginInterface.GetIpcSubscriber<int>(gate).InvokeFunc());

    public void Post(string gate, string argument) =>
        Guard(gate, () =>
        {
            pluginInterface.GetIpcSubscriber<string, object>(gate).InvokeAction(argument);
            return 0;
        });

    private T Guard<T>(string gate, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (IpcNotReadyError ex)
        {
            throw new BridgeCapabilityMissingException(gate, ex);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Bridge call {Gate} failed", gate);
            throw new McpToolException($"{gate} did not answer ({ex.GetType().Name}: {BridgeJson.Clip(ex.Message, BridgeJson.MaxErrorChars)}).", ex);
        }
    }
}

using System.Globalization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XivMcp.Plugin.Bridges;

/// <summary>What XivMcp currently knows about one optional plugin.</summary>
/// <param name="Key">Catalog key.</param>
/// <param name="Name">Display name.</param>
/// <param name="Purpose">What the bridge is for.</param>
/// <param name="Installed">The plugin is installed in this client.</param>
/// <param name="Loaded">The plugin is loaded right now (IPC only works when it is).</param>
/// <param name="Version">Installed version, when known.</param>
/// <param name="IpcAvailable">The probe gate answered.</param>
/// <param name="ApiVersion">What the probe gate answered, as text.</param>
/// <param name="Unavailable">Why the bridge cannot be used, when it cannot.</param>
/// <param name="Verified">Whether the gate names were exercised against the real plugin by this project.</param>
public sealed record BridgeStatus(
    string Key,
    string Name,
    string Purpose,
    bool Installed,
    bool Loaded,
    string? Version,
    bool IpcAvailable,
    string? ApiVersion,
    string? Unavailable,
    bool Verified);

/// <summary>
/// Resolves the optional plugin bridges: is the plugin there, does it answer IPC, and what do its read-only
/// gates say. Every call into another plugin is wrapped: a missing or changed gate is reported, never thrown
/// into the game.
/// </summary>
public sealed class BridgeRegistry
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    public BridgeRegistry(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    /// <summary>Status of every known bridge.</summary>
    public List<BridgeStatus> ProbeAll() => BridgeCatalog.All.Select(Probe).ToList();

    /// <summary>Status of one bridge.</summary>
    public BridgeStatus Probe(BridgeDefinition definition)
    {
        var (installed, loaded, version) = FindPlugin(definition);
        if (definition.ProbeGate.Length == 0)
        {
            return new BridgeStatus(definition.Key, definition.DisplayName, definition.Purpose, installed, loaded, version,
                false, null, "This plugin has no inbound IPC XivMcp uses.", definition.Verified);
        }

        if (!installed)
        {
            return new BridgeStatus(definition.Key, definition.DisplayName, definition.Purpose, false, false, null,
                false, null, $"{definition.DisplayName} is not installed in this game client.", definition.Verified);
        }

        if (!loaded)
        {
            return new BridgeStatus(definition.Key, definition.DisplayName, definition.Purpose, true, false, version,
                false, null, $"{definition.DisplayName} is installed but not loaded; enable it in /xlplugins.", definition.Verified);
        }

        try
        {
            var api = definition.ProbeKind switch
            {
                BridgeProbeKind.IntPair => Describe(pluginInterface.GetIpcSubscriber<(int Breaking, int Feature)>(definition.ProbeGate).InvokeFunc()),
                BridgeProbeKind.Int => pluginInterface.GetIpcSubscriber<int>(definition.ProbeGate).InvokeFunc().ToString(CultureInfo.InvariantCulture),
                _ => pluginInterface.GetIpcSubscriber<string>(definition.ProbeGate).InvokeFunc(),
            };

            return new BridgeStatus(definition.Key, definition.DisplayName, definition.Purpose, true, true, version,
                true, api, null, definition.Verified);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Bridge probe failed for {Bridge}", definition.Key);
            return new BridgeStatus(definition.Key, definition.DisplayName, definition.Purpose, true, true, version,
                false, null,
                $"{definition.DisplayName} is loaded but did not answer {definition.ProbeGate} ({ex.GetType().Name}). " +
                "Its IPC surface may have changed in a newer version than this bridge was written against.",
                definition.Verified);
        }
    }

    /// <summary>
    /// Reads one of a bridge's read-only gates. Returns the value, or null with <paramref name="error"/> set.
    /// </summary>
    public object? Read(BridgeGate gate, out string? error)
    {
        error = null;
        try
        {
            switch (gate.Kind)
            {
                case BridgeGateKind.Bool:
                    return pluginInterface.GetIpcSubscriber<bool>(gate.Name).InvokeFunc();
                case BridgeGateKind.Int:
                    return pluginInterface.GetIpcSubscriber<int>(gate.Name).InvokeFunc();
                case BridgeGateKind.String:
                    return pluginInterface.GetIpcSubscriber<string>(gate.Name).InvokeFunc();
                case BridgeGateKind.StringList:
                    return pluginInterface.GetIpcSubscriber<IList<string>>(gate.Name).InvokeFunc()?.ToList();
                case BridgeGateKind.StringMap:
                    return pluginInterface.GetIpcSubscriber<Dictionary<string, string>>(gate.Name).InvokeFunc()
                        ?.Select(kv => new BridgeEntry(kv.Key, kv.Value)).ToList();
                case BridgeGateKind.GuidMap:
                    return pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>(gate.Name).InvokeFunc()
                        ?.Select(kv => new BridgeEntry(kv.Key.ToString(), kv.Value)).ToList();
                default:
                    error = $"Gate {gate.Name} has a shape this bridge cannot read.";
                    return null;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Bridge gate {Gate} failed", gate.Name);
            error = $"{gate.Name} did not answer ({ex.GetType().Name}: {ex.Message}).";
            return null;
        }
    }

    /// <summary>Calls a string → string gate, e.g. GhosttyDalamud.v1.Call or XivDesktop.v1.Launch.</summary>
    public string Invoke(string gate, string argument)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<string, string>(gate).InvokeFunc(argument) ?? "";
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Bridge call {Gate} failed", gate);
            throw new BridgeCallException($"{gate} did not answer ({ex.GetType().Name}: {ex.Message}).", ex);
        }
    }

    /// <summary>Calls an int → string gate, e.g. XivDesktop.v1.Workspace.</summary>
    public string Invoke(string gate, int argument)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<int, string>(gate).InvokeFunc(argument) ?? "";
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Bridge call {Gate} failed", gate);
            throw new BridgeCallException($"{gate} did not answer ({ex.GetType().Name}: {ex.Message}).", ex);
        }
    }

    /// <summary>Calls a no-argument string gate.</summary>
    public string Invoke(string gate)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<string>(gate).InvokeFunc() ?? "";
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Bridge call {Gate} failed", gate);
            throw new BridgeCallException($"{gate} did not answer ({ex.GetType().Name}: {ex.Message}).", ex);
        }
    }

    /// <summary>Throws a clear <see cref="Core.McpToolException"/> when a bridge cannot be used.</summary>
    public BridgeStatus Require(string? key)
    {
        var definition = BridgeCatalog.Find(key)
                         ?? throw new Core.McpToolException(
                             $"Unknown bridge \"{key}\". Known bridges: {string.Join(", ", BridgeCatalog.Keys)}.");
        var status = Probe(definition);
        if (!status.IpcAvailable)
        {
            throw new Core.McpToolException(status.Unavailable ?? $"{definition.DisplayName} is not available.");
        }

        return status;
    }

    private (bool Installed, bool Loaded, string? Version) FindPlugin(BridgeDefinition definition)
    {
        try
        {
            foreach (var plugin in pluginInterface.InstalledPlugins)
            {
                if (!definition.InternalNames.Any(n =>
                        n.Equals(plugin.InternalName, StringComparison.OrdinalIgnoreCase) ||
                        n.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                return (true, plugin.IsLoaded, plugin.Version?.ToString());
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not enumerate installed plugins");
        }

        return (false, false, null);
    }

    private static string Describe((int Breaking, int Feature) version) => $"{version.Breaking}.{version.Feature}";
}

/// <summary>One id/name pair read from a bridge.</summary>
public sealed record BridgeEntry(string Id, string Name);

/// <summary>A bridge call that failed on the other plugin's side.</summary>
public sealed class BridgeCallException : Exception
{
    public BridgeCallException(string message, Exception inner) : base(message, inner) { }
}

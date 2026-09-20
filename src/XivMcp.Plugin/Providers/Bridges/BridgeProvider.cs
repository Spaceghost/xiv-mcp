using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Bridges;

namespace XivMcp.Plugin.Providers.Bridges;

/// <summary>
/// Optional bridges to the player's other Dalamud plugins. Every tool here degrades to a clear "not
/// available" instead of failing hard, and only reads: anything that changes another plugin's state lives in
/// the Action-tier tools of the specific bridge provider and goes through the normal approval path.
/// </summary>
[McpProvider("bridges")]
public sealed class BridgeProvider
{
    private readonly BridgeRegistry registry;

    public BridgeProvider(IDalamudPluginInterface pluginInterface, IPluginLog log) =>
        registry = new BridgeRegistry(pluginInterface, log);

    public sealed record BridgeListDto(int Total, int Available, List<BridgeStatus> Bridges, string Note);

    public sealed record BridgeValueDto(string Field, string Description, object? Value, string? Error);

    public sealed record BridgeStateDto(BridgeStatus Bridge, List<BridgeValueDto> Values, string Note);

    [McpTool("list_bridges",
        Sources = ["dalamud:InstalledPlugins", "ipc:probe"],
        Title = "List plugin bridges",
        RequiresLogin = false,
        Description =
            "Every other Dalamud plugin XivMcp can read through IPC, with installed, loaded, version, ipcAvailable, apiVersion and " +
            "(when it cannot be used) unavailable explaining why — not installed, not loaded, or the plugin's IPC surface changed. " +
            "verified=false means the gate names come from that plugin's published IPC documentation and have not been exercised " +
            "against the real plugin by this project. Call this before any other bridge tool so you know what exists on this machine; " +
            "read-only bridge data needs no approval, while anything that changes another plugin's state is an Action-tier tool.")]
    public BridgeListDto ListBridges()
    {
        var bridges = registry.ProbeAll();
        return new BridgeListDto(
            bridges.Count,
            bridges.Count(b => b.IpcAvailable),
            bridges,
            "A bridge is only usable while the other plugin is loaded. Nothing here installs or enables anything.");
    }

    [McpTool("get_bridge_state",
        Sources = ["ipc:bridge"],
        Title = "Read a plugin bridge",
        RequiresLogin = false,
        Description =
            "Reads one bridge's read-only IPC gates and returns each as {field, description, value, error}: for Penumbra the enabled " +
            "state, mod list and collections; for Glamourer the saved design list; for Lifestream/AutoRetainer/Artisan/Deliveroo a busy " +
            "or running flag; for ghostty and xivdesktop their status JSON. A gate that does not answer sets error on that field instead " +
            "of failing the call. Fails only when the bridge itself is unavailable — check list_bridges first. " +
            "Lists (mods, designs) can be long; use limit to cap them.")]
    public BridgeStateDto GetBridgeState(
        [McpParam("Bridge key from list_bridges.", Enum = ["penumbra", "glamourer", "mare", "lifestream", "autoretainer", "artisan", "deliveroo", "ghostty", "xivdesktop", "almanac", "umbra"])]
        string bridge,
        [McpParam("Maximum entries kept from any list-shaped value (1-500).", Minimum = 1, Maximum = 500)] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        var status = registry.Require(bridge);
        var definition = BridgeCatalog.Find(bridge)!;

        var values = new List<BridgeValueDto>();
        foreach (var gate in definition.ReadGates)
        {
            var value = registry.Read(gate, out var error);
            values.Add(new BridgeValueDto(gate.Field, gate.Description, Cap(value, limit), error));
        }

        return new BridgeStateDto(
            status,
            values,
            definition.Verified
                ? "Gate names verified against the installed plugin."
                : "Gate names come from the other plugin's published IPC documentation; they have not been verified against a running copy.");
    }

    private static object? Cap(object? value, int limit) => value switch
    {
        List<BridgeEntry> entries when entries.Count > limit => entries.Take(limit).ToList(),
        List<string> strings when strings.Count > limit => strings.Take(limit).ToList(),
        _ => value,
    };
}

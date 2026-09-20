namespace XivMcp.Plugin.Bridges;

/// <summary>
/// One optional plugin XivMcp can talk to over Dalamud IPC. Everything here is static description: no
/// Dalamud types, no game memory, so the catalog is host-testable.
/// </summary>
/// <param name="Key">Stable lower-case key used as the tool argument, e.g. "penumbra".</param>
/// <param name="DisplayName">Name a human recognises.</param>
/// <param name="InternalNames">Dalamud internal plugin names that count as this bridge (first is preferred).</param>
/// <param name="Purpose">One line: what XivMcp can do through it.</param>
/// <param name="ProbeGate">An IPC gate that answers a version, used to check the plugin really exposes IPC.</param>
/// <param name="ProbeKind">Shape of <paramref name="ProbeGate"/>: intPair, int or string.</param>
/// <param name="ReadGates">Read-only gates this bridge offers, gate name → what it returns.</param>
/// <param name="Verified">
/// True only when the gate names and signatures were exercised against the real plugin on a developer's
/// machine. Everything else comes from the other plugin's published IPC documentation.
/// </param>
public sealed record BridgeDefinition(
    string Key,
    string DisplayName,
    IReadOnlyList<string> InternalNames,
    string Purpose,
    string ProbeGate,
    BridgeProbeKind ProbeKind,
    IReadOnlyList<BridgeGate> ReadGates,
    bool Verified = false);

/// <summary>Shape of a bridge's version gate.</summary>
public enum BridgeProbeKind
{
    /// <summary>Func&lt;(int breaking, int feature)&gt;.</summary>
    IntPair,

    /// <summary>Func&lt;int&gt;.</summary>
    Int,

    /// <summary>Func&lt;string&gt;.</summary>
    String,
}

/// <summary>A read-only gate: its name, what it answers and the shape XivMcp reads it as.</summary>
/// <param name="Name">Full IPC gate name.</param>
/// <param name="Field">Key the value appears under in the tool result.</param>
/// <param name="Description">What the value means.</param>
/// <param name="Kind">How XivMcp calls it.</param>
public sealed record BridgeGate(string Name, string Field, string Description, BridgeGateKind Kind);

/// <summary>Return shapes XivMcp knows how to read from a bridge without knowing the plugin's types.</summary>
public enum BridgeGateKind
{
    /// <summary>Func&lt;bool&gt;.</summary>
    Bool,

    /// <summary>Func&lt;int&gt;.</summary>
    Int,

    /// <summary>Func&lt;string&gt;.</summary>
    String,

    /// <summary>Func&lt;Dictionary&lt;string, string&gt;&gt; — returned as a list of {id, name}.</summary>
    StringMap,

    /// <summary>Func&lt;Dictionary&lt;Guid, string&gt;&gt; — returned as a list of {id, name}.</summary>
    GuidMap,

    /// <summary>Func&lt;IList&lt;string&gt;&gt;.</summary>
    StringList,
}

/// <summary>The bridges XivMcp knows about.</summary>
public static class BridgeCatalog
{
    /// <summary>Every known bridge, in the order <c>list_bridges</c> reports them.</summary>
    public static IReadOnlyList<BridgeDefinition> All { get; } =
    [
        new(
            "penumbra",
            "Penumbra",
            ["Penumbra"],
            "List the player's mods and collections (read-only).",
            "Penumbra.ApiVersion",
            BridgeProbeKind.IntPair,
            [
                new("Penumbra.GetEnabledState", "enabled", "Whether Penumbra is currently applying mods.", BridgeGateKind.Bool),
                new("Penumbra.GetModList", "mods", "Every installed mod as directory → display name.", BridgeGateKind.StringMap),
                new("Penumbra.GetCollections", "collections", "Every collection as id → name.", BridgeGateKind.GuidMap),
            ]),
        new(
            "glamourer",
            "Glamourer",
            ["Glamourer"],
            "List saved designs; applying one to the player needs in-game approval.",
            "Glamourer.ApiVersions",
            BridgeProbeKind.IntPair,
            [
                new("Glamourer.GetDesignList.V2", "designs", "Saved designs as id → name.", BridgeGateKind.GuidMap),
            ]),
        new(
            "mare",
            "Mare Synchronos",
            ["MareSynchronos"],
            "Report whether the sync service is connected.",
            "MareSynchronos.GetApiVersion",
            BridgeProbeKind.Int,
            [
                new("MareSynchronos.GetHandledAddresses", "handledPlayers", "Players Mare is currently drawing.", BridgeGateKind.StringList),
            ]),
        new(
            "lifestream",
            "Lifestream",
            ["Lifestream"],
            "Report travel state; starting travel needs in-game approval.",
            "Lifestream.ApiVersion",
            BridgeProbeKind.Int,
            [
                new("Lifestream.IsBusy", "busy", "Whether a travel sequence is running.", BridgeGateKind.Bool),
            ]),
        new(
            "autoretainer",
            "AutoRetainer",
            ["AutoRetainer"],
            "Report whether retainer automation is running and how many retainers are ready.",
            "AutoRetainer.Ready",
            BridgeProbeKind.Int,
            [
                new("AutoRetainer.IsBusy", "busy", "Whether AutoRetainer is in a sequence.", BridgeGateKind.Bool),
            ]),
        new(
            "artisan",
            "Artisan",
            ["Artisan"],
            "Report crafting-list and endurance state.",
            "Artisan.GetEnduranceStatus",
            BridgeProbeKind.Int,
            [
                new("Artisan.IsListRunning", "listRunning", "Whether a crafting list is running.", BridgeGateKind.Bool),
                new("Artisan.IsBusy", "busy", "Whether Artisan is crafting.", BridgeGateKind.Bool),
            ]),
        new(
            "deliveroo",
            "Deliveroo",
            ["Deliveroo"],
            "Report whether a Grand Company turn-in is running.",
            "Deliveroo.IsTurnInRunning",
            BridgeProbeKind.Int,
            [
                new("Deliveroo.IsTurnInRunning", "turnInRunning", "Whether a turn-in is running.", BridgeGateKind.Bool),
            ]),
        new(
            "ghostty",
            "GhosttyDalamud",
            ["GhosttyDalamud"],
            "Read the terminal/panel list and status; opening or focusing a panel needs in-game approval.",
            "GhosttyDalamud.v1.Status",
            BridgeProbeKind.String,
            [
                new("GhosttyDalamud.v1.Status", "status", "One-line status text.", BridgeGateKind.String),
            ]),
        new(
            "xivdesktop",
            "XivDesktop",
            ["XivDesktop"],
            "Read the host desktop's app and window list; launching or acting on a window needs in-game approval.",
            "XivDesktop.v1.Status",
            BridgeProbeKind.String,
            [
                new("XivDesktop.v1.Status", "status", "Desktop bridge status JSON.", BridgeGateKind.String),
                new("XivDesktop.v1.Windows", "windows", "Window list JSON.", BridgeGateKind.String),
                new("XivDesktop.v1.ListApps", "apps", "Installed desktop applications JSON.", BridgeGateKind.String),
            ]),
        new(
            "umbra",
            "Umbra",
            ["Umbra"],
            "No inbound bridge: XivMcp publishes its own IPC gates and the Umbra widget consumes them (see docs/UMBRA.md).",
            "",
            BridgeProbeKind.String,
            []),
    ];

    /// <summary>Case-insensitive lookup by <see cref="BridgeDefinition.Key"/>.</summary>
    public static BridgeDefinition? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(b => b.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Every key, for error messages and schema enums.</summary>
    public static string[] Keys { get; } = All.Select(b => b.Key).ToArray();
}

namespace XivMcp.Plugin.Providers.Meta;

// Shared by the plugin and the standalone host (no Dalamud types): both answer get_server_info with this shape.

public sealed record PermissionTiersDto(
    bool Read,
    bool Ui,
    bool Action,
    bool Chat,
    bool ConfirmActions,
    string? Note);

public sealed record CategoryInfoDto(string Name, bool Enabled, int Tools, int FailedProviders);

public sealed record ServerInfoDto(
    string Name,
    string Version,
    string Endpoint,
    string Transport,
    bool Running,
    DateTimeOffset? StartedAt,
    int ActiveSessions,
    IReadOnlyList<string> ConnectedClients,
    string? YourClient,
    string? YourSessionId,
    PermissionTiersDto Permissions,
    int RegisteredTools,
    int AvailableTools,
    IReadOnlyList<CategoryInfoDto> Categories,
    string? DalamudVersion,
    string? GameVersion,
    string ClientLanguage,
    bool LoggedIn,
    string? ProtocolVersion)
{
    /// <summary>"plugin" inside the running game, "standalone" for the pre-game host.</summary>
    public string Host { get; init; } = "plugin";

    /// <summary>False on the standalone host: only tools with availability "static" work there.</summary>
    public bool GameRunning { get; init; } = true;

    /// <summary>Version of the tool catalogue (docs/tools.json).</summary>
    public int CatalogueVersion { get; init; }

    /// <summary>How many registered tools need only the installed game data.</summary>
    public int StaticTools { get; init; }

    /// <summary>Calls per minute allowed per client; 0 means unlimited.</summary>
    public int RateLimitPerMinute { get; init; }
}

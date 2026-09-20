namespace XivMcp.Plugin.Services;

/// <summary>Result of <see cref="ClientConnector.Connect"/>: a token for <see cref="ClientName"/>, or an <see cref="Error"/> code.</summary>
public readonly record struct ClientConnectResult(string? ClientName, string? Token, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Token issuance behind the XivMcp.ConnectClient IPC gate, on a <see cref="Configuration"/> only (the caller saves it and
/// hands the new hashes to the server). A fresh token replaces any existing token of the same name, so a companion
/// plugin can reconnect after losing its token; auto-approve rules name the client, not the token, and keep matching.
/// </summary>
public static class ClientConnector
{
    public const string CreatedViaIpc = "ipc";

    public const string ErrorDisabled = "disabled";

    public const string ErrorInvalidName = "invalid_client_name";

    public static ClientConnectResult Connect(Configuration config, string? clientName, DateTimeOffset now)
    {
        if (!config.AllowIpcClientTokens)
            return new(null, null, ErrorDisabled);
        var name = (clientName ?? "").Trim();
        if (!AutoApprovePolicy.IsValidClientName(name))
            return new(null, null, ErrorInvalidName);

        // Keep an existing token's spelling so rules that name it keep matching; AddClientToken rejects duplicates
        // case-insensitively, so revoke every spelling first.
        name = config.ClientTokens.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Name ?? name;
        foreach (var existing in config.ClientTokens.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).ToList())
            config.RevokeClientToken(existing);
        var token = config.AddClientToken(name, now, CreatedViaIpc);
        return new(name, token, null);
    }
}

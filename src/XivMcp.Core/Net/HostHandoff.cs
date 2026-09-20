using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Core.Net;

/// <summary>Who answered on the MCP port. See <see cref="HostHandoff"/>.</summary>
public enum HostKind
{
    /// <summary>Nothing answered, or it was not an xiv-mcp host.</summary>
    Unknown,

    /// <summary>The Dalamud plugin inside the running game.</summary>
    Plugin,

    /// <summary>The standalone host (static game data only).</summary>
    Standalone,
}

/// <summary>
/// The hand-off between the two hosts that share one endpoint. Both serve <c>GET {path}/host</c> (who am I) and the
/// standalone also serves <c>POST {path}/handoff</c>: it stops listening so the plugin, which has the live game, can bind
/// the same port; afterwards the standalone keeps trying to bind again and gets the port back when the game exits.
/// The plugin never proxies and the standalone never proxies: whoever holds the port answers, so a client only ever
/// needs one URL and one token. Both routes sit behind the endpoint's own Host, Origin and bearer checks, and a
/// hand-off is only honoured from a loopback peer.
/// </summary>
public static class HostHandoff
{
    public const string HostRoute = "host";

    public const string HandoffRoute = "handoff";

    /// <summary>The identity document both hosts answer on <see cref="HostRoute"/>.</summary>
    public static JsonObject Identity(HostKind kind, string version, bool gameRunning) => new()
    {
        ["server"] = "xiv-mcp",
        ["host"] = kind == HostKind.Plugin ? "plugin" : "standalone",
        ["version"] = version,
        ["catalogueVersion"] = McpServer.CatalogueVersion,
        ["gameRunning"] = gameRunning,
    };

    /// <summary>Asks whoever listens on <paramref name="endpoint"/> what it is. Never throws.</summary>
    public static async Task<HostKind> ProbeAsync(Uri endpoint, string? bearerToken, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        var body = await SendAsync(HttpMethod.Get, Route(endpoint, HostRoute), bearerToken, timeout, handler).ConfigureAwait(false);
        return KindOf(body);
    }

    /// <summary>
    /// Asks a standalone host on <paramref name="endpoint"/> to release the port. True when it said it is yielding; the
    /// caller then retries its own bind for a moment (see <see cref="RetryAsync"/>). Never throws.
    /// </summary>
    public static async Task<bool> RequestYieldAsync(Uri endpoint, string? bearerToken, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        var body = await SendAsync(HttpMethod.Post, Route(endpoint, HandoffRoute), bearerToken, timeout, handler).ConfigureAwait(false);
        return KindOf(body) == HostKind.Standalone && body?["yielding"]?.GetValueKind() == JsonValueKind.True;
    }

    /// <summary>Runs <paramref name="attempt"/> until it succeeds or <paramref name="attempts"/> are used up, <paramref name="delay"/> apart. Rethrows the last failure.</summary>
    public static async Task RetryAsync(Func<Task> attempt, int attempts, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        for (var i = 1; ; i++)
        {
            try
            {
                await attempt().ConfigureAwait(false);
                return;
            }
            catch (Exception) when (i < attempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static HostKind KindOf(JsonObject? body) =>
        body?["server"]?.GetValue<string>() != "xiv-mcp" ? HostKind.Unknown
        : body["host"]?.GetValue<string>() switch
        {
            "plugin" => HostKind.Plugin,
            "standalone" => HostKind.Standalone,
            _ => HostKind.Unknown,
        };

    private static Uri Route(Uri endpoint, string route) =>
        new UriBuilder(endpoint) { Path = endpoint.AbsolutePath.TrimEnd('/') + "/" + route }.Uri;

    private static async Task<JsonObject?> SendAsync(HttpMethod method, Uri uri, string? bearerToken, TimeSpan timeout, HttpMessageHandler? handler)
    {
        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = timeout;
            using var request = new HttpRequestMessage(method, uri);
            if (!string.IsNullOrEmpty(bearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            if (method == HttpMethod.Post)
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }
}

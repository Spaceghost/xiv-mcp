using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XivMcp.Core.Http;
using XivMcp.Core.Protocol;

namespace XivMcp.Core;

public sealed partial class McpServer
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, AllowDuplicateProperties = false };

    private static readonly HashSet<string> ModernMethods = new(StringComparer.Ordinal)
    {
        "server/discover", "tools/list", "tools/call", "resources/list", "resources/templates/list", "resources/read",
        "prompts/list", "prompts/get", "completion/complete", "subscriptions/listen",
    };

    private static readonly HashSet<string> LegacyMethods = new(StringComparer.Ordinal)
    {
        "initialize", "ping", "tools/list", "tools/call", "resources/list", "resources/templates/list", "resources/read",
        "resources/subscribe", "resources/unsubscribe", "prompts/list", "prompts/get", "completion/complete", "logging/setLevel",
    };

    private TimeSpan SseKeepAlive => Options.SseKeepAliveInterval > TimeSpan.Zero ? Options.SseKeepAliveInterval : TimeSpan.FromSeconds(15);

    private async Task<bool> HandleHttpAsync(HttpConnection connection, HttpRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        var path = request.Path.Length > 1 ? request.Path.TrimEnd('/') : request.Path;
        string? controlPath = null;
        if (ControlHandler is not null && path.Length > _path.Length + 1 && path.StartsWith(_path, StringComparison.Ordinal) && path[_path.Length] == '/')
            controlPath = path[(_path.Length + 1)..];
        if (controlPath is null && !string.Equals(path, _path, StringComparison.Ordinal))
        {
            await connection.SendErrorAsync(request, 404, $"Not found. The MCP endpoint is {_path}", close: false).ConfigureAwait(false);
            return true;
        }

        var headers = new List<KeyValuePair<string, string>>(6);

        // DNS-rebinding defence: answer only to Host headers naming loopback, an address this server
        // actually bound, or a name the owner allowed (its MagicDNS name, say). Switched off only for a
        // wildcard bind, where there is no address list to check against.
        if (_checkHostHeader && request.Headers.Get("Host") is { } hostHeader && !IsAllowedHost(hostHeader))
        {
            RecordRejection(request, started, "403", $"Host '{hostHeader}' is not allowed");
            await SendJsonErrorAsync(connection, request, 403, null, JsonRpcCodes.InvalidRequest, "Forbidden: Host header is not an address this server listens on", headers).ConfigureAwait(false);
            return true;
        }

        var origin = request.Headers.Get("Origin");
        if (origin is not null)
        {
            if (!IsAllowedOrigin(origin))
            {
                RecordRejection(request, started, "403", $"Origin '{origin}' is not allowed");
                await SendJsonErrorAsync(connection, request, 403, null, JsonRpcCodes.InvalidRequest, "Forbidden: Origin not allowed", headers).ConfigureAwait(false);
                return true;
            }

            headers.Add(new("Access-Control-Allow-Origin", origin));
            headers.Add(new("Vary", "Origin"));
            headers.Add(new("Access-Control-Expose-Headers", "Mcp-Session-Id, Mcp-Protocol-Version, WWW-Authenticate"));
        }

        if (request.Method == "OPTIONS")
        {
            headers.Add(new("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS"));
            var requested = request.Headers.Get("Access-Control-Request-Headers");
            headers.Add(new("Access-Control-Allow-Headers",
                requested is not null && requested.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ',' or ' ')
                    ? requested
                    : "Content-Type, Accept, Authorization, Mcp-Session-Id, MCP-Protocol-Version, Last-Event-ID, Mcp-Method, Mcp-Name"));
            headers.Add(new("Access-Control-Max-Age", "600"));
            await connection.SendResponseAsync(request, 204, headers, null, ReadOnlySpan<byte>.Empty, close: false).ConfigureAwait(false);
            return true;
        }

        if (!IsAuthorized(request, out var presented, out var tokenClient))
        {
            RecordRejection(request, started, "401", presented ? "Invalid bearer token" : "Missing bearer token");
            headers.Add(new("WWW-Authenticate", presented ? "Bearer realm=\"xiv-mcp\", error=\"invalid_token\"" : "Bearer realm=\"xiv-mcp\""));
            await SendJsonErrorAsync(connection, request, 401, null, JsonRpcCodes.InvalidRequest,
                presented ? "Unauthorized: invalid bearer token" : "Unauthorized: send Authorization: Bearer <token>", headers).ConfigureAwait(false);
            return true;
        }

        request.AuthenticatedClient = tokenClient;
        if (controlPath is not null)
            return await HandleControlAsync(connection, request, controlPath, headers).ConfigureAwait(false);

        switch (request.Method)
        {
            case "POST":
                return await HandlePostAsync(connection, request, headers).ConfigureAwait(false);
            case "GET":
                return await HandleGetAsync(connection, request, headers).ConfigureAwait(false);
            case "DELETE":
                return await HandleDeleteAsync(connection, request, headers).ConfigureAwait(false);
            default:
                headers.Add(new("Allow", "GET, POST, DELETE, OPTIONS"));
                await connection.SendErrorAsync(request, 405, "Method not allowed", close: false, headers).ConfigureAwait(false);
                return true;
        }
    }

    /// <summary>Routes below the endpoint path (<see cref="ControlHandler"/>), reached only after the Host, Origin and bearer checks.</summary>
    private async Task<bool> HandleControlAsync(HttpConnection connection, HttpRequest request, string subPath, List<KeyValuePair<string, string>> headers)
    {
        ControlResponse? response = null;
        try
        {
            JsonNode? body = null;
            if (request.Body.Length > 0)
            {
                try
                {
                    body = JsonNode.Parse(request.Body, null, DocumentOptions);
                }
                catch (JsonException)
                {
                    body = null;
                }
            }

            if (ControlHandler is { } handler)
                response = await handler(new ControlRequest(request.Method, subPath, request.AuthenticatedClient, connection.PeerIsThisMachine, body)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSink($"control route '{subPath}' failed", ex);
            await SendJsonAsync(connection, request, 500, new JsonObject { ["error"] = "internal_error" }, headers).ConfigureAwait(false);
            return true;
        }

        if (response is null)
        {
            await connection.SendErrorAsync(request, 404, $"Not found. The MCP endpoint is {_path}", close: false).ConfigureAwait(false);
            return true;
        }

        await SendJsonAsync(connection, request, response.Status, response.Body, headers).ConfigureAwait(false);
        return true;
    }

    private void RecordRejection(HttpRequest request, long started, string status, string reason) =>
        RecordActivity(null, null, "http " + request.Method, status, false, reason, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private bool IsAllowedHost(string hostHeader) => IsAllowedHostHeader(hostHeader, _allowedHosts);

    /// <summary>
    /// The DNS-rebinding rule, split out so it can be tested per bind mode: strip the port, then accept
    /// a loopback name or a name in <paramref name="allowed"/> (the bound addresses, the owner's extra
    /// names and the hosts of the allowed origins).
    /// </summary>
    internal static bool IsAllowedHostHeader(string hostHeader, IReadOnlySet<string> allowed)
    {
        var host = StripPort(hostHeader);
        return IsLoopbackHostName(host) || allowed.Contains(host);
    }

    /// <summary>The host part of a <c>Host</c> header value, without the port and without IPv6 brackets.</summary>
    internal static string StripPort(string hostHeader)
    {
        var host = hostHeader.Trim();
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            return end > 0 ? host[1..end] : host;
        }

        var colon = host.LastIndexOf(':');
        return colon > 0 ? host[..colon] : host;
    }

    private bool IsAllowedOrigin(string origin)
    {
        if (origin == "null")
            return false;
        var normalized = NormalizeOrigin(origin);
        if (normalized is null)
            return false;
        if (_allowedOrigins.Contains(normalized.Value.Origin))
            return true;
        return (origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
               && IsLoopbackHostName(normalized.Value.Host);
    }

    private bool IsAuthorized(HttpRequest request, out bool presented, out string? tokenClient)
    {
        tokenClient = null;
        var auth = request.Headers.Get("Authorization");
        presented = auth is not null;
        byte[]? actual = null;
        if (auth is not null && auth.Length >= 7 && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            actual = SHA256.HashData(Encoding.UTF8.GetBytes(auth[7..].Trim()));
            tokenClient = ClientTokenName(actual);
        }

        var expected = _tokenHash;
        if (expected is null)
            return true;
        if (actual is null)
            return false;
        return tokenClient is not null || CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>The client whose per-client token hashes to <paramref name="hash"/>, or null. Read live, so revocation is immediate.</summary>
    private string? ClientTokenName(byte[] hash)
    {
        string? match = null;
        foreach (var token in Options.ClientTokens)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(token.Sha256Hex);
            }
            catch (FormatException)
            {
                continue;
            }

            if (expected.Length == hash.Length && CryptographicOperations.FixedTimeEquals(expected, hash) && !string.IsNullOrEmpty(token.Name))
                match = token.Name;
        }

        return match;
    }

    private static (bool Json, bool Sse) ParseAccept(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept))
            return (true, true);
        bool json = false, sse = false;
        foreach (var part in accept.Split(','))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries);
            var media = segments[0].ToLowerInvariant();
            if (segments.Skip(1).Any(s => s.Replace(" ", "", StringComparison.Ordinal) is "q=0" or "q=0.0" or "q=0.00" or "q=0.000"))
                continue;
            json |= media is "application/json" or "application/*" or "*/*";
            sse |= media is "text/event-stream" or "text/*" or "*/*";
        }

        return (json, sse);
    }

    private static ValueTask SendJsonAsync(HttpConnection connection, HttpRequest request, int status, JsonNode body, List<KeyValuePair<string, string>> headers) =>
        connection.SendResponseAsync(request, status, headers, "application/json", JsonRpc.Serialize(body), close: false);

    private static ValueTask SendJsonErrorAsync(HttpConnection connection, HttpRequest request, int status, JsonNode? id, int code, string message, List<KeyValuePair<string, string>> headers, JsonNode? data = null) =>
        SendJsonAsync(connection, request, status, JsonRpc.Error(id, code, message, data), headers);

    private async Task<bool> HandlePostAsync(HttpConnection connection, HttpRequest request, List<KeyValuePair<string, string>> headers)
    {
        var contentType = request.Headers.Get("Content-Type");
        if (contentType is null || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await SendJsonErrorAsync(connection, request, 415, null, JsonRpcCodes.InvalidRequest, "Content-Type must be application/json", headers).ConfigureAwait(false);
            return true;
        }

        var (acceptsJson, acceptsSse) = ParseAccept(request.Headers.Get("Accept"));
        if (!acceptsJson && !acceptsSse)
        {
            await SendJsonErrorAsync(connection, request, 406, null, JsonRpcCodes.InvalidRequest, "Not Acceptable: client must accept application/json and text/event-stream", headers).ConfigureAwait(false);
            return true;
        }

        // JsonNode reads strings lazily, so a malformed byte would only surface later, as a 500
        if (!System.Text.Unicode.Utf8.IsValid(request.Body))
        {
            await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.ParseError, "Parse error: the body is not valid UTF-8", headers).ConfigureAwait(false);
            return true;
        }

        // before the parse: with duplicate keys refused the parser unescapes names, and would report this less clearly
        if (!HasWellFormedStrings(request.Body))
        {
            await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.ParseError, "Parse error: a string contains an unpaired UTF-16 surrogate escape", headers).ConfigureAwait(false);
            return true;
        }

        JsonNode? body;
        try
        {
            body = JsonNode.Parse(request.Body, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.ParseError, "Parse error: " + ex.Message, headers).ConfigureAwait(false);
            return true;
        }

        var sessionId = request.Headers.Get("Mcp-Session-Id");

        if (body is JsonArray batch)
            return await HandleBatchAsync(connection, request, batch, sessionId, headers).ConfigureAwait(false);

        var message = JsonRpcMessage.Classify(body);
        if (message.Kind == JsonRpcKind.Invalid)
        {
            await SendJsonErrorAsync(connection, request, 400, message.Id, JsonRpcCodes.InvalidRequest, "Invalid Request: " + message.InvalidReason, headers).ConfigureAwait(false);
            return true;
        }

        if (message.Kind == JsonRpcKind.Request && message.Method == "initialize")
            return await HandleInitializeAsync(connection, request, message, acceptsJson, acceptsSse, headers).ConfigureAwait(false);

        var meta = JsonRpc.GetObject(message.Params, "_meta");
        var metaVersion = JsonRpc.GetString(meta, MetaKeys.ProtocolVersion);

        // A 2026-07-28 request carrying a stale/foreign session id (e.g. forwarded by a proxy) is served statelessly;
        // a live legacy session id always selects legacy semantics.
        if (sessionId is not null && ProtocolVersions.IsModern(metaVersion) && !_sessions.ContainsKey(sessionId))
            sessionId = null;

        if (sessionId is not null)
        {
            var session = await ResolveSessionAsync(connection, request, sessionId, message.Id, headers).ConfigureAwait(false);
            if (session is null)
                return true;

            switch (message.Kind)
            {
                case JsonRpcKind.Notification:
                    HandleLegacyNotification(session, message);
                    await connection.SendResponseAsync(request, 202, headers, null, ReadOnlySpan<byte>.Empty, close: false).ConfigureAwait(false);
                    return true;
                case JsonRpcKind.Response:
                    await connection.SendResponseAsync(request, 202, headers, null, ReadOnlySpan<byte>.Empty, close: false).ConfigureAwait(false);
                    return true;
                default:
                    return await HandleLegacyRequestAsync(connection, request, session, message, acceptsJson, acceptsSse, headers).ConfigureAwait(false);
            }
        }

        switch (message.Kind)
        {
            case JsonRpcKind.Notification:
                // 2026-07-28 defines no client notifications over HTTP (closing the stream cancels); accept and ignore.
                await connection.SendResponseAsync(request, 202, headers, null, ReadOnlySpan<byte>.Empty, close: false).ConfigureAwait(false);
                return true;
            case JsonRpcKind.Response:
                await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.InvalidRequest, "Invalid Request: clients must not send JSON-RPC responses to this server", headers).ConfigureAwait(false);
                return true;
        }

        var headerVersion = request.Headers.Get("MCP-Protocol-Version");
        if (metaVersion is null && (headerVersion is null || ProtocolVersions.IsLegacy(headerVersion)))
        {
            await SendJsonErrorAsync(connection, request, 400, message.Id, JsonRpcCodes.InvalidRequest,
                "Bad Request: missing Mcp-Session-Id header. Legacy clients (protocol 2025-11-25 and earlier) must call initialize first; " +
                $"2026-07-28 clients must send _meta[\"{MetaKeys.ProtocolVersion}\"] on every request.", headers).ConfigureAwait(false);
            return true;
        }

        return await HandleModernRequestAsync(connection, request, message, meta, metaVersion, headerVersion, acceptsJson, acceptsSse, headers).ConfigureAwait(false);
    }

    private async Task<LegacySession?> ResolveSessionAsync(HttpConnection connection, HttpRequest request, string sessionId, JsonNode? id, List<KeyValuePair<string, string>> headers)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.IsClosed)
        {
            await SendJsonErrorAsync(connection, request, 404, id, JsonRpcCodes.SessionNotFound, "Session not found: it expired, was deleted, or the server restarted. Send initialize again.", headers).ConfigureAwait(false);
            return null;
        }

        var headerVersion = request.Headers.Get("MCP-Protocol-Version");
        if (headerVersion is not null && headerVersion != session.ProtocolVersion)
        {
            await SendJsonErrorAsync(connection, request, 400, id, JsonRpcCodes.InvalidRequest,
                $"Bad Request: MCP-Protocol-Version '{headerVersion}' does not match the version negotiated for this session ('{session.ProtocolVersion}')", headers).ConfigureAwait(false);
            return null;
        }

        session.Touch();
        return session;
    }

    private async Task<bool> HandleInitializeAsync(HttpConnection connection, HttpRequest request, JsonRpcMessage message, bool acceptsJson, bool acceptsSse, List<KeyValuePair<string, string>> headers)
    {
        var started = Stopwatch.GetTimestamp();
        var requested = JsonRpc.GetString(message.Params, "protocolVersion");
        if (requested is null)
        {
            RecordActivity(null, null, "initialize", null, false, "missing protocolVersion", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await SendJsonErrorAsync(connection, request, 400, message.Id, JsonRpcCodes.InvalidParams, "initialize requires params.protocolVersion", headers).ConfigureAwait(false);
            return true;
        }

        var negotiated = ProtocolVersions.IsLegacy(requested) ? requested : ProtocolVersions.LatestLegacy;
        var clientInfo = JsonRpc.GetObject(message.Params, "clientInfo");
        var clientName = JsonRpc.GetString(clientInfo, "name");
        var clientVersion = JsonRpc.GetString(clientInfo, "version");
        var capabilities = JsonRpc.GetObject(message.Params, "capabilities")?.DeepClone() as JsonObject;

        EvictSessionsIfFull();
        var session = new LegacySession(RandomNumberGenerator.GetHexString(32, lowercase: true), negotiated, clientName, clientVersion, capabilities);
        _sessions[session.Id] = session;

        var result = new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = CapabilitiesJson(),
            ["serverInfo"] = ServerInfoJson(negotiated),
        };
        if (!string.IsNullOrWhiteSpace(Options.Instructions))
            result["instructions"] = Options.Instructions;

        headers.Add(new("Mcp-Session-Id", session.Id));
        var response = JsonRpc.Result(message.Id, result);
        RecordActivity(session.Id, ClientLabel(clientName, clientVersion), "initialize", negotiated, true, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        LogSink($"session {session.Id[..8]}… opened by {ClientLabel(clientName, clientVersion)} (requested {requested}, using {negotiated})", null);

        var responder = new PostResponder(connection, request, acceptsJson, acceptsSse, session, negotiated, headers, SseKeepAlive);
        await responder.CompleteAsync(response, 200).ConfigureAwait(false);
        return responder.KeepAlive;
    }

    private void HandleLegacyNotification(LegacySession session, JsonRpcMessage message)
    {
        switch (message.Method)
        {
            case "notifications/initialized":
                session.Initialized = true;
                break;
            case "notifications/cancelled":
                if (message.Params is not null && message.Params.TryGetPropertyValue("requestId", out var requestId) && requestId is not null &&
                    session.InFlight.TryGetValue(JsonRpc.IdKey(requestId), out var cts))
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (Exception ex)
                    {
                        LogSink("cancellation callback threw", ex);
                    }
                }

                break;
        }
    }

    private async Task<bool> HandleLegacyRequestAsync(HttpConnection connection, HttpRequest request, LegacySession session, JsonRpcMessage message, bool acceptsJson, bool acceptsSse, List<KeyValuePair<string, string>> headers)
    {
        var idKey = JsonRpc.IdKey(message.Id);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token, _serverCts.Token);
        if (!session.InFlight.TryAdd(idKey, cts))
        {
            await SendJsonErrorAsync(connection, request, 200, message.Id, JsonRpcCodes.InvalidRequest, $"Request id {idKey} is already in use by an in-flight request", headers).ConfigureAwait(false);
            return true;
        }

        var responder = new PostResponder(connection, request, acceptsJson, acceptsSse, session, session.ProtocolVersion, headers, SseKeepAlive);
        session.StreamOpened();
        try
        {
            var scope = new RequestScope
            {
                Era = Era.Legacy,
                ProtocolVersion = session.ProtocolVersion,
                Method = message.Method!,
                Id = message.Id,
                Params = message.Params,
                Session = session,
                ClientName = ClientLabel(session.ClientName, session.ClientVersion),
                AuthenticatedClient = request.AuthenticatedClient,
                ProgressToken = ProgressTokenOf(message.Params),
                CancellationToken = cts.Token,
                Outbound = responder,
            };

            var (response, status) = await DispatchAsync(scope).ConfigureAwait(false);
            session.InFlight.TryRemove(idKey, out _);
            if (response is null)
                await responder.CompleteWithoutResponseAsync(message.Id).ConfigureAwait(false);
            else
                await responder.CompleteAsync(response, status).ConfigureAwait(false);
        }
        finally
        {
            session.InFlight.TryRemove(idKey, out _);
            session.StreamClosed();
        }

        return responder.KeepAlive;
    }

    private static JsonNode? ProgressTokenOf(JsonObject? parameters)
    {
        var meta = JsonRpc.GetObject(parameters, "_meta");
        if (meta is null || !meta.TryGetPropertyValue(MetaKeys.ProgressToken, out var token) || token is not JsonValue v)
            return null;
        return v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number ? v.DeepClone() : null;
    }

    private async Task<bool> HandleModernRequestAsync(
        HttpConnection connection,
        HttpRequest request,
        JsonRpcMessage message,
        JsonObject? meta,
        string? metaVersion,
        string? headerVersion,
        bool acceptsJson,
        bool acceptsSse,
        List<KeyValuePair<string, string>> headers)
    {
        var started = Stopwatch.GetTimestamp();
        var method = message.Method!;

        async Task<bool> Reject(int status, int code, string text, JsonNode? data = null)
        {
            RecordActivity(null, null, method, null, false, text, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await SendJsonErrorAsync(connection, request, status, message.Id, code, text, headers, data).ConfigureAwait(false);
            return true;
        }

        if (metaVersion is null)
            return await Reject(400, JsonRpcCodes.InvalidParams, $"Missing required _meta field \"{MetaKeys.ProtocolVersion}\"").ConfigureAwait(false);

        if (!ProtocolVersions.IsModern(metaVersion))
        {
            var data = new JsonObject
            {
                ["supported"] = new JsonArray(ProtocolVersions.All.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                ["requested"] = metaVersion,
            };
            var text = ProtocolVersions.IsLegacy(metaVersion)
                ? $"Unsupported protocol version: {metaVersion} requires the initialize handshake; per-request _meta is only valid for {ProtocolVersions.LatestModern}"
                : $"Unsupported protocol version: {metaVersion}";
            return await Reject(400, JsonRpcCodes.UnsupportedProtocolVersion, text, data).ConfigureAwait(false);
        }

        if (headerVersion is null)
            return await Reject(400, JsonRpcCodes.HeaderMismatch, "Header mismatch: required MCP-Protocol-Version header is missing").ConfigureAwait(false);
        if (headerVersion != metaVersion)
            return await Reject(400, JsonRpcCodes.HeaderMismatch, $"Header mismatch: MCP-Protocol-Version header value '{headerVersion}' does not match body value '{metaVersion}'").ConfigureAwait(false);

        if (meta!.TryGetPropertyValue(MetaKeys.ClientCapabilities, out var caps) is false || caps is not JsonObject)
            return await Reject(400, JsonRpcCodes.InvalidParams, $"Missing required _meta field \"{MetaKeys.ClientCapabilities}\" (an object)").ConfigureAwait(false);

        McpLogLevel? logLevel = null;
        if (meta.TryGetPropertyValue(MetaKeys.LogLevel, out var levelNode) && levelNode is not null)
        {
            var levelText = levelNode is JsonValue lv && lv.GetValueKind() == JsonValueKind.String ? lv.GetValue<string>() : levelNode.ToJsonString();
            if (!LogLevels.TryParse(levelText, out var parsedLevel))
                return await Reject(400, JsonRpcCodes.InvalidParams, $"Invalid log level '{levelText}' in _meta[\"{MetaKeys.LogLevel}\"]").ConfigureAwait(false);
            logLevel = parsedLevel;
        }

        var methodHeader = request.Headers.Get("Mcp-Method");
        if (methodHeader is null)
            return await Reject(400, JsonRpcCodes.HeaderMismatch, "Header mismatch: required Mcp-Method header is missing").ConfigureAwait(false);
        if (methodHeader != method)
            return await Reject(400, JsonRpcCodes.HeaderMismatch, $"Header mismatch: Mcp-Method header value '{methodHeader}' does not match body value '{method}'").ConfigureAwait(false);

        if (!ModernMethods.Contains(method))
            return await Reject(404, JsonRpcCodes.MethodNotFound, $"Method not found: {method}").ConfigureAwait(false);

        var nameField = method switch
        {
            "tools/call" or "prompts/get" => "name",
            "resources/read" => "uri",
            _ => null,
        };
        if (nameField is not null)
        {
            var bodyName = JsonRpc.GetString(message.Params, nameField);
            var nameHeader = request.Headers.Get("Mcp-Name");
            if (nameHeader is null)
                return await Reject(400, JsonRpcCodes.HeaderMismatch, $"Header mismatch: required Mcp-Name header is missing for {method}").ConfigureAwait(false);
            if (!TryDecodeHeaderValue(nameHeader, out var decodedName))
                return await Reject(400, JsonRpcCodes.HeaderMismatch, "Header mismatch: Mcp-Name header has an invalid Base64 sentinel encoding").ConfigureAwait(false);
            if (bodyName is not null && decodedName != bodyName)
                return await Reject(400, JsonRpcCodes.HeaderMismatch, $"Header mismatch: Mcp-Name header value '{decodedName}' does not match body value '{bodyName}'").ConfigureAwait(false);
        }

        var clientInfo = meta[MetaKeys.ClientInfo] as JsonObject;
        var clientLabel = clientInfo is null ? null : ClientLabel(JsonRpc.GetString(clientInfo, "name"), JsonRpc.GetString(clientInfo, "version"));
        if (clientLabel is not null)
            _recentModernClients[clientLabel] = Environment.TickCount64;

        if (method == "subscriptions/listen")
            return await HandleListenAsync(connection, request, message, clientLabel, acceptsSse, headers, started).ConfigureAwait(false);

        // Closing the connection is the cancellation signal in 2026-07-28.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(connection.Closed, _serverCts.Token);
        var responder = new PostResponder(connection, request, acceptsJson, acceptsSse, null, metaVersion, headers, SseKeepAlive);
        var scope = new RequestScope
        {
            Era = Era.Modern,
            ProtocolVersion = metaVersion,
            Method = method,
            Id = message.Id,
            Params = message.Params,
            ClientName = clientLabel,
            AuthenticatedClient = request.AuthenticatedClient,
            LogLevel = logLevel,
            ProgressToken = ProgressTokenOf(message.Params),
            CancellationToken = cts.Token,
            Outbound = responder,
        };

        var (response, status) = await DispatchAsync(scope).ConfigureAwait(false);
        if (response is null)
        {
            await responder.CompleteWithoutResponseAsync(message.Id).ConfigureAwait(false);
            return false;
        }

        await responder.CompleteAsync(response, status).ConfigureAwait(false);
        return responder.KeepAlive;
    }

    /// <summary>
    /// JSON allows <c>\ud800</c> escapes that do not form a valid UTF-16 pair; System.Text.Json then throws when such a
    /// string is read. Reject them up front so every later string read is safe.
    /// </summary>
    internal static bool HasWellFormedStrings(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && reader.ValueIsEscaped)
                    _ = reader.GetString();
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (JsonException)
        {
            return true; // malformed JSON is the parse's to report, with its position
        }
    }

    internal static bool TryDecodeHeaderValue(string value, out string decoded)
    {
        const string prefix = "=?base64?";
        const string suffix = "?=";
        if (value.Length >= prefix.Length + suffix.Length && value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith(suffix, StringComparison.Ordinal))
        {
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[prefix.Length..^suffix.Length]));
                return true;
            }
            catch (FormatException)
            {
                decoded = "";
                return false;
            }
        }

        decoded = value;
        return true;
    }

    private async Task<bool> HandleListenAsync(HttpConnection connection, HttpRequest request, JsonRpcMessage message, string? clientLabel, bool acceptsSse, List<KeyValuePair<string, string>> headers, long started)
    {
        if (!acceptsSse)
        {
            RecordActivity(null, clientLabel, "subscriptions/listen", null, false, "client does not accept text/event-stream", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await SendJsonErrorAsync(connection, request, 406, message.Id, JsonRpcCodes.InvalidRequest, "subscriptions/listen responds with text/event-stream; include it in Accept", headers).ConfigureAwait(false);
            return true;
        }

        var filter = JsonRpc.GetObject(message.Params, "notifications");
        if (filter is null)
        {
            RecordActivity(null, clientLabel, "subscriptions/listen", null, false, "missing params.notifications", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await SendJsonErrorAsync(connection, request, 200, message.Id, JsonRpcCodes.InvalidParams, "subscriptions/listen requires params.notifications (a subscription filter object)", headers).ConfigureAwait(false);
            return true;
        }

        static bool Flag(JsonObject f, string name) =>
            f.TryGetPropertyValue(name, out var v) && v is JsonValue jv && jv.GetValueKind() == JsonValueKind.True;

        var uris = new HashSet<string>(StringComparer.Ordinal);
        if (filter["resourceSubscriptions"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is JsonValue iv && iv.GetValueKind() == JsonValueKind.String)
                    uris.Add(iv.GetValue<string>());
            }
        }

        var writer = new SseWriter(connection, request.IsHttp11, SseKeepAlive);
        var subscription = new ListenSubscription
        {
            RequestId = message.Id!.DeepClone(),
            Writer = writer,
            ToolsListChanged = Flag(filter, "toolsListChanged"),
            PromptsListChanged = Flag(filter, "promptsListChanged"),
            ResourcesListChanged = Flag(filter, "resourcesListChanged"),
            ResourceUris = uris,
            ClientName = clientLabel,
        };

        var acknowledged = new JsonObject();
        if (subscription.ToolsListChanged)
            acknowledged["toolsListChanged"] = true;
        if (subscription.PromptsListChanged)
            acknowledged["promptsListChanged"] = true;
        if (subscription.ResourcesListChanged)
            acknowledged["resourcesListChanged"] = true;
        if (uris.Count > 0)
            acknowledged["resourceSubscriptions"] = new JsonArray(uris.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray());

        writer.Start(SseWriter.BuildHead(request, headers, connection.Limits.KeepAliveHintSeconds));
        subscription.Send("notifications/subscriptions/acknowledged", new JsonObject { ["notifications"] = acknowledged });
        _listeners.TryAdd(subscription, 0);
        RecordActivity(null, clientLabel, "subscriptions/listen", string.Join(",", acknowledged.Select(kv => kv.Key)), true, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        try
        {
            if (_http is null)
                writer.Complete();
            await writer.Completion.ConfigureAwait(false);
        }
        finally
        {
            _listeners.TryRemove(subscription, out _);
        }

        return !connection.IsClosed && request.IsHttp11;
    }

    private async Task<bool> HandleGetAsync(HttpConnection connection, HttpRequest request, List<KeyValuePair<string, string>> headers)
    {
        var (_, acceptsSse) = ParseAccept(request.Headers.Get("Accept"));
        if (!acceptsSse)
        {
            await SendJsonErrorAsync(connection, request, 406, null, JsonRpcCodes.InvalidRequest, "Not Acceptable: GET opens a text/event-stream", headers).ConfigureAwait(false);
            return true;
        }

        var sessionId = request.Headers.Get("Mcp-Session-Id");
        if (sessionId is null)
        {
            await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.InvalidRequest,
                "Bad Request: GET requires an Mcp-Session-Id (legacy protocol). 2026-07-28 clients use POST subscriptions/listen instead.", headers).ConfigureAwait(false);
            return true;
        }

        var session = await ResolveSessionAsync(connection, request, sessionId, null, headers).ConfigureAwait(false);
        if (session is null)
            return true;

        var writer = new SseWriter(connection, request.IsHttp11, SseKeepAlive);
        writer.Start(SseWriter.BuildHead(request, headers, connection.Limits.KeepAliveHintSeconds));

        SseLogicalStream stream;
        var lastEventId = request.Headers.Get("Last-Event-ID");
        if (SseLogicalStream.TryParseEventId(lastEventId, out var streamId, out var seq) && session.FindStream(streamId) is { } resumed)
        {
            stream = resumed;
            stream.Attach(writer, seq);
        }
        else
        {
            stream = session.Standalone;
            if (ProtocolVersions.UsesPrimingEvent(session.ProtocolVersion))
                writer.TryEnqueuePriming(stream.EventId(stream.CurrentSeq));
            stream.Attach(writer, stream.CurrentSeq);
        }

        session.StreamOpened();
        RecordActivity(session.Id, ClientLabel(session.ClientName, session.ClientVersion), "GET (sse)", lastEventId is null ? stream.StreamId : "resume " + lastEventId, true, null, 0);
        using var registration = session.Lifetime.Token.Register(static s => ((SseWriter)s!).Complete(), writer);
        try
        {
            await writer.Completion.ConfigureAwait(false);
        }
        finally
        {
            stream.Detach(writer);
            session.StreamClosed();
        }

        return !connection.IsClosed && request.IsHttp11;
    }

    private async Task<bool> HandleDeleteAsync(HttpConnection connection, HttpRequest request, List<KeyValuePair<string, string>> headers)
    {
        var sessionId = request.Headers.Get("Mcp-Session-Id");
        if (sessionId is null)
        {
            await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.InvalidRequest, "Bad Request: DELETE requires an Mcp-Session-Id header", headers).ConfigureAwait(false);
            return true;
        }

        if (!_sessions.TryRemove(sessionId, out var session))
        {
            await SendJsonErrorAsync(connection, request, 404, null, JsonRpcCodes.SessionNotFound, "Session not found", headers).ConfigureAwait(false);
            return true;
        }

        session.Close();
        RecordActivity(session.Id, ClientLabel(session.ClientName, session.ClientVersion), "DELETE (session)", null, true, null, 0);
        LogSink($"session {session.Id[..8]}… closed by client", null);
        await connection.SendResponseAsync(request, 200, headers, null, ReadOnlySpan<byte>.Empty, close: false).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleBatchAsync(HttpConnection connection, HttpRequest request, JsonArray batch, string? sessionId, List<KeyValuePair<string, string>> headers)
    {
        if (sessionId is null)
        {
            await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.InvalidRequest,
                "Invalid Request: JSON-RPC batches are only accepted within a 2025-03-26 session (and initialize must not be batched)", headers).ConfigureAwait(false);
            return true;
        }

        var session = await ResolveSessionAsync(connection, request, sessionId, null, headers).ConfigureAwait(false);
        if (session is null)
            return true;

        if (!ProtocolVersions.AllowsBatch(session.ProtocolVersion))
        {
            await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.InvalidRequest,
                $"Invalid Request: JSON-RPC batching is not supported in protocol version {session.ProtocolVersion}", headers).ConfigureAwait(false);
            return true;
        }

        if (batch.Count == 0)
        {
            await SendJsonErrorAsync(connection, request, 400, null, JsonRpcCodes.InvalidRequest, "Invalid Request: empty batch", headers).ConfigureAwait(false);
            return true;
        }

        var pending = new List<Task<JsonObject?>>();
        var immediate = new List<JsonObject>();
        foreach (var element in batch)
        {
            var message = JsonRpcMessage.Classify(element);
            switch (message.Kind)
            {
                case JsonRpcKind.Invalid:
                    immediate.Add(JsonRpc.Error(message.Id, JsonRpcCodes.InvalidRequest, "Invalid Request: " + message.InvalidReason));
                    break;
                case JsonRpcKind.Notification:
                    HandleLegacyNotification(session, message);
                    break;
                case JsonRpcKind.Response:
                    break;
                case JsonRpcKind.Request when message.Method == "initialize":
                    immediate.Add(JsonRpc.Error(message.Id, JsonRpcCodes.InvalidRequest, "Invalid Request: initialize must not be part of a batch"));
                    break;
                default:
                    pending.Add(DispatchBatchItemAsync(session, message, request.AuthenticatedClient));
                    break;
            }
        }

        var results = await Task.WhenAll(pending).ConfigureAwait(false);
        var responses = new JsonArray();
        foreach (var r in immediate)
            responses.Add(r);
        foreach (var r in results)
        {
            if (r is not null)
                responses.Add(r);
        }

        if (responses.Count == 0)
        {
            await connection.SendResponseAsync(request, 202, headers, null, ReadOnlySpan<byte>.Empty, close: false).ConfigureAwait(false);
            return true;
        }

        await SendJsonAsync(connection, request, 200, responses, headers).ConfigureAwait(false);
        return true;
    }

    private async Task<JsonObject?> DispatchBatchItemAsync(LegacySession session, JsonRpcMessage message, string? authenticatedClient)
    {
        var idKey = JsonRpc.IdKey(message.Id);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token, _serverCts.Token);
        if (!session.InFlight.TryAdd(idKey, cts))
            return JsonRpc.Error(message.Id, JsonRpcCodes.InvalidRequest, $"Request id {idKey} is already in use by an in-flight request");
        try
        {
            var scope = new RequestScope
            {
                Era = Era.Legacy,
                ProtocolVersion = session.ProtocolVersion,
                Method = message.Method!,
                Id = message.Id,
                Params = message.Params,
                Session = session,
                ClientName = ClientLabel(session.ClientName, session.ClientVersion),
                AuthenticatedClient = authenticatedClient,
                ProgressToken = null,
                CancellationToken = cts.Token,
                Outbound = NullOutbound.Instance,
            };
            var (response, _) = await DispatchAsync(scope).ConfigureAwait(false);
            return response;
        }
        finally
        {
            session.InFlight.TryRemove(idKey, out _);
        }
    }
}

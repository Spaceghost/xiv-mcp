using System.Text.Json.Nodes;
using XivMcp.Core.Http;

namespace XivMcp.Core.Protocol;

internal enum Era
{
    /// <summary>initialize handshake + Mcp-Session-Id (2025-03-26 … 2025-11-25).</summary>
    Legacy,

    /// <summary>Stateless per-request _meta (2026-07-28).</summary>
    Modern,
}

/// <summary>Where request-scoped notifications (progress, log) for one request go.</summary>
internal interface IRequestOutbound
{
    void Notify(JsonObject notification);
}

internal sealed class NullOutbound : IRequestOutbound
{
    public static readonly NullOutbound Instance = new();

    public void Notify(JsonObject notification)
    {
    }
}

/// <summary>Everything the dispatcher needs to know about one JSON-RPC request.</summary>
internal sealed class RequestScope
{
    private readonly object _progressLock = new();
    private double _lastProgress = double.NegativeInfinity;

    public required Era Era { get; init; }

    public required string ProtocolVersion { get; init; }

    public required string Method { get; init; }

    public required JsonNode? Id { get; init; }

    public JsonObject? Params { get; init; }

    public LegacySession? Session { get; init; }

    public string? ClientName { get; init; }

    /// <summary>Per-client token name the request authenticated with (unlike ClientName, not self-reported).</summary>
    public string? AuthenticatedClient { get; init; }

    /// <summary>MCP session id for calls that run outside an HTTP request (approved tickets); requests use <see cref="Session"/>.</summary>
    public string? DetachedSessionId { get; init; }

    /// <summary>Modern: minimum level from _meta (null = no log notifications). Legacy uses the session level.</summary>
    public McpLogLevel? LogLevel { get; init; }

    public JsonNode? ProgressToken { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    public required IRequestOutbound Outbound { get; init; }

    /// <summary>Filled by handlers for the activity feed (tool name, URI, prompt name).</summary>
    public string? Target { get; set; }

    /// <summary>Set when a tools/call produced isError=true.</summary>
    public string? ToolError { get; set; }

    public bool TryAdvanceProgress(double progress)
    {
        lock (_progressLock)
        {
            if (progress <= _lastProgress)
                return false;
            _lastProgress = progress;
            return true;
        }
    }
}

/// <summary>
/// Response side of one POST carrying a JSON-RPC request. Starts as a plain application/json response
/// and switches to text/event-stream the first time a notification must precede the result.
/// </summary>
internal sealed class PostResponder : IRequestOutbound
{
    private readonly object _gate = new();
    private readonly HttpConnection _connection;
    private readonly HttpRequest _request;
    private readonly bool _acceptsJson;
    private readonly bool _acceptsSse;
    private readonly LegacySession? _session;
    private readonly string _protocolVersion;
    private readonly List<KeyValuePair<string, string>> _headers;
    private readonly TimeSpan _keepAlive;
    private SseWriter? _writer;
    private SseLogicalStream? _logical;
    private bool _completed;

    public PostResponder(
        HttpConnection connection,
        HttpRequest request,
        bool acceptsJson,
        bool acceptsSse,
        LegacySession? session,
        string protocolVersion,
        List<KeyValuePair<string, string>> headers,
        TimeSpan keepAlive)
    {
        _connection = connection;
        _request = request;
        _acceptsJson = acceptsJson;
        _acceptsSse = acceptsSse;
        _session = session;
        _protocolVersion = protocolVersion;
        _headers = headers;
        _keepAlive = keepAlive;
    }

    /// <summary>False when the response used a close-delimited stream (HTTP/1.0) or the peer vanished.</summary>
    public bool KeepAlive => !_connection.IsClosed && (_writer is null || _request.IsHttp11);

    public bool IsStreaming
    {
        get
        {
            lock (_gate)
                return _writer is not null;
        }
    }

    public void Notify(JsonObject notification)
    {
        if (!_acceptsSse)
            return;
        var json = JsonRpc.Serialize(notification);
        lock (_gate)
        {
            if (_completed)
                return;
            EnsureStreamLocked();
            PostLocked(json);
        }
    }

    private void EnsureStreamLocked()
    {
        if (_writer is not null)
            return;
        _writer = new SseWriter(_connection, _request.IsHttp11, _keepAlive);
        _writer.Start(SseWriter.BuildHead(_request, _headers, _connection.Limits.KeepAliveHintSeconds));
        if (_session is not null)
        {
            _logical = _session.NewPostStream();
            if (ProtocolVersions.UsesPrimingEvent(_protocolVersion))
                _writer.TryEnqueuePriming(_logical.EventId(0));
            _logical.Attach(_writer, 0);
        }
    }

    private void PostLocked(byte[] json)
    {
        if (_logical is not null)
            _logical.Post(json);
        else
            _writer!.TryEnqueueMessage(null, json);
    }

    /// <summary>Sends the final response (JSON body, or last SSE event) and waits until it is written.</summary>
    public async Task CompleteAsync(JsonObject response, int httpStatus)
    {
        var json = JsonRpc.Serialize(response);
        SseWriter? writer = null;
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            if (_writer is not null || (!_acceptsJson && _acceptsSse && httpStatus == 200))
            {
                EnsureStreamLocked();
                PostLocked(json);
                if (_logical is not null)
                    _logical.Complete();
                else
                    _writer!.Complete();
                writer = _writer;
            }
        }

        if (writer is null)
        {
            await _connection.SendResponseAsync(_request, httpStatus, _headers, "application/json", json, close: false).ConfigureAwait(false);
            return;
        }

        await writer.Completion.ConfigureAwait(false);
        _logical?.Detach(writer);
    }

    /// <summary>The request was cancelled: end the exchange without a JSON-RPC response where possible.</summary>
    public async Task CompleteWithoutResponseAsync(JsonNode? id)
    {
        SseWriter? writer = null;
        var sendError = false;
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            if (_connection.IsClosed)
                return;
            if (_writer is not null || _acceptsSse)
            {
                EnsureStreamLocked();
                if (_logical is not null)
                    _logical.Complete();
                else
                    _writer!.Complete();
                writer = _writer;
            }
            else
            {
                sendError = true;
            }
        }

        if (sendError)
        {
            var json = JsonRpc.Serialize(JsonRpc.Error(id, JsonRpcCodes.InternalError, "Request cancelled"));
            await _connection.SendResponseAsync(_request, 200, _headers, "application/json", json, close: false).ConfigureAwait(false);
            return;
        }

        if (writer is not null)
        {
            await writer.Completion.ConfigureAwait(false);
            _logical?.Detach(writer);
        }
    }
}

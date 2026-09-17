using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace XivMcp.Core.Http;

/// <summary>Minimal HTTP/1.1 server on a raw <see cref="TcpListener"/> (no http.sys, works under Wine).</summary>
internal sealed class HttpServer
{
    public delegate Task<bool> RequestHandler(HttpConnection connection, HttpRequest request);

    private readonly HttpLimits _limits;
    private readonly RequestHandler _handler;
    private readonly Action<string, Exception?> _log;
    private readonly ConcurrentDictionary<long, HttpConnection> _connections = new();
    private readonly ConcurrentDictionary<long, Task> _connectionTasks = new();
    private readonly CancellationTokenSource _stopCts = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private long _nextId;

    public HttpServer(HttpLimits limits, RequestHandler handler, Action<string, Exception?> log)
    {
        _limits = limits;
        _handler = handler;
        _log = log;
    }

    public int Port { get; private set; }

    public int ConnectionCount => _connections.Count;

    public void Start(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        if (!OperatingSystem.IsWindows())
        {
            // Linux: allow rebinding while old connections sit in TIME_WAIT (does not allow two listeners).
            // Not set on Windows/Wine, where SO_REUSEADDR has port-sharing semantics.
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        listener.Start(64);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(listener);
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stopCts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(_stopCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                if (_stopCts.IsCancellationRequested)
                    return;
                if (ex is SocketException se && se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                    continue;
                _log("accept failed", ex);
                try
                {
                    await Task.Delay(100, _stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                socket.NoDelay = true;
            }
            catch (Exception)
            {
            }

            var id = Interlocked.Increment(ref _nextId);
            var connection = new HttpConnection(socket, _limits, id);
            if (_connections.Count >= _limits.MaxConnections || _stopCts.IsCancellationRequested)
            {
                _ = RejectBusyAsync(connection);
                continue;
            }

            _connections[id] = connection;
            var task = Task.Run(() => ProcessConnectionAsync(connection));
            _connectionTasks[id] = task;
            if (task.IsCompleted)
                _connectionTasks.TryRemove(id, out _);
        }
    }

    private static async Task RejectBusyAsync(HttpConnection connection)
    {
        try
        {
            connection.StartPump();
            await connection.SendErrorAsync(null, 503, "Too many connections", close: true).ConfigureAwait(false);
            await connection.CloseAfterResponseAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            connection.Abort();
        }
    }

    private async Task ProcessConnectionAsync(HttpConnection connection)
    {
        try
        {
            connection.StartPump();
            var first = true;
            while (!connection.IsClosed && !_stopCts.IsCancellationRequested)
            {
                var (request, headError) = await connection.ReadRequestHeadAsync(first).ConfigureAwait(false);
                first = false;
                if (request is null)
                {
                    if (headError is { } e && !connection.IsClosed)
                    {
                        await connection.SendErrorAsync(null, e.Status, e.Message, close: true).ConfigureAwait(false);
                        await connection.CloseAfterResponseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    else
                    {
                        connection.CloseGracefully();
                    }

                    return;
                }

                var bodyError = await connection.ReadBodyAsync(request).ConfigureAwait(false);
                if (bodyError is { } be)
                {
                    if (be.Status != 0 && !connection.IsClosed)
                    {
                        await connection.SendErrorAsync(request, be.Status, be.Message, close: true).ConfigureAwait(false);
                        await connection.CloseAfterResponseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    else
                    {
                        connection.Abort();
                    }

                    return;
                }

                bool keepAlive;
                try
                {
                    keepAlive = await _handler(connection, request).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    connection.Abort();
                    return;
                }
                catch (Exception ex)
                {
                    _log($"unhandled error serving {request.Method} {request.Path}", ex);
                    if (!connection.ResponseStarted && !connection.IsClosed)
                    {
                        await connection.SendErrorAsync(request, 500, "Internal server error", close: true).ConfigureAwait(false);
                        await connection.CloseAfterResponseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    else
                    {
                        connection.Abort();
                    }

                    return;
                }

                if (!keepAlive || !request.KeepAlive)
                {
                    await connection.CloseAfterResponseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            _log("connection error", ex);
        }
        finally
        {
            connection.Abort();
            _connections.TryRemove(connection.Id, out _);
            _connectionTasks.TryRemove(connection.Id, out _);
        }
    }

    /// <summary>Stops accepting and releases the listening port immediately. Existing connections keep running.</summary>
    public void StopAccepting()
    {
        try
        {
            _stopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var listener = Interlocked.Exchange(ref _listener, null);
        try
        {
            listener?.Stop();
            listener?.Dispose();
        }
        catch (Exception ex)
        {
            _log("listener stop failed", ex);
        }
    }

    /// <summary>Stops accepting, releases the port immediately, resets every connection and waits briefly for handlers.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        StopAccepting();

        foreach (var connection in _connections.Values)
            connection.Abort();

        var tasks = _connectionTasks.Values.ToArray();
        if (_acceptTask is not null)
            tasks = [.. tasks, _acceptTask];
        try
        {
            await Task.WhenAll(tasks).WaitAsync(grace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timeouts or handler faults during shutdown are not actionable.
        }
    }
}

/// <summary>
/// A text/event-stream response body. Frames are queued (bounded) and written by a single background
/// loop; a consumer that falls behind is dropped instead of blocking producers.
/// </summary>
internal sealed class SseWriter
{
    private static readonly byte[] KeepAliveFrame = ": keepalive\n\n"u8.ToArray();
    private static readonly byte[] ChunkedTerminator = "0\r\n\r\n"u8.ToArray();

    private readonly HttpConnection _connection;
    private readonly bool _chunked;
    private readonly TimeSpan _keepAlive;
    private readonly Channel<byte[]> _queue;
    private Task _run = Task.CompletedTask;
    private int _dropped;

    public SseWriter(HttpConnection connection, bool chunked, TimeSpan keepAlive, int capacity = 1024)
    {
        _connection = connection;
        _chunked = chunked;
        _keepAlive = keepAlive <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : keepAlive;
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public HttpConnection Connection => _connection;

    /// <summary>Completes when the stream has ended (normally, by abort, or by disconnect).</summary>
    public Task Completion => _run;

    public bool IsClosed => _connection.IsClosed || Volatile.Read(ref _dropped) != 0;

    public bool WasDropped => Volatile.Read(ref _dropped) != 0;

    public static byte[] BuildHead(HttpRequest request, IReadOnlyList<KeyValuePair<string, string>>? headers, int keepAliveHint)
    {
        var all = new List<KeyValuePair<string, string>>(headers?.Count + 2 ?? 2)
        {
            new("Cache-Control", "no-cache, no-transform"),
            new("X-Accel-Buffering", "no"),
        };
        if (headers is not null)
            all.AddRange(headers);
        return HttpResponseHead.Build(200, request.IsHttp11, all, "text/event-stream", -1, chunked: request.IsHttp11, close: !request.IsHttp11 || !request.KeepAlive, keepAliveHint);
    }

    public void Start(byte[] head) => _run = Task.Run(() => RunAsync(head));

    public bool TryEnqueueMessage(string? eventId, ReadOnlySpan<byte> json)
    {
        if (IsClosed)
            return false;
        var sb = new StringBuilder(32);
        if (eventId is not null)
            sb.Append("id: ").Append(eventId).Append('\n');
        sb.Append("event: message\ndata: ");
        var prefix = Encoding.UTF8.GetBytes(sb.ToString());
        var frame = new byte[prefix.Length + json.Length + 2];
        prefix.CopyTo(frame, 0);
        json.CopyTo(frame.AsSpan(prefix.Length));
        frame[^2] = (byte)'\n';
        frame[^1] = (byte)'\n';
        return TryEnqueueFrame(frame);
    }

    /// <summary>An event with an id and empty data (2025-11-25 "priming" event for resumability).</summary>
    public bool TryEnqueuePriming(string eventId) =>
        TryEnqueueFrame(Encoding.UTF8.GetBytes("id: " + eventId + "\ndata: \n\n"));

    private bool TryEnqueueFrame(byte[] frame)
    {
        if (_queue.Writer.TryWrite(frame))
            return true;
        if (!_queue.Reader.Completion.IsCompleted)
        {
            // Queue full: the consumer is not keeping up. Drop it rather than block producers.
            Interlocked.Exchange(ref _dropped, 1);
            _connection.Abort();
            _queue.Writer.TryComplete();
        }

        return false;
    }

    /// <summary>No more frames; the loop flushes what is queued and ends the response.</summary>
    public void Complete() => _queue.Writer.TryComplete();

    public void Abort()
    {
        _queue.Writer.TryComplete();
        _connection.Abort();
    }

    private async Task RunAsync(byte[] head)
    {
        try
        {
            await _connection.WriteAsync(head).ConfigureAwait(false);
            var reader = _queue.Reader;
            Task<bool>? wait = null;
            while (true)
            {
                while (reader.TryRead(out var frame))
                    await WriteFrameAsync(frame).ConfigureAwait(false);

                wait ??= reader.WaitToReadAsync().AsTask();
                if (!wait.IsCompleted)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_connection.Closed);
                    var delay = Task.Delay(_keepAlive, delayCts.Token);
                    var done = await Task.WhenAny(wait, delay).ConfigureAwait(false);
                    delayCts.Cancel();
                    if (done != wait)
                    {
                        if (_connection.IsClosed)
                            return;
                        await WriteFrameAsync(KeepAliveFrame).ConfigureAwait(false);
                        continue;
                    }
                }

                var more = await wait.ConfigureAwait(false);
                wait = null;
                if (!more)
                    break;
            }

            if (_chunked && !_connection.IsClosed)
                await _connection.WriteAsync(ChunkedTerminator).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _connection.Abort();
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    private ValueTask WriteFrameAsync(byte[] frame)
    {
        if (!_chunked)
            return _connection.WriteAsync(frame);

        var sizeLine = Encoding.ASCII.GetBytes(frame.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        var buffer = new byte[sizeLine.Length + frame.Length + 2];
        sizeLine.CopyTo(buffer, 0);
        frame.CopyTo(buffer, sizeLine.Length);
        buffer[^2] = (byte)'\r';
        buffer[^1] = (byte)'\n';
        return _connection.WriteAsync(buffer);
    }
}

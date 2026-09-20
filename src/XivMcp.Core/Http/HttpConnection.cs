using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XivMcp.Core.Http;

/// <summary>
/// One accepted TCP connection. A background pump reads the socket into a bounded buffer so a
/// client disconnect is observed (via <see cref="Closed"/>) even while a request is being processed.
/// Requests are handled strictly one at a time (pipelined requests wait in the buffer).
/// </summary>
internal sealed class HttpConnection : IDisposable
{
    private const int HighWaterBytes = 256 * 1024;
    private static readonly byte[] ContinueResponse = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();

    private readonly Socket _socket;
    private readonly HttpLimits _limits;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _closedCts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Cached so Closed keeps working after _closedCts is disposed: Dispose cancels first, and a
    // registration on an already-cancelled token runs inline without touching the source.
    private readonly CancellationToken _closedToken;
    private int _disposed;

    private byte[] _buf = new byte[4096];
    private int _start;
    private int _end;
    private bool _eof;
    private TaskCompletionSource _dataSignal = NewSignal();
    private TaskCompletionSource? _drainSignal;
    private int _aborted;
    private int _closed;
    private long _bytesWrittenForRequest;

    public HttpConnection(Socket socket, HttpLimits limits, long id)
    {
        _socket = socket;
        _limits = limits;
        _closedToken = _closedCts.Token;
        Id = id;
        try
        {
            RemoteEndPoint = socket.RemoteEndPoint;
        }
        catch (SocketException)
        {
        }
    }

    public long Id { get; }

    public EndPoint? RemoteEndPoint { get; }

    /// <summary>Cancelled when the peer disconnects or the connection is aborted.</summary>
    public CancellationToken Closed => _closedToken;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>True once any byte of a response to the current request has been written.</summary>
    public bool ResponseStarted => Interlocked.Read(ref _bytesWrittenForRequest) > 0;

    public HttpLimits Limits => _limits;

    public void StartPump() => _ = PumpAsync();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long Now => Environment.TickCount64;

    private async Task PumpAsync()
    {
        var tmp = new byte[16 * 1024];
        try
        {
            while (true)
            {
                Task? drain = null;
                lock (_lock)
                {
                    if (_eof)
                        return;
                    if (_end - _start >= HighWaterBytes)
                    {
                        _drainSignal ??= NewSignal();
                        drain = _drainSignal.Task;
                    }
                }

                if (drain is not null)
                {
                    await drain.ConfigureAwait(false);
                    continue;
                }

                var n = await _socket.ReceiveAsync(tmp.AsMemory(), SocketFlags.None).ConfigureAwait(false);
                if (n <= 0)
                {
                    MarkEof();
                    return;
                }

                TaskCompletionSource signal;
                lock (_lock)
                {
                    EnsureCapacityLocked(n);
                    Buffer.BlockCopy(tmp, 0, _buf, _end, n);
                    _end += n;
                    signal = _dataSignal;
                    _dataSignal = NewSignal();
                }

                signal.TrySetResult();
            }
        }
        catch (Exception)
        {
            MarkEof();
        }
    }

    private void EnsureCapacityLocked(int extra)
    {
        if (_end + extra <= _buf.Length)
            return;

        var live = _end - _start;
        if (live + extra <= _buf.Length && _start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, live);
        }
        else
        {
            var size = _buf.Length;
            while (size < live + extra)
                size *= 2;
            var next = new byte[size];
            Buffer.BlockCopy(_buf, _start, next, 0, live);
            _buf = next;
        }

        _start = 0;
        _end = live;
    }

    private void ConsumeLocked(int count)
    {
        _start += count;
        if (_start >= _end)
        {
            _start = _end = 0;
            if (_buf.Length > 64 * 1024)
                _buf = new byte[4096];
        }

        if (_drainSignal is { } drain && _end - _start < HighWaterBytes)
        {
            _drainSignal = null;
            drain.TrySetResult();
        }
    }

    private void MarkEof()
    {
        TaskCompletionSource signal;
        TaskCompletionSource? drain;
        lock (_lock)
        {
            _eof = true;
            signal = _dataSignal;
            drain = _drainSignal;
            _drainSignal = null;
        }

        signal.TrySetResult();
        drain?.TrySetResult();
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        // Cancel on the thread pool: callers may hold locks (e.g. an SSE producer dropping a slow consumer),
        // and cancellation callbacks run arbitrary continuations.
        ThreadPool.UnsafeQueueUserWorkItem(static cts =>
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception)
            {
                // A cancellation callback threw; the connection is closed regardless.
            }
        }, _closedCts, preferLocal: false);
    }

    /// <summary>
    /// Waits for and parses the next request head. Returns (null, null) when the connection closed or
    /// idled out cleanly, (null, error) when a response should be sent before closing.
    /// </summary>
    public async Task<(HttpRequest? Request, HttpError? Error)> ReadRequestHeadAsync(bool firstRequest)
    {
        Interlocked.Exchange(ref _bytesWrittenForRequest, 0);
        var idleDeadline = Now + (long)(firstRequest ? _limits.HeaderTimeout : _limits.KeepAliveIdle).TotalMilliseconds;
        long headerDeadline = 0;

        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_end > _start)
                {
                    if (headerDeadline == 0)
                        headerDeadline = Now + (long)_limits.HeaderTimeout.TotalMilliseconds;

                    var status = HttpRequestParser.TryParseHead(
                        _buf.AsSpan(_start, _end - _start), _limits, out var request, out var consumed, out var error);
                    if (status == ParseStatus.Complete)
                    {
                        ConsumeLocked(consumed);
                        return (request, null);
                    }

                    if (status == ParseStatus.Error)
                        return (null, error);
                }

                if (_eof)
                    return (null, null);
                wait = _dataSignal.Task;
            }

            var remaining = (headerDeadline != 0 ? headerDeadline : idleDeadline) - Now;
            if (remaining <= 0)
                return (null, headerDeadline != 0 ? new HttpError(408, "Request timeout") : null);

            try
            {
                await wait.WaitAsync(TimeSpan.FromMilliseconds(remaining)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    /// <summary>Reads the body announced by <paramref name="request"/>. Status 0 in the error means the peer vanished.</summary>
    public async Task<HttpError?> ReadBodyAsync(HttpRequest request)
    {
        if (!request.Chunked && request.ContentLength == 0)
            return null;

        if (request.ExpectContinue)
        {
            bool haveData;
            lock (_lock)
                haveData = _end > _start;
            if (!haveData)
            {
                try
                {
                    await WriteRawAsync(ContinueResponse, countAsResponse: false).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return new HttpError(0, "Connection lost");
                }
            }
        }

        var deadline = Now + (long)_limits.BodyTimeout.TotalMilliseconds;
        var output = new ArrayBufferWriter<byte>(request.Chunked ? 1024 : (int)Math.Min(request.ContentLength, 64 * 1024));
        var decoder = request.Chunked ? new ChunkedDecoder(_limits.MaxBodyBytes) : null;

        while (true)
        {
            Task wait;
            lock (_lock)
            {
                var available = _end - _start;
                if (available > 0)
                {
                    if (decoder is null)
                    {
                        var need = (int)(request.ContentLength - output.WrittenCount);
                        var take = Math.Min(need, available);
                        _buf.AsSpan(_start, take).CopyTo(output.GetSpan(take));
                        output.Advance(take);
                        ConsumeLocked(take);
                        if (output.WrittenCount == request.ContentLength)
                        {
                            request.Body = output.WrittenSpan.ToArray();
                            return null;
                        }
                    }
                    else
                    {
                        var status = decoder.Decode(_buf.AsSpan(_start, available), output, out var consumed, out var error);
                        ConsumeLocked(consumed);
                        if (status == ParseStatus.Complete)
                        {
                            request.Body = output.WrittenSpan.ToArray();
                            return null;
                        }

                        if (status == ParseStatus.Error)
                            return error;
                    }
                }

                if (_eof)
                    return new HttpError(0, "Connection lost");
                wait = _dataSignal.Task;
            }

            var remaining = deadline - Now;
            if (remaining <= 0)
                return new HttpError(408, "Request body timeout");
            try
            {
                await wait.WaitAsync(TimeSpan.FromMilliseconds(remaining)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data) => WriteRawAsync(data, countAsResponse: true);

    private async ValueTask WriteRawAsync(ReadOnlyMemory<byte> data, bool countAsResponse)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _aborted) != 0)
                throw new IOException("Connection closed");
            if (countAsResponse)
                Interlocked.Add(ref _bytesWrittenForRequest, Math.Max(1, data.Length));

            using var timeout = new CancellationTokenSource(_limits.WriteTimeout);
            using var registration = timeout.Token.UnsafeRegister(static s => ((HttpConnection)s!).Abort(), this);
            var sent = 0;
            while (sent < data.Length)
            {
                var n = await _socket.SendAsync(data[sent..], SocketFlags.None).ConfigureAwait(false);
                if (n <= 0)
                    throw new IOException("Connection closed during write");
                sent += n;
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Abort();
            throw new IOException("Connection lost", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Writes a complete, non-streaming response.</summary>
    public ValueTask SendResponseAsync(
        HttpRequest? request,
        int status,
        IReadOnlyList<KeyValuePair<string, string>>? headers,
        string? contentType,
        ReadOnlySpan<byte> body,
        bool close)
    {
        close |= request is { KeepAlive: false };
        var head = HttpResponseHead.Build(status, request?.IsHttp11 ?? true, headers, contentType, body.Length, chunked: false, close, _limits.KeepAliveHintSeconds);
        var buffer = new byte[head.Length + body.Length];
        head.CopyTo(buffer, 0);
        body.CopyTo(buffer.AsSpan(head.Length));
        return WriteAsync(buffer);
    }

    /// <summary>Sends a plain-text error response.</summary>
    public ValueTask SendErrorAsync(HttpRequest? request, int status, string message, bool close, IReadOnlyList<KeyValuePair<string, string>>? headers = null) =>
        SendResponseAsync(request, status, headers, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message + "\n"), close);

    /// <summary>
    /// After a response that announced Connection: close, let the client close first (avoiding TIME_WAIT on
    /// the server port, which matters for plugin hot-reload), then reset whatever is left.
    /// </summary>
    public async Task CloseAfterResponseAsync(TimeSpan wait)
    {
        if (!IsClosed)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (Closed.UnsafeRegister(static s => ((TaskCompletionSource)s!).TrySetResult(), tcs))
            {
                await Task.WhenAny(tcs.Task, Task.Delay(wait)).ConfigureAwait(false);
            }
        }

        Abort();
    }

    /// <summary>Graceful FIN for an idle keep-alive connection.</summary>
    public void CloseGracefully()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception)
        {
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception)
        {
        }

        MarkEof();
    }

    /// <summary>Immediate reset (no TIME_WAIT on the server side). Safe to call repeatedly from any thread.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            MarkEof();
            return;
        }

        try
        {
            _socket.LingerState = new LingerOption(true, 0);
        }
        catch (Exception)
        {
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception)
        {
        }

        MarkEof();
    }

    /// <summary>
    /// Resets the connection and releases the cancellation source and the write lock. Called once
    /// the connection has been retired from <see cref="HttpServer"/>; safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Abort();

        // MarkEof queues the cancel to the thread pool, so it may not have run yet; cancel here
        // too (idempotent) so nothing is left waiting on a source that is about to go away.
        try
        {
            _closedCts.Cancel();
        }
        catch (Exception)
        {
            // A cancellation callback threw, or the source raced to disposal. Either way the
            // connection is gone.
        }

        _closedCts.Dispose();
        _writeLock.Dispose();
    }
}

internal static class HttpResponseHead
{
    public static string ReasonPhrase(int status) => status switch
    {
        100 => "Continue",
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        408 => "Request Timeout",
        409 => "Conflict",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        417 => "Expectation Failed",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        505 => "HTTP Version Not Supported",
        _ => "Unknown",
    };

    public static byte[] Build(
        int status,
        bool http11,
        IReadOnlyList<KeyValuePair<string, string>>? headers,
        string? contentType,
        long contentLength,
        bool chunked,
        bool close,
        int keepAliveHintSeconds)
    {
        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
        sb.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Server: xiv-mcp\r\n");
        if (contentType is not null)
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (chunked)
            sb.Append("Transfer-Encoding: chunked\r\n");
        else if (contentLength >= 0)
            sb.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

        if (close)
        {
            sb.Append("Connection: close\r\n");
        }
        else
        {
            if (!http11)
                sb.Append("Connection: keep-alive\r\n");
            sb.Append("Keep-Alive: timeout=").Append(keepAliveHintSeconds.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                // Header values we emit never contain CR/LF; guard anyway against injection.
                if (kv.Value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                    continue;
                sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            }
        }

        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

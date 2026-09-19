using System.Buffers;
using System.Text;

namespace XivMcp.Core.Http;

internal sealed class HttpHeaderCollection
{
    private readonly List<KeyValuePair<string, string>> _items = new(16);

    public int Count => _items.Count;

    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    public void Add(string name, string value) => _items.Add(new(name, value));

    /// <summary>First value of <paramref name="name"/> (case-insensitive), or null.</summary>
    public string? Get(string name)
    {
        foreach (var kv in _items)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }

    public int CountOf(string name)
    {
        var n = 0;
        foreach (var kv in _items)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                n++;
        }

        return n;
    }

    /// <summary>All values of a header joined with ", " (RFC 9110 list semantics), or null.</summary>
    public string? GetCombined(string name)
    {
        string? result = null;
        foreach (var kv in _items)
        {
            if (!string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                continue;
            result = result is null ? kv.Value : result + ", " + kv.Value;
        }

        return result;
    }
}

internal sealed class HttpRequest
{
    public required string Method { get; init; }

    public required string Target { get; init; }

    public required string Path { get; init; }

    public string? Query { get; init; }

    public required bool IsHttp11 { get; init; }

    public required HttpHeaderCollection Headers { get; init; }

    /// <summary>-1 when the body is chunked.</summary>
    public long ContentLength { get; init; }

    public bool Chunked { get; init; }

    public bool ExpectContinue { get; init; }

    /// <summary>Whether the client asked to keep the connection open after this exchange.</summary>
    public bool KeepAlive { get; init; }

    public byte[] Body { get; set; } = [];

    /// <summary>Name of the per-client token this request authenticated with (see McpServerOptions.ClientTokens), or null.</summary>
    public string? AuthenticatedClient { get; set; }
}

internal readonly record struct HttpError(int Status, string Message);

internal enum ParseStatus
{
    NeedMore,
    Complete,
    Error,
}

internal sealed class HttpLimits
{
    public int MaxHeaderBytes { get; init; } = 16 * 1024;

    public int MaxHeaderCount { get; init; } = 100;

    public long MaxBodyBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Idle time allowed between requests on a kept-alive connection.</summary>
    public TimeSpan KeepAliveIdle { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Advertised in Keep-Alive: timeout=N so well-behaved clients close first.</summary>
    public int KeepAliveHintSeconds { get; init; } = 30;

    /// <summary>Time allowed from the first byte of a request until its headers are complete (slowloris guard).</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Time allowed to receive the whole body once headers are complete.</summary>
    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A single socket write that makes no progress for this long aborts the connection.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxConnections { get; init; } = 64;
}

/// <summary>Pure HTTP/1.1 request-head parser (RFC 9112), strict where ambiguity enables smuggling.</summary>
internal static class HttpRequestParser
{
    private static readonly SearchValues<byte> TokenChars = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

    public static ParseStatus TryParseHead(
        ReadOnlySpan<byte> data,
        HttpLimits limits,
        out HttpRequest? request,
        out int consumed,
        out HttpError error)
    {
        request = null;
        consumed = 0;
        error = default;

        var pos = 0;
        string? method = null, target = null;
        var http11 = false;
        var headers = new HttpHeaderCollection();

        while (true)
        {
            var rel = data[pos..].IndexOf((byte)'\n');
            if (rel < 0)
            {
                if (data.Length > limits.MaxHeaderBytes)
                {
                    error = new HttpError(431, "Request header fields too large");
                    return ParseStatus.Error;
                }

                return ParseStatus.NeedMore;
            }

            var line = data.Slice(pos, rel);
            pos += rel + 1;
            if (pos > limits.MaxHeaderBytes)
            {
                error = new HttpError(431, "Request header fields too large");
                return ParseStatus.Error;
            }

            if (line.Length > 0 && line[^1] == (byte)'\r')
                line = line[..^1];

            if (method is null)
            {
                if (line.IsEmpty)
                {
                    // RFC 9112 §2.2: ignore at least one empty line before the request-line.
                    if (pos > 4)
                    {
                        error = new HttpError(400, "Malformed request line");
                        return ParseStatus.Error;
                    }

                    continue;
                }

                if (!TryParseRequestLine(line, out method, out target, out http11, out error))
                    return ParseStatus.Error;
                continue;
            }

            if (line.IsEmpty)
                break;

            if (line[0] is (byte)' ' or (byte)'\t')
            {
                error = new HttpError(400, "Obsolete header line folding is not accepted");
                return ParseStatus.Error;
            }

            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || line[..colon].IndexOfAnyExcept(TokenChars) >= 0)
            {
                error = new HttpError(400, "Malformed header field");
                return ParseStatus.Error;
            }

            var value = TrimOws(line[(colon + 1)..]);
            foreach (var b in value)
            {
                if ((b < 0x20 && b != (byte)'\t') || b == 0x7F)
                {
                    error = new HttpError(400, "Invalid character in header value");
                    return ParseStatus.Error;
                }
            }

            if (headers.Count >= limits.MaxHeaderCount)
            {
                error = new HttpError(431, "Too many header fields");
                return ParseStatus.Error;
            }

            headers.Add(Encoding.ASCII.GetString(line[..colon]), Encoding.Latin1.GetString(value));
        }

        consumed = pos;
        return BuildRequest(method, target!, http11, headers, limits, out request, out error)
            ? ParseStatus.Complete
            : ParseStatus.Error;
    }

    private static bool TryParseRequestLine(
        ReadOnlySpan<byte> line,
        out string? method,
        out string? target,
        out bool http11,
        out HttpError error)
    {
        method = target = null;
        http11 = false;
        error = new HttpError(400, "Malformed request line");

        var sp1 = line.IndexOf((byte)' ');
        if (sp1 <= 0)
            return false;
        var rest = line[(sp1 + 1)..];
        var sp2 = rest.IndexOf((byte)' ');
        if (sp2 <= 0)
            return false;

        var m = line[..sp1];
        var t = rest[..sp2];
        var v = rest[(sp2 + 1)..];

        if (m.IndexOfAnyExcept(TokenChars) >= 0)
            return false;
        foreach (var b in t)
        {
            if (b <= 0x20 || b >= 0x7F)
                return false;
        }

        if (v.SequenceEqual("HTTP/1.1"u8))
        {
            http11 = true;
        }
        else if (v.SequenceEqual("HTTP/1.0"u8))
        {
            http11 = false;
        }
        else if (v.Length == 8 && v.StartsWith("HTTP/"u8) && char.IsAsciiDigit((char)v[5]) && v[6] == (byte)'.' && char.IsAsciiDigit((char)v[7]))
        {
            error = new HttpError(505, "HTTP version not supported");
            return false;
        }
        else
        {
            return false;
        }

        method = Encoding.ASCII.GetString(m);
        target = Encoding.ASCII.GetString(t);
        error = default;
        return true;
    }

    private static bool BuildRequest(
        string method,
        string target,
        bool http11,
        HttpHeaderCollection headers,
        HttpLimits limits,
        out HttpRequest? request,
        out HttpError error)
    {
        request = null;
        error = default;

        string path;
        string? query = null;
        if (target.StartsWith('/'))
        {
            var q = target.IndexOf('?');
            path = q < 0 ? target : target[..q];
            if (q >= 0)
                query = target[(q + 1)..];
        }
        else if (target == "*")
        {
            path = "*";
        }
        else if (Uri.TryCreate(target, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https"))
        {
            path = abs.AbsolutePath;
            query = abs.Query.Length > 1 ? abs.Query[1..] : null;
        }
        else
        {
            error = new HttpError(400, "Invalid request target");
            return false;
        }

        if (http11 && headers.CountOf("Host") != 1)
        {
            error = new HttpError(400, "Exactly one Host header is required");
            return false;
        }

        var te = headers.GetCombined("Transfer-Encoding");
        var cl = headers.GetCombined("Content-Length");
        long contentLength = 0;
        var chunked = false;

        if (te is not null)
        {
            if (!http11)
            {
                error = new HttpError(400, "Transfer-Encoding is not allowed in HTTP/1.0");
                return false;
            }

            if (cl is not null)
            {
                error = new HttpError(400, "Both Transfer-Encoding and Content-Length present");
                return false;
            }

            var codings = te.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (codings.Length != 1 || !codings[0].Equals("chunked", StringComparison.OrdinalIgnoreCase))
            {
                error = new HttpError(501, "Only chunked transfer-coding is supported");
                return false;
            }

            chunked = true;
            contentLength = -1;
        }
        else if (cl is not null)
        {
            string? first = null;
            foreach (var part in cl.Split(',', StringSplitOptions.TrimEntries))
            {
                if (part.Length == 0 || part.Length > 18 || !part.AsSpan().ContainsOnlyDigits())
                {
                    error = new HttpError(400, "Invalid Content-Length");
                    return false;
                }

                if (first is not null && first != part)
                {
                    error = new HttpError(400, "Conflicting Content-Length values");
                    return false;
                }

                first = part;
            }

            contentLength = long.Parse(first!, System.Globalization.CultureInfo.InvariantCulture);
            if (contentLength > limits.MaxBodyBytes)
            {
                error = new HttpError(413, "Request body too large");
                return false;
            }
        }

        var connection = headers.GetCombined("Connection");
        var keepAlive = http11;
        if (connection is not null)
        {
            foreach (var token in connection.Split(',', StringSplitOptions.TrimEntries))
            {
                if (token.Equals("close", StringComparison.OrdinalIgnoreCase))
                    keepAlive = false;
                else if (token.Equals("keep-alive", StringComparison.OrdinalIgnoreCase) && !http11)
                    keepAlive = true;
            }
        }

        var expectContinue = false;
        var expect = headers.Get("Expect");
        if (expect is not null)
        {
            if (!expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                error = new HttpError(417, "Unsupported expectation");
                return false;
            }

            expectContinue = http11;
        }

        request = new HttpRequest
        {
            Method = method,
            Target = target,
            Path = path,
            Query = query,
            IsHttp11 = http11,
            Headers = headers,
            ContentLength = contentLength,
            Chunked = chunked,
            ExpectContinue = expectContinue,
            KeepAlive = keepAlive,
        };
        return true;
    }

    private static ReadOnlySpan<byte> TrimOws(ReadOnlySpan<byte> s)
    {
        var start = 0;
        while (start < s.Length && s[start] is (byte)' ' or (byte)'\t')
            start++;
        var end = s.Length;
        while (end > start && s[end - 1] is (byte)' ' or (byte)'\t')
            end--;
        return s[start..end];
    }

    private static bool ContainsOnlyDigits(this ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }
}

/// <summary>Incremental decoder for chunked request bodies (RFC 9112 §7.1).</summary>
internal sealed class ChunkedDecoder
{
    private const int MaxLineBytes = 1024;
    private const int MaxTrailerBytes = 8 * 1024;

    private enum State
    {
        Size,
        Data,
        DataEnd,
        Trailer,
        Done,
    }

    private readonly long _maxBody;
    private State _state = State.Size;
    private long _remaining;
    private long _total;
    private int _trailerBytes;

    public ChunkedDecoder(long maxBody) => _maxBody = maxBody;

    public bool IsDone => _state == State.Done;

    public ParseStatus Decode(ReadOnlySpan<byte> input, IBufferWriter<byte> output, out int consumed, out HttpError error)
    {
        error = default;
        var pos = 0;
        consumed = 0;

        while (true)
        {
            switch (_state)
            {
                case State.Size:
                {
                    var nl = input[pos..].IndexOf((byte)'\n');
                    if (nl < 0)
                    {
                        if (input.Length - pos > MaxLineBytes)
                        {
                            error = new HttpError(400, "Chunk size line too long");
                            return ParseStatus.Error;
                        }

                        consumed = pos;
                        return ParseStatus.NeedMore;
                    }

                    var line = input.Slice(pos, nl);
                    pos += nl + 1;
                    if (line.Length > 0 && line[^1] == (byte)'\r')
                        line = line[..^1];
                    var semi = line.IndexOf((byte)';');
                    if (semi >= 0)
                        line = line[..semi];
                    while (line.Length > 0 && line[^1] is (byte)' ' or (byte)'\t')
                        line = line[..^1];

                    if (line.IsEmpty || line.Length > 15)
                    {
                        error = new HttpError(400, "Invalid chunk size");
                        return ParseStatus.Error;
                    }

                    long size = 0;
                    foreach (var b in line)
                    {
                        int digit = b switch
                        {
                            >= (byte)'0' and <= (byte)'9' => b - '0',
                            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                            _ => -1,
                        };
                        if (digit < 0)
                        {
                            error = new HttpError(400, "Invalid chunk size");
                            return ParseStatus.Error;
                        }

                        size = (size << 4) | (uint)digit;
                    }

                    if (size == 0)
                    {
                        _state = State.Trailer;
                        break;
                    }

                    if (_total + size > _maxBody)
                    {
                        error = new HttpError(413, "Request body too large");
                        return ParseStatus.Error;
                    }

                    _total += size;
                    _remaining = size;
                    _state = State.Data;
                    break;
                }

                case State.Data:
                {
                    var available = input.Length - pos;
                    if (available == 0)
                    {
                        consumed = pos;
                        return ParseStatus.NeedMore;
                    }

                    var take = (int)Math.Min(_remaining, available);
                    input.Slice(pos, take).CopyTo(output.GetSpan(take));
                    output.Advance(take);
                    pos += take;
                    _remaining -= take;
                    if (_remaining == 0)
                        _state = State.DataEnd;
                    break;
                }

                case State.DataEnd:
                {
                    if (pos >= input.Length)
                    {
                        consumed = pos;
                        return ParseStatus.NeedMore;
                    }

                    if (input[pos] == (byte)'\n')
                    {
                        pos++;
                        _state = State.Size;
                        break;
                    }

                    if (input[pos] != (byte)'\r')
                    {
                        error = new HttpError(400, "Missing CRLF after chunk data");
                        return ParseStatus.Error;
                    }

                    if (pos + 1 >= input.Length)
                    {
                        consumed = pos;
                        return ParseStatus.NeedMore;
                    }

                    if (input[pos + 1] != (byte)'\n')
                    {
                        error = new HttpError(400, "Missing CRLF after chunk data");
                        return ParseStatus.Error;
                    }

                    pos += 2;
                    _state = State.Size;
                    break;
                }

                case State.Trailer:
                {
                    var nl = input[pos..].IndexOf((byte)'\n');
                    if (nl < 0)
                    {
                        if (_trailerBytes + input.Length - pos > MaxTrailerBytes)
                        {
                            error = new HttpError(431, "Trailer section too large");
                            return ParseStatus.Error;
                        }

                        consumed = pos;
                        return ParseStatus.NeedMore;
                    }

                    var line = input.Slice(pos, nl);
                    pos += nl + 1;
                    _trailerBytes += nl + 1;
                    if (_trailerBytes > MaxTrailerBytes)
                    {
                        error = new HttpError(431, "Trailer section too large");
                        return ParseStatus.Error;
                    }

                    if (line.Length > 0 && line[^1] == (byte)'\r')
                        line = line[..^1];
                    if (line.IsEmpty)
                    {
                        _state = State.Done;
                        consumed = pos;
                        return ParseStatus.Complete;
                    }

                    break;
                }

                case State.Done:
                    consumed = pos;
                    return ParseStatus.Complete;
            }
        }
    }
}

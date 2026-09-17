using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using XivMcp.Core.Http;

namespace XivMcp.Core.Protocol;

/// <summary>
/// One logical SSE stream of a legacy session (the standalone GET stream or a POST response stream).
/// Every message gets a per-session unique event id "{streamId}_{seq}" and is kept in a bounded history
/// so a client can resume with Last-Event-ID after a dropped connection.
/// </summary>
internal sealed class SseLogicalStream
{
    private const int MaxHistoryCount = 256;
    private const long MaxHistoryBytes = 1024 * 1024;

    private readonly object _lock = new();
    private readonly Queue<(long Seq, byte[] Json)> _history = new();
    private long _historyBytes;
    private long _seq;
    private SseWriter? _attached;

    public SseLogicalStream(string streamId)
    {
        StreamId = streamId;
        LastUsed = Environment.TickCount64;
    }

    public string StreamId { get; }

    public bool Completed { get; private set; }

    public long LastUsed { get; private set; }

    public bool IsAttached
    {
        get
        {
            lock (_lock)
                return _attached is { IsClosed: false };
        }
    }

    public long CurrentSeq
    {
        get
        {
            lock (_lock)
                return _seq;
        }
    }

    public string EventId(long seq) => StreamId + "_" + seq.ToString(CultureInfo.InvariantCulture);

    public void Post(byte[] json)
    {
        lock (_lock)
        {
            if (Completed)
                return;
            _seq++;
            _history.Enqueue((_seq, json));
            _historyBytes += json.Length;
            while (_history.Count > MaxHistoryCount || (_historyBytes > MaxHistoryBytes && _history.Count > 1))
                _historyBytes -= _history.Dequeue().Json.Length;
            LastUsed = Environment.TickCount64;

            if (_attached is not null && !_attached.TryEnqueueMessage(EventId(_seq), json))
                _attached = null;
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            Completed = true;
            LastUsed = Environment.TickCount64;
            _attached?.Complete();
            _attached = null;
        }
    }

    /// <summary>Attaches a live connection, replaying history after <paramref name="afterSeq"/>. Replaces any previous connection.</summary>
    public void Attach(SseWriter writer, long afterSeq)
    {
        lock (_lock)
        {
            if (_attached is not null && _attached != writer)
                _attached.Complete();
            _attached = null;

            foreach (var (seq, json) in _history)
            {
                if (seq > afterSeq && !writer.TryEnqueueMessage(EventId(seq), json))
                    return;
            }

            LastUsed = Environment.TickCount64;
            if (Completed)
                writer.Complete();
            else
                _attached = writer;
        }
    }

    public void Detach(SseWriter writer)
    {
        lock (_lock)
        {
            if (_attached == writer)
                _attached = null;
        }
    }

    public static bool TryParseEventId(string? eventId, out string streamId, out long seq)
    {
        streamId = "";
        seq = 0;
        if (string.IsNullOrEmpty(eventId))
            return false;
        var sep = eventId.LastIndexOf('_');
        if (sep <= 0 || sep == eventId.Length - 1)
            return false;
        streamId = eventId[..sep];
        return long.TryParse(eventId.AsSpan(sep + 1), NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }
}

/// <summary>A session created by <c>initialize</c> (protocol 2025-03-26 … 2025-11-25).</summary>
internal sealed class LegacySession
{
    private const int MaxPostStreams = 32;

    private readonly object _streamsLock = new();
    private readonly Dictionary<string, SseLogicalStream> _streams = new(StringComparer.Ordinal);
    private int _streamCounter;
    private long _lastActivity;
    private int _minLogLevel = (int)McpLogLevel.Info;
    private int _openStreams;

    public LegacySession(string id, string protocolVersion, string? clientName, string? clientVersion, JsonObject? clientCapabilities)
    {
        Id = id;
        ProtocolVersion = protocolVersion;
        ClientName = clientName;
        ClientVersion = clientVersion;
        ClientCapabilities = clientCapabilities;
        Standalone = new SseLogicalStream("g");
        _streams[Standalone.StreamId] = Standalone;
        Touch();
    }

    public string Id { get; }

    public string ProtocolVersion { get; }

    public string? ClientName { get; }

    public string? ClientVersion { get; }

    public JsonObject? ClientCapabilities { get; }

    public volatile bool Initialized;

    public CancellationTokenSource Lifetime { get; } = new();

    public SseLogicalStream Standalone { get; }

    public ConcurrentDictionary<string, byte> Subscriptions { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, CancellationTokenSource> InFlight { get; } = new(StringComparer.Ordinal);

    public long LastActivity => Interlocked.Read(ref _lastActivity);

    public McpLogLevel MinLogLevel
    {
        get => (McpLogLevel)Volatile.Read(ref _minLogLevel);
        set => Volatile.Write(ref _minLogLevel, (int)value);
    }

    public bool IsClosed => Lifetime.IsCancellationRequested;

    public int OpenStreams => Volatile.Read(ref _openStreams);

    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public void StreamOpened() => Interlocked.Increment(ref _openStreams);

    public void StreamClosed()
    {
        Interlocked.Decrement(ref _openStreams);
        Touch();
    }

    public SseLogicalStream NewPostStream()
    {
        lock (_streamsLock)
        {
            var stream = new SseLogicalStream("p" + (++_streamCounter).ToString(CultureInfo.InvariantCulture));
            _streams[stream.StreamId] = stream;
            if (_streams.Count > MaxPostStreams + 1)
            {
                var oldest = _streams.Values.Where(s => s != Standalone && s.Completed).OrderBy(s => s.LastUsed).FirstOrDefault();
                if (oldest is not null)
                    _streams.Remove(oldest.StreamId);
            }

            return stream;
        }
    }

    public SseLogicalStream? FindStream(string streamId)
    {
        lock (_streamsLock)
            return _streams.GetValueOrDefault(streamId);
    }

    public void PurgeStreams(long now, long maxAgeMs)
    {
        lock (_streamsLock)
        {
            foreach (var stream in _streams.Values.ToArray())
            {
                if (stream != Standalone && stream.Completed && now - stream.LastUsed > maxAgeMs)
                    _streams.Remove(stream.StreamId);
            }
        }
    }

    public void Close()
    {
        try
        {
            Lifetime.Cancel();
        }
        catch (Exception)
        {
            // Callbacks of cancelled tool calls must not break session teardown.
        }

        SseLogicalStream[] streams;
        lock (_streamsLock)
            streams = _streams.Values.ToArray();
        foreach (var s in streams)
            s.Complete();
        foreach (var cts in InFlight.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception)
            {
            }
        }
    }
}

/// <summary>A 2026-07-28 <c>subscriptions/listen</c> stream.</summary>
internal sealed class ListenSubscription
{
    public required JsonNode RequestId { get; init; }

    public required SseWriter Writer { get; init; }

    public bool ToolsListChanged { get; init; }

    public bool PromptsListChanged { get; init; }

    public bool ResourcesListChanged { get; init; }

    public required HashSet<string> ResourceUris { get; init; }

    public string? ClientName { get; init; }

    public bool Send(string method, JsonObject? parameters)
    {
        var p = parameters ?? new JsonObject();
        p.Insert(0, "_meta", new JsonObject { [MetaKeys.SubscriptionId] = RequestId.DeepClone() });
        return Writer.TryEnqueueMessage(null, JsonRpc.Serialize(JsonRpc.Notification(method, p)));
    }
}

/// <summary>Fixed-size ring buffer of handled requests.</summary>
internal sealed class ActivityLog
{
    private readonly object _lock = new();
    private readonly ActivityEntry?[] _items;
    private int _next;
    private int _count;

    public ActivityLog(int capacity) => _items = new ActivityEntry?[Math.Max(1, capacity)];

    public void Add(ActivityEntry entry)
    {
        lock (_lock)
        {
            _items[_next] = entry;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length)
                _count++;
        }
    }

    public IReadOnlyList<ActivityEntry> Newest(int max)
    {
        lock (_lock)
        {
            var n = Math.Clamp(max, 0, _count);
            var result = new ActivityEntry[n];
            for (var i = 0; i < n; i++)
            {
                var idx = (_next - 1 - i + _items.Length) % _items.Length;
                result[i] = _items[idx]!;
            }

            return result;
        }
    }
}

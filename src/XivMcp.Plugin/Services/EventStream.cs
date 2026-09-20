using System.Text.Json.Nodes;

namespace XivMcp.Plugin.Services;

/// <summary>One thing that happened in game, as an MCP client sees it.</summary>
/// <param name="Seq">Monotonic sequence number; a client passes the last one it saw back as a cursor.</param>
/// <param name="At">When the plugin noticed it.</param>
/// <param name="Kind">Event family, e.g. "zone", "duty", "party", "combat", "inventory", "login".</param>
/// <param name="Summary">One human-readable line.</param>
/// <param name="Data">Optional structured detail.</param>
public sealed record GameEvent(long Seq, DateTimeOffset At, string Kind, string Summary, JsonObject? Data);

/// <summary>
/// A bounded, thread-safe ring buffer of game events with a monotonic cursor, so an MCP client can poll or
/// subscribe without the plugin growing without limit. Pure bookkeeping: it never touches game memory, so it
/// is host-testable on its own.
/// </summary>
public sealed class EventStream
{
    /// <summary>Largest buffer the plugin will keep.</summary>
    public const int MaxCapacity = 2000;

    private readonly object gate = new();
    private readonly Queue<GameEvent> events = new();
    private long nextSeq = 1;
    private long droppedTotal;

    public EventStream(int capacity = 500) => Capacity = Math.Clamp(capacity, 16, MaxCapacity);

    /// <summary>Raised after an event is appended, from whatever thread appended it.</summary>
    public event Action<GameEvent>? Appended;

    public int Capacity { get; }

    /// <summary>Events currently buffered.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return events.Count;
            }
        }
    }

    /// <summary>Sequence number the next appended event will get.</summary>
    public long NextSequence
    {
        get
        {
            lock (gate)
            {
                return nextSeq;
            }
        }
    }

    /// <summary>How many events fell out of the buffer because a client did not read fast enough.</summary>
    public long DroppedTotal
    {
        get
        {
            lock (gate)
            {
                return droppedTotal;
            }
        }
    }

    /// <summary>Appends an event and returns it. Kind and summary are trimmed and capped.</summary>
    public GameEvent Append(string kind, string summary, JsonObject? data = null)
    {
        GameEvent entry;
        lock (gate)
        {
            entry = new GameEvent(
                nextSeq++,
                DateTimeOffset.UtcNow,
                Cap(kind, 32, "other"),
                Cap(summary, 400, ""),
                data);
            events.Enqueue(entry);
            while (events.Count > Capacity)
            {
                events.Dequeue();
                droppedTotal++;
            }
        }

        try
        {
            Appended?.Invoke(entry);
        }
        catch
        {
            // A subscriber must never break the producer.
        }

        return entry;
    }

    /// <summary>
    /// Events with <c>Seq &gt; afterSeq</c>, oldest first, optionally filtered by kind.
    /// </summary>
    /// <param name="afterSeq">Cursor; 0 means "everything buffered".</param>
    /// <param name="kinds">Kinds to keep (case-insensitive); null or empty keeps all.</param>
    /// <param name="limit">Maximum events to return.</param>
    /// <returns>
    /// The page, the cursor to pass next time, how many matches were left behind, and whether the requested
    /// cursor had already fallen out of the buffer (the client missed events).
    /// </returns>
    public (List<GameEvent> Events, long NextCursor, int Remaining, bool Gap) Since(
        long afterSeq,
        IReadOnlyCollection<string>? kinds = null,
        int limit = 100)
    {
        limit = Math.Clamp(limit, 1, MaxCapacity);
        lock (gate)
        {
            var oldest = events.Count > 0 ? events.Peek().Seq : nextSeq;
            var gap = afterSeq > 0 && afterSeq + 1 < oldest;

            var matching = events
                .Where(e => e.Seq > afterSeq)
                .Where(e => kinds == null || kinds.Count == 0 || kinds.Any(k => k.Equals(e.Kind, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var page = matching.Take(limit).ToList();
            var cursor = page.Count > 0 ? page[^1].Seq : Math.Max(afterSeq, oldest - 1);
            return (page, cursor, matching.Count - page.Count, gap);
        }
    }

    /// <summary>Distinct kinds currently in the buffer with their counts, most frequent first.</summary>
    public List<(string Kind, int Count)> Kinds()
    {
        lock (gate)
        {
            return events
                .GroupBy(e => e.Kind, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count()))
                .OrderByDescending(g => g.Item2)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            events.Clear();
        }
    }

    private static string Cap(string? value, int max, string fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return fallback;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

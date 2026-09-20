using System.Collections.Concurrent;

namespace XivMcp.Plugin.Services;

/// <summary>
/// A tiny bounded time-to-live cache used to keep the plugin polite towards public HTTP data sources.
/// Pure and host-testable: it never fetches anything itself, it only remembers what a caller fetched and
/// for how long that answer stays fresh. Entries are evicted oldest-first once <see cref="Capacity"/> is hit.
/// </summary>
/// <typeparam name="TValue">Cached value type.</typeparam>
public sealed class MarketCache<TValue>
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> clock;

    public MarketCache(int capacity = 256, Func<DateTimeOffset>? clock = null)
    {
        Capacity = Math.Clamp(capacity, 1, 4096);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public int Capacity { get; }

    public int Count => entries.Count;

    /// <summary>Returns a still-fresh value and when it was stored, or null.</summary>
    public (TValue Value, DateTimeOffset StoredAt)? TryGet(string key)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= clock())
        {
            entries.TryRemove(key, out _);
            return null;
        }

        return (entry.Value, entry.StoredAt);
    }

    /// <summary>Stores a value for <paramref name="ttl"/>, evicting the oldest entries when full.</summary>
    public void Set(string key, TValue value, TimeSpan ttl)
    {
        var now = clock();
        entries[key] = new Entry(value, now, now + (ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : ttl));
        Trim();
    }

    public void Clear() => entries.Clear();

    private void Trim()
    {
        while (entries.Count > Capacity)
        {
            var oldest = entries.OrderBy(e => e.Value.StoredAt).Select(e => e.Key).FirstOrDefault();
            if (oldest == null || !entries.TryRemove(oldest, out _))
            {
                return;
            }
        }
    }

    private readonly record struct Entry(TValue Value, DateTimeOffset StoredAt, DateTimeOffset ExpiresAt);
}

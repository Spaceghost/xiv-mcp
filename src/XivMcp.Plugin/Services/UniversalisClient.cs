using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Plugin.Services;

/// <summary>
/// Reads the public <see href="https://universalis.app">Universalis</see> market-board API from the host's
/// network. Nothing about the player is sent: only item ids and a world/data-centre name, which are the same
/// public values anyone can query. Answers are cached so a chatty agent cannot hammer a community service.
/// </summary>
/// <remarks>
/// The HTTP call is injected (<c>fetch</c>) so the request building, caching, rate limiting and error mapping
/// are testable on the host with no network.
/// </remarks>
public sealed class UniversalisClient : IDisposable
{
    public const string BaseUrl = "https://universalis.app/api/v2";

    /// <summary>The attribution string every result carries.</summary>
    public const string SourceName = "universalis.app";

    private static readonly TimeSpan PriceTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan WorldTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<string, CancellationToken, Task<string>> fetch;
    private readonly HttpClient? owned;
    private readonly MarketCache<JsonNode> cache = new(128);
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset lastRequest = DateTimeOffset.MinValue;

    public UniversalisClient(string userAgent)
    {
        owned = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        owned.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        owned.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        fetch = async (url, ct) =>
        {
            using var response = await owned.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UniversalisException(
                    $"{SourceName} answered {(int)response.StatusCode} {response.ReasonPhrase}." +
                    (response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? " That usually means the item is not sold on the market board, or the world/data-centre name is wrong."
                        : ""));
            }

            return body;
        };
    }

    /// <summary>Test constructor: supply the transport.</summary>
    public UniversalisClient(Func<string, CancellationToken, Task<string>> fetch) => this.fetch = fetch;

    /// <summary>Builds the market query URL. <paramref name="scope"/> is a world, data centre or region name.</summary>
    public static string PricesUrl(string scope, IReadOnlyList<uint> itemIds, int listings, int entries, bool? hq)
    {
        var ids = string.Join(',', itemIds);
        var url = $"{BaseUrl}/{Uri.EscapeDataString(scope)}/{ids}?listings={listings}&entries={entries}";
        if (hq.HasValue)
        {
            url += hq.Value ? "&hq=true" : "&hq=false";
        }

        return url;
    }

    public static string WorldsUrl() => $"{BaseUrl}/worlds";

    public static string DataCentersUrl() => $"{BaseUrl}/data-centers";

    /// <summary>Market data for up to 100 items. Returns the parsed JSON and when it was fetched.</summary>
    public Task<(JsonNode Json, DateTimeOffset FetchedAt, bool FromCache)> GetPricesAsync(
        string scope,
        IReadOnlyList<uint> itemIds,
        int listings,
        int entries,
        bool? hq,
        CancellationToken cancellationToken) =>
        GetJsonAsync(PricesUrl(scope, itemIds, listings, entries, hq), PriceTtl, cancellationToken);

    public Task<(JsonNode Json, DateTimeOffset FetchedAt, bool FromCache)> GetWorldsAsync(CancellationToken cancellationToken) =>
        GetJsonAsync(WorldsUrl(), WorldTtl, cancellationToken);

    public Task<(JsonNode Json, DateTimeOffset FetchedAt, bool FromCache)> GetDataCentersAsync(CancellationToken cancellationToken) =>
        GetJsonAsync(DataCentersUrl(), WorldTtl, cancellationToken);

    public void Dispose()
    {
        owned?.Dispose();
        gate.Dispose();
    }

    private async Task<(JsonNode Json, DateTimeOffset FetchedAt, bool FromCache)> GetJsonAsync(
        string url,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        if (cache.TryGet(url) is { } hit)
        {
            return (hit.Value, hit.StoredAt, true);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have filled it while we waited.
            if (cache.TryGet(url) is { } raced)
            {
                return (raced.Value, raced.StoredAt, true);
            }

            var since = DateTimeOffset.UtcNow - lastRequest;
            if (since < MinimumInterval)
            {
                await Task.Delay(MinimumInterval - since, cancellationToken).ConfigureAwait(false);
            }

            string body;
            try
            {
                body = await fetch(url, cancellationToken).ConfigureAwait(false);
            }
            catch (UniversalisException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new UniversalisException($"Could not reach {SourceName}: {ex.Message}", ex);
            }
            finally
            {
                lastRequest = DateTimeOffset.UtcNow;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new UniversalisException($"{SourceName} returned data this plugin could not parse.", ex);
            }

            if (node == null)
            {
                throw new UniversalisException($"{SourceName} returned an empty answer.");
            }

            var now = DateTimeOffset.UtcNow;
            cache.Set(url, node, ttl);
            return (node, now, false);
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>A Universalis request that failed in a way worth telling the caller about.</summary>
public sealed class UniversalisException : Exception
{
    public UniversalisException(string message) : base(message) { }

    public UniversalisException(string message, Exception inner) : base(message, inner) { }
}

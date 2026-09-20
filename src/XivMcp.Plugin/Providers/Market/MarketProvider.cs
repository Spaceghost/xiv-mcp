using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Market;

/// <summary>
/// Market-board prices from the public Universalis crowd-sourced API, plus the world/data-centre list from
/// game data. Read-only and nothing about the player leaves the machine: the requests carry item ids and a
/// world or data-centre name only.
/// </summary>
[McpProvider("market")]
public sealed class MarketProvider : IDisposable
{
    private const int MaxItems = 20;

    private readonly GameDataIndex index;
    private readonly IClientState clientState;
    private readonly UniversalisClient client;

    public MarketProvider(IDataManager data, IClientState clientState)
    {
        index = GameDataIndex.For(data);
        this.clientState = clientState;
        client = new UniversalisClient("XivMcp/0.1 (+https://github.com/Spaceghost/xiv-mcp)");
    }

    public sealed record ListingDto(
        long PricePerUnit,
        int Quantity,
        long Total,
        bool Hq,
        string? RetainerName,
        string? World,
        int? MateriaCount,
        DateTimeOffset? LastReviewUtc);

    public sealed record SaleDto(
        long PricePerUnit,
        int Quantity,
        long Total,
        bool Hq,
        string? World,
        DateTimeOffset? TimestampUtc);

    public sealed record ItemMarketDto(
        uint ItemId,
        string? Name,
        bool Marketable,
        string Scope,
        long? MinPrice,
        long? MinPriceNq,
        long? MinPriceHq,
        double? AveragePrice,
        double? AveragePriceNq,
        double? AveragePriceHq,
        double? NqSaleVelocityPerDay,
        double? HqSaleVelocityPerDay,
        int? UnitsForSale,
        int? UnitsSold,
        DateTimeOffset? LastUploadUtc,
        int ListingCount,
        List<ListingDto> Listings,
        List<SaleDto> RecentSales);

    public sealed record MarketResultDto(
        string Scope,
        string ScopeKind,
        string Source,
        string SourceUrl,
        DateTimeOffset FetchedAtUtc,
        bool FromCache,
        string DataNote,
        List<ItemMarketDto> Items,
        List<uint> UnresolvedItemIds);

    public sealed record WorldDto(uint Id, string Name, string? DataCenter, string? Region, bool IsCurrent, bool IsHome);

    public sealed record WorldsResultDto(
        string? CurrentWorld,
        string? HomeWorld,
        string? CurrentDataCenter,
        int Total,
        List<WorldDto> Worlds,
        string Note);

    [McpTool("get_market_prices",
        Title = "Get market board prices",
        GameThread = false,
        RequiresLogin = false,
        OpenWorld = true,
        Description =
            "Current market-board listings and recent sales for up to 20 items, from the public Universalis crowd-sourced database " +
            "(universalis.app) over the host's internet connection — NOT from the player's own market board window, so the data is only as " +
            "fresh as the last time some player uploaded it (lastUploadUtc tells you when). " +
            "scope is a world (\"Gilgamesh\"), a data centre (\"Aether\") or a region (\"North-America\"); omit it to use the character's " +
            "current world. Per item you get minPrice/minPriceNq/minPriceHq, averagePrice*, nq/hqSaleVelocityPerDay (units sold per day), " +
            "unitsForSale, unitsSold, then listings (cheapest first: pricePerUnit, quantity, total, hq, retainerName, world, materiaCount) " +
            "and recentSales (newest first). Untradable items come back with marketable=false and no prices. " +
            "Results are cached for about three minutes to stay polite to a community service. " +
            "Use search_items or get_item to turn a name into an item id first.")]
    public async Task<MarketResultDto> GetMarketPrices(
        [McpParam("Item ids to price (1-20). Get them from search_items or get_item.")] uint[] itemIds,
        [McpParam("World, data-centre or region name. Default: the character's current world.")] string? scope = null,
        [McpParam("Only high-quality (true) or only normal-quality (false) listings. Omit for both.")] bool? hq = null,
        [McpParam("Listings to return per item (1-50).", Minimum = 1, Maximum = 50)] int listings = 10,
        [McpParam("Recent sales to return per item (0-50).", Minimum = 0, Maximum = 50)] int recentSales = 5,
        ToolContext? ctx = null)
    {
        if (ctx == null)
        {
            throw new McpToolException("Tool context unavailable.");
        }

        if (itemIds == null || itemIds.Length == 0)
        {
            throw new McpToolException("itemIds must contain at least one item id. Use search_items to find one.");
        }

        if (itemIds.Length > MaxItems)
        {
            throw new McpToolException($"At most {MaxItems} item ids per call.");
        }

        listings = Math.Clamp(listings, 1, 50);
        recentSales = Math.Clamp(recentSales, 0, 50);
        var ids = itemIds.Distinct().Where(id => id != 0).ToList();
        if (ids.Count == 0)
        {
            throw new McpToolException("itemIds contained no usable item id.");
        }

        var unmarketable = ids.Where(id => index.Item(id) is { IsMarketable: false }).ToList();
        var tradable = ids.Except(unmarketable).ToList();

        var (resolvedScope, scopeKind) = await ResolveScopeAsync(scope, ctx).ConfigureAwait(false);
        var items = new List<ItemMarketDto>();
        var unresolved = new List<uint>();
        var fetchedAt = DateTimeOffset.UtcNow;
        var fromCache = false;
        var url = UniversalisClient.PricesUrl(resolvedScope, tradable, listings, Math.Max(recentSales, 1), hq);

        if (tradable.Count > 0)
        {
            JsonNode json;
            try
            {
                (json, fetchedAt, fromCache) = await client
                    .GetPricesAsync(resolvedScope, tradable, listings, Math.Max(recentSales, 1), hq, ctx.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UniversalisException ex)
            {
                throw new McpToolException(ex.Message);
            }

            foreach (var (id, node) in SplitItems(json, tradable, unresolved))
            {
                items.Add(MapItem(id, resolvedScope, node, listings, recentSales));
            }
        }

        foreach (var id in unmarketable)
        {
            items.Add(new ItemMarketDto(
                id, index.ItemName(id), false, resolvedScope,
                null, null, null, null, null, null, null, null, null, null, null, 0, [], []));
        }

        return new MarketResultDto(
            resolvedScope,
            scopeKind,
            UniversalisClient.SourceName,
            url,
            fetchedAt,
            fromCache,
            "Crowd-sourced: prices are whatever players last uploaded, not a live read of the market board. Check lastUploadUtc before trusting a number.",
            items.OrderBy(i => ids.IndexOf(i.ItemId)).ToList(),
            unresolved);
    }

    [McpTool("list_worlds",
        Title = "List worlds and data centres",
        GameThread = false,
        RequiresLogin = false,
        Description =
            "Every public world in the game with its data centre and region, from game data (no network call). Flags the character's " +
            "current and home world when logged in. Use it to pick a valid scope for get_market_prices, or to check which data centre a " +
            "world belongs to. Filter with nameContains (matches world, data centre or region) and page with limit/offset.")]
    public WorldsResultDto ListWorlds(
        [McpParam("Case-insensitive substring matched against world, data-centre or region name.")] string? nameContains = null,
        [McpParam("Maximum entries (1-500). Default 100.", Minimum = 1, Maximum = 500)] int limit = 100,
        [McpParam("Entries to skip for paging.", Minimum = 0)] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        var current = SafeWorldName(() => clientState.LocalPlayer?.CurrentWorld.RowId);
        var home = SafeWorldName(() => clientState.LocalPlayer?.HomeWorld.RowId);

        var all = new List<WorldDto>();
        string? currentDc = null;
        foreach (var world in index.Sheet<Sheets.World>())
        {
            if (!world.IsPublic)
            {
                continue;
            }

            var name = SheetJson.Text(world.Name);
            if (name.Length == 0)
            {
                continue;
            }

            var dc = world.DataCenter.ValueNullable;
            var dcName = dc is { } d ? GameDataIndex.NullIfEmpty(SheetJson.Text(d.Name)) : null;
            var region = dc is { } d2 ? RegionName(d2.Region) : null;
            var isCurrent = name.Equals(current, StringComparison.OrdinalIgnoreCase);
            if (isCurrent)
            {
                currentDc = dcName;
            }

            all.Add(new WorldDto(world.RowId, name, dcName, region, isCurrent, name.Equals(home, StringComparison.OrdinalIgnoreCase)));
        }

        var needle = nameContains?.Trim();
        var matching = string.IsNullOrEmpty(needle)
            ? all
            : all.Where(w =>
                w.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                (w.DataCenter?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (w.Region?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        var page = matching
            .OrderBy(w => w.Region, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return new WorldsResultDto(
            current,
            home,
            currentDc,
            matching.Count,
            page,
            "From the game's World sheet; private/test worlds are omitted. Data-centre and region names are the ones get_market_prices accepts as scope.");
    }

    public void Dispose() => client.Dispose();

    /// <summary>Splits the Universalis answer (single item or multi-item envelope) into per-item nodes.</summary>
    internal static IEnumerable<(uint ItemId, JsonObject Node)> SplitItems(JsonNode json, IReadOnlyList<uint> requested, List<uint> unresolved)
    {
        if (json is not JsonObject root)
        {
            yield break;
        }

        if (root["items"] is JsonObject bag)
        {
            if (root["unresolvedItems"] is JsonArray missing)
            {
                foreach (var entry in missing)
                {
                    if (entry?.GetValue<uint>() is { } id)
                    {
                        unresolved.Add(id);
                    }
                }
            }

            foreach (var id in requested)
            {
                if (bag[id.ToString()] is JsonObject item)
                {
                    yield return (id, item);
                }
                else if (!unresolved.Contains(id))
                {
                    unresolved.Add(id);
                }
            }

            yield break;
        }

        var single = requested.Count > 0 ? requested[0] : 0;
        if (root["itemID"]?.GetValue<uint>() is { } reported && reported != 0)
        {
            single = reported;
        }

        yield return (single, root);
    }

    private ItemMarketDto MapItem(uint itemId, string scope, JsonObject node, int maxListings, int maxSales)
    {
        var listings = new List<ListingDto>();
        if (node["listings"] is JsonArray listingArray)
        {
            foreach (var entry in listingArray.OfType<JsonObject>().Take(maxListings))
            {
                var price = Long(entry, "pricePerUnit") ?? 0;
                var quantity = (int)(Long(entry, "quantity") ?? 0);
                listings.Add(new ListingDto(
                    price,
                    quantity,
                    Long(entry, "total") ?? price * quantity,
                    Bool(entry, "hq"),
                    Str(entry, "retainerName"),
                    Str(entry, "worldName"),
                    entry["materia"] is JsonArray materia ? materia.Count : null,
                    Unix(entry, "lastReviewTime")));
            }
        }

        var sales = new List<SaleDto>();
        if (maxSales > 0 && node["recentHistory"] is JsonArray historyArray)
        {
            foreach (var entry in historyArray.OfType<JsonObject>().Take(maxSales))
            {
                var price = Long(entry, "pricePerUnit") ?? 0;
                var quantity = (int)(Long(entry, "quantity") ?? 0);
                sales.Add(new SaleDto(
                    price,
                    quantity,
                    price * quantity,
                    Bool(entry, "hq"),
                    Str(entry, "worldName"),
                    Unix(entry, "timestamp")));
            }
        }

        return new ItemMarketDto(
            itemId,
            index.ItemName(itemId),
            true,
            scope,
            Long(node, "minPrice"),
            Long(node, "minPriceNQ"),
            Long(node, "minPriceHQ"),
            Double(node, "currentAveragePrice"),
            Double(node, "currentAveragePriceNQ"),
            Double(node, "currentAveragePriceHQ"),
            Double(node, "nqSaleVelocity"),
            Double(node, "hqSaleVelocity"),
            (int?)Long(node, "unitsForSale"),
            (int?)Long(node, "unitsSold"),
            Unix(node, "lastUploadTime", milliseconds: true),
            node["listings"] is JsonArray all ? all.Count : listings.Count,
            listings,
            sales);
    }

    private async Task<(string Scope, string Kind)> ResolveScopeAsync(string? scope, ToolContext ctx)
    {
        var requested = scope?.Trim();
        if (!string.IsNullOrEmpty(requested))
        {
            return (requested, ClassifyScope(requested));
        }

        var world = await ctx.Game
            .InvokeAsync(() => SafeWorldName(() => clientState.LocalPlayer?.CurrentWorld.RowId), ctx.CancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(world))
        {
            throw new McpToolException("No scope given and no character is logged in. Pass scope with a world, data-centre or region name (see list_worlds).");
        }

        return (world, "world");
    }

    private string ClassifyScope(string scope)
    {
        foreach (var world in index.Sheet<Sheets.World>())
        {
            if (world.IsPublic && SheetJson.Text(world.Name).Equals(scope, StringComparison.OrdinalIgnoreCase))
            {
                return "world";
            }
        }

        foreach (var dc in index.Sheet<Sheets.WorldDCGroupType>())
        {
            if (SheetJson.Text(dc.Name).Equals(scope, StringComparison.OrdinalIgnoreCase))
            {
                return "dataCenter";
            }
        }

        return "region";
    }

    private string? SafeWorldName(Func<uint?> read)
    {
        try
        {
            var id = read();
            if (id is not { } rowId || rowId == 0)
            {
                return null;
            }

            return index.Row<Sheets.World>(rowId) is { } world
                ? GameDataIndex.NullIfEmpty(SheetJson.Text(world.Name))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? RegionName(byte region) => region switch
    {
        1 => "Japan",
        2 => "North-America",
        3 => "Europe",
        4 => "Oceania",
        5 => "China",
        6 => "Korea",
        _ => null,
    };

    private static long? Long(JsonObject node, string name) =>
        node[name] is { } value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? (long)Math.Round(value.GetValue<double>())
            : null;

    private static double? Double(JsonObject node, string name) =>
        node[name] is { } value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? Math.Round(value.GetValue<double>(), 2)
            : null;

    private static bool Bool(JsonObject node, string name) =>
        node[name] is { } value && value.GetValueKind() == System.Text.Json.JsonValueKind.True;

    private static string? Str(JsonObject node, string name) =>
        node[name] is { } value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? GameDataIndex.NullIfEmpty(value.GetValue<string>())
            : null;

    private static DateTimeOffset? Unix(JsonObject node, string name, bool milliseconds = false)
    {
        if (Long(node, name) is not { } raw || raw <= 0)
        {
            return null;
        }

        // Universalis uses seconds for sales/listings and milliseconds for lastUploadTime.
        return milliseconds || raw > 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
            : DateTimeOffset.FromUnixTimeSeconds(raw);
    }
}

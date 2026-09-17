using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Inventory;

/// <summary>Live inventory, equipment and currency state of the logged-in character (framework thread).</summary>
[McpProvider("inventory")]
public sealed unsafe class InventoryProvider : IDisposable
{
    public const string InventoryUri = "ffxiv://inventory";

    private static readonly string[] EquipSlotNames =
        ["MainHand", "OffHand", "Head", "Body", "Hands", "Waist", "Legs", "Feet", "Ears", "Neck", "Wrists", "RingRight", "RingLeft", "SoulCrystal"];

    /// <summary>Known currencies by category; counts come from CurrencyManager/InventoryManager at call time.</summary>
    private static readonly (string Category, uint ItemId)[] KnownCurrencies =
    [
        ("common", 21072), // Venture
        ("common", 29), // MGP
        ("pvp", 25), // Wolf Mark
        ("pvp", 36656), // Trophy Crystal
        ("hunt", 27), // Allied Seal
        ("hunt", 10307), // Centurio Seal
        ("hunt", 26533), // Sack of Nuts
        ("fate", 26807), // Bicolor Gemstone
        ("scrip", 41784), // Orange Crafters' Scrip
        ("scrip", 41785), // Orange Gatherers' Scrip
        ("scrip", 33913), // Purple Crafters' Scrip
        ("scrip", 33914), // Purple Gatherers' Scrip
        ("scrip", 28063), // Skybuilders' Scrip
        ("field", 31135), // Bozjan Cluster
        ("other", 37549), // Seafarer's Cowrie
        ("other", 37550), // Islander's Cowrie
        ("other", 30341), // Faux Leaf
        ("other", 45690), // Cosmocredit
        ("other", 45691), // Lunar Credit
        ("other", 21172), // Achievement Certificate
    ];

    private readonly GameDataIndex index;
    private readonly IPlayerState playerState;
    private readonly IGameInventory gameInventory;
    private readonly IMcpNotifier notifier;
    private long lastNotifyTicks;

    public InventoryProvider(IDataManager data, IPlayerState playerState, IGameInventory gameInventory, IMcpNotifier notifier)
    {
        index = GameDataIndex.For(data);
        this.playerState = playerState;
        this.gameInventory = gameInventory;
        this.notifier = notifier;
        gameInventory.InventoryChanged += OnInventoryChanged;
    }

    public void Dispose() => gameInventory.InventoryChanged -= OnInventoryChanged;

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        try
        {
            // Coalesce bursts (e.g. sorting) to at most one notification per second.
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastNotifyTicks) < 1000) return;
            Interlocked.Exchange(ref lastNotifyTicks, now);
            notifier.ResourceUpdated(InventoryUri);
        }
        catch
        {
            // Never let an exception escape a game event handler.
        }
    }

    [McpTool("get_inventory",
        Title = "Get inventory contents",
        Description =
            "Lists items in the character's containers, slot by slot. containers selects groups: bags (4 main inventory pages), equipped, armory " +
            "(armoury chest), crystals, currency, keyItems, saddlebag, premiumSaddlebag, retainer (pages + crystals of the retainer currently/last opened), " +
            "retainerEquipped, retainerMarket (with listing prices), or all (default: bags). Each entry has container, group, slot, itemId, name, quantity, " +
            "and when applicable hq, collectable + collectability, item level, condition % and spiritbond % (gear), materia with stat bonus, glamour item, dyes. " +
            "Containers that are not in client memory are listed under unavailable with the reason (saddlebags need to have been opened this session; " +
            "retainer containers need a retainer opened at a summoning bell). Output is paged (limit/offset) with per-container size/used counts. " +
            "To locate specific items across all containers prefer find_owned_items.",
        RequiresLogin = true)]
    public InventoryResult GetInventory(
        [McpParam("Container groups to list; default [\"bags\"]. \"all\" selects every group.",
            Enum = ["bags", "equipped", "armory", "crystals", "currency", "keyItems", "saddlebag", "premiumSaddlebag", "retainer", "retainerEquipped", "retainerMarket", "all"])]
        string[]? containers = null,
        [McpParam("Include empty slots (itemId 0) in items.")] bool includeEmpty = false,
        [McpParam("Maximum slot entries (1-500).", Minimum = 1, Maximum = 500)] int limit = 100,
        [McpParam("Entries to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var groups = InventoryReader.Resolve(containers is { Length: > 0 } ? containers : ["bags"]);
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        var statuses = new List<ContainerStatus>();
        var unavailable = new List<string>();
        var items = new List<InventorySlot>();
        var total = 0;
        var anyRetainer = false;

        foreach (var group in groups)
        {
            var groupLoaded = false;
            foreach (var type in group.Types)
            {
                var container = InventoryReader.Container(type);
                if (!InventoryReader.IsLoaded(container))
                {
                    statuses.Add(new ContainerStatus(InventoryReader.ContainerName(type), group.Key, false, 0, 0));
                    continue;
                }

                groupLoaded = true;
                var used = 0;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null) continue;
                    var occupied = slot->ItemId != 0;
                    if (occupied) used++;
                    if (!occupied && !includeEmpty) continue;
                    total++;
                    if (total <= offset || items.Count >= limit) continue;
                    items.Add(occupied
                        ? InventoryReader.ToSlot(index, slot, type, group.Key, i)
                        : new InventorySlot(InventoryReader.ContainerName(type), group.Key, i, 0, "", 0));
                }

                statuses.Add(new ContainerStatus(InventoryReader.ContainerName(type), group.Key, true, container->Size, used));
            }

            if (!groupLoaded) unavailable.Add($"{group.Key}: {group.UnloadedNote}");
            else if (InventoryReader.IsRetainerGroup(group)) anyRetainer = true;
        }

        return new InventoryResult(
            statuses,
            unavailable.Count > 0 ? unavailable : null,
            anyRetainer ? InventoryReader.ActiveRetainerName() : null,
            total,
            offset,
            items.Count,
            offset + items.Count < total,
            items);
    }

    [McpTool("find_owned_items",
        Title = "Find items the character owns",
        Description =
            "Searches every loaded container (bags, equipped, armory, crystals, currency, key items, saddlebags if opened this session, and the " +
            "currently/last opened retainer's inventory, equipment and market listings) for items by name (case-insensitive substring; " +
            "exact and prefix matches rank first) and/or exact itemIds, and aggregates counts per item: total quantity, HQ quantity, and a " +
            "location breakdown (group, container, quantity, slots). notSearched lists container groups that are not in memory, so absence " +
            "there is unknown rather than zero. Other retainers' inventories are never visible without opening them.",
        RequiresLogin = true)]
    public OwnedItemsResult FindOwnedItems(
        [McpParam("Item name text to search for (any case). Provide this and/or itemIds.")] string? query = null,
        [McpParam("Exact item ids to look for.")] uint[]? itemIds = null,
        [McpParam("Maximum distinct items returned (1-500).", Minimum = 1, Maximum = 500)] int limit = 50)
    {
        var q = TextSearch.Normalize(query);
        var tokens = TextSearch.Tokens(q);
        var ids = itemIds is { Length: > 0 } ? itemIds.Select(i => i > 1_000_000 && i < 2_000_000 ? i - 1_000_000 : i).ToHashSet() : null;
        if (q.Length == 0 && ids == null) throw new McpToolException("Provide query or itemIds.");
        limit = Math.Clamp(limit, 1, 500);

        var found = new Dictionary<(uint Id, bool Key), (string Name, int Rank, int Total, int Hq, Dictionary<(string Group, string Container), (int Qty, int Slots, int Hq)> Locations)>();
        var notSearched = new List<string>();
        var sawRetainer = false;

        foreach (var group in InventoryReader.Groups)
        {
            var groupLoaded = false;
            foreach (var type in group.Types)
            {
                var container = InventoryReader.Container(type);
                if (!InventoryReader.IsLoaded(container)) continue;
                groupLoaded = true;
                var isKey = type == InventoryType.KeyItems;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    var itemId = slot->ItemId;
                    var key = (itemId, isKey);
                    int rank;
                    string name;
                    if (found.TryGetValue(key, out var existing))
                    {
                        rank = existing.Rank;
                        name = existing.Name;
                    }
                    else
                    {
                        name = InventoryReader.ItemName(index, itemId, type);
                        rank = ids != null && !isKey && ids.Contains(itemId) ? 0 : q.Length > 0 ? TextSearch.Rank(name.ToLowerInvariant(), q, tokens) : TextSearch.NoMatch;
                        if (rank == TextSearch.NoMatch) continue;
                        existing = (name, rank, 0, 0, new());
                    }

                    var qty = slot->Quantity;
                    var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0 ? qty : 0;
                    var locKey = (group.Key, InventoryReader.ContainerName(type));
                    var loc = existing.Locations.GetValueOrDefault(locKey);
                    existing.Locations[locKey] = (loc.Qty + qty, loc.Slots + 1, loc.Hq + hq);
                    found[key] = (name, rank, existing.Total + qty, existing.Hq + hq, existing.Locations);
                }
            }

            if (!groupLoaded) notSearched.Add($"{group.Key}: {group.UnloadedNote}");
            else if (InventoryReader.IsRetainerGroup(group)) sawRetainer = true;
        }

        var ordered = found
            .OrderBy(kv => kv.Value.Rank)
            .ThenByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var results = ordered.Take(limit).Select(kv => new OwnedItem(
                kv.Key.Id,
                kv.Value.Name,
                kv.Value.Total,
                kv.Value.Hq > 0 ? kv.Value.Hq : null,
                kv.Value.Locations.Select(l => new OwnedLocation(l.Key.Group, l.Key.Container, l.Value.Qty, l.Value.Slots, l.Value.Hq > 0 ? l.Value.Hq : null)).ToList()))
            .ToList();

        return new OwnedItemsResult(ordered.Count, results.Count, ordered.Count > results.Count, results,
            notSearched.Count > 0 ? notSearched : null, sawRetainer ? InventoryReader.ActiveRetainerName() : null);
    }

    [McpTool("get_equipment",
        Title = "Get equipped gear",
        Description =
            "The character's currently equipped gear: for each occupied slot (MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, " +
            "RingRight, RingLeft, SoulCrystal) the item id, name, item level, equip level, HQ, condition %, spiritbond %, melded materia with stat bonus, " +
            "glamour item and dyes; empty slots are listed separately. averageItemLevel is computed like the character screen: the 12 gear slots " +
            "(soul crystal excluded) summed with the main hand counted again when it is two-handed and the off hand is empty, divided by 12 and floored. " +
            "Also returns the current class/job and soul crystal name.",
        RequiresLogin = true)]
    public EquipmentResult GetEquipment()
    {
        var container = InventoryReader.Container(InventoryType.EquippedItems);
        if (!InventoryReader.IsLoaded(container))
            throw new McpToolException("Equipped items are not loaded yet; wait until the character has finished loading.");

        var slots = new List<EquippedSlot>();
        var empty = new List<string>();
        var levels = new int[EquipSlotNames.Length];
        var mainHandBlocksOffHand = false;
        string? soulCrystal = null;

        for (var i = 0; i < container->Size && i < EquipSlotNames.Length; i++)
        {
            var slotName = EquipSlotNames[i];
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0)
            {
                if (i != 5) empty.Add(slotName); // Waist slot no longer exists in game.
                continue;
            }

            var row = index.Row<Sheets.Item>(item->ItemId);
            var ilvl = row is { } r ? (int)r.LevelItem.RowId : 0;
            levels[i] = ilvl;
            if (i == 0 && row is { } main && main.EquipSlotCategory.ValueNullable is { OffHand: < 0 }) mainHandBlocksOffHand = true;
            var name = index.ItemName(item->ItemId);
            if (i == 13) soulCrystal = name;
            var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            slots.Add(new EquippedSlot(
                slotName,
                item->ItemId,
                name,
                ilvl,
                row?.LevelEquip ?? 0,
                hq ? true : null,
                i == 13 ? null : Math.Round(item->Condition / 300.0, 1),
                i == 13 ? null : Math.Round(item->SpiritbondOrCollectability / 100.0, 2),
                InventoryReader.Materia(index, item),
                item->GlamourId != 0 ? item->GlamourId : null,
                item->GlamourId != 0 ? index.ItemName(item->GlamourId) : null,
                InventoryReader.Dyes(index, item)));
        }

        var sum = 0;
        for (var i = 0; i <= 12; i++)
        {
            if (i == 5) continue;
            sum += levels[i];
        }

        if (levels[1] == 0 && mainHandBlocksOffHand) sum += levels[0];
        var average = sum / 12;

        string? job = null;
        try
        {
            var classJob = playerState.ClassJob;
            if (classJob.RowId != 0) job = index.ClassJob(classJob.RowId)?.Name;
        }
        catch
        {
            // Player state unavailable: omit the job.
        }

        return new EquipmentResult(
            job,
            average,
            mainHandBlocksOffHand && levels[1] == 0
                ? "main hand counted twice (two-handed weapon), 12 slots, floored"
                : "12 gear slots including off hand, floored",
            soulCrystal,
            slots,
            empty);
    }

    [McpTool("get_currencies",
        Title = "Get currencies",
        Description =
            "The character's currency balances: gil; Grand Company seals for the current company with its cap; and a list of currencies with " +
            "category (common: ventures, MGP; tomestone: every current tomestone with weeklyAcquired/weeklyLimit on the weekly-capped one; scrip: " +
            "orange/purple crafters' and gatherers' scrips, Skybuilders'; pvp: wolf marks, trophy crystals; hunt: allied/centurio seals, sacks of nuts; " +
            "fate: bicolor gemstones; field: Bozjan clusters; other: cowries, faux leaves, cosmocredits, lunar credits, achievement certificates; " +
            "plus any other currency the client tracks), count and cap (max) where the game defines one. Zero balances are included for listed currencies.",
        RequiresLogin = true)]
    public CurrenciesResult GetCurrencies()
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null) throw new McpToolException("Inventory is not available yet.");
        var currency = CurrencyManager.Instance();

        GrandCompanySeals? seals = null;
        try
        {
            var gc = playerState.GrandCompany.RowId;
            if (gc is >= 1 and <= 3)
            {
                var gcName = index.Row<Sheets.GrandCompany>(gc) is { } g ? SheetJson.Text(g.Name) : $"Grand Company #{gc}";
                seals = new GrandCompanySeals(gc, gcName, 19 + gc, inventory->GetCompanySeals((byte)gc), inventory->GetMaxCompanySeals((byte)gc));
            }
        }
        catch
        {
            // Player state unavailable: omit seals.
        }

        var entries = new List<CurrencyEntry>();
        var seen = new HashSet<uint> { 1, 20, 21, 22 };

        void AddEntry(string category, uint itemId, long? weeklyAcquired = null, long? weeklyLimit = null)
        {
            if (!seen.Add(itemId)) return;
            long count;
            long? max = null;
            if (itemId == 29) count = inventory->GetGoldSaucerCoin();
            else if (itemId == 25) count = inventory->GetWolfMarks();
            else if (itemId == 27) count = inventory->GetAlliedSeals();
            else if (currency != null && currency->HasItem(itemId))
            {
                count = currency->GetItemCount(itemId);
                var m = currency->GetItemMaxCount(itemId);
                if (m > 0) max = m;
            }
            else count = inventory->GetInventoryItemCount(itemId, false, false, false);

            if (max == null && currency != null && currency->HasItem(itemId))
            {
                var m = currency->GetItemMaxCount(itemId);
                if (m > 0) max = m;
            }

            entries.Add(new CurrencyEntry(category, itemId, index.ItemName(itemId), count, max, weeklyAcquired, weeklyLimit));
        }

        foreach (var (category, itemId) in KnownCurrencies.Where(c => c.Category == "common")) AddEntry(category, itemId);

        foreach (var tome in index.Sheet<Sheets.TomestonesItem>())
        {
            if (tome.Tomestones.RowId == 0 || tome.Item.RowId == 0) continue;
            var weekly = tome.Tomestones.ValueNullable?.WeeklyLimit ?? 0;
            if (weekly > 0)
                AddEntry("tomestone", tome.Item.RowId, inventory->GetWeeklyAcquiredTomestoneCount(), InventoryManager.GetLimitedTomestoneWeeklyLimit());
            else
                AddEntry("tomestone", tome.Item.RowId);
        }

        foreach (var (category, itemId) in KnownCurrencies.Where(c => c.Category != "common")) AddEntry(category, itemId);

        if (currency != null)
        {
            // Anything else the client tracks as a currency (new content currencies), only when non-zero.
            var extra = new List<uint>();
            foreach (var pair in currency->ItemBucket) extra.Add(pair.Item1);
            foreach (var pair in currency->ContentItemBucket) extra.Add(pair.Item1);
            foreach (var pair in currency->SpecialItemBucket) extra.Add(pair.Item1);
            foreach (var itemId in extra.Distinct())
            {
                if (itemId == 0 || seen.Contains(itemId) || currency->GetItemCount(itemId) == 0) continue;
                AddEntry("other", itemId);
            }
        }

        return new CurrenciesResult(inventory->GetGil(), seals, entries);
    }

    [McpResource(InventoryUri,
        Name = "Inventory (bags)",
        Description = "Main inventory bags (4 pages) with per-container usage; updated notifications are sent when the inventory changes.",
        RequiresLogin = true)]
    public InventoryResult InventoryResource() => GetInventory(["bags"], false, 500, 0);
}

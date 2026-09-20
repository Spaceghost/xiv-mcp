using System.Collections.Frozen;
using System.Reflection;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Data;
using Lumina.Excel;
using XivMcp.Plugin.Util;
using LuminaGameData = Lumina.GameData;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Lazily built, thread-safe lookup indexes over Lumina sheets in the client language. Each index is
/// built once on first use (on whichever thread asks, which for data tools is never the framework
/// thread) and then shared by every provider.
/// </summary>
internal sealed class GameDataIndex : IDisposable
{
    private static readonly object Gate = new();
    private static GameDataIndex? current;

    public GameDataIndex(ExcelModule module, Language language, LuminaGameData gameData)
    {
        Module = module;
        Language = language;
        GameData = gameData;

        items = new(BuildItems);
        recipes = new(BuildRecipes);
        gathering = new(BuildGathering);
        gilShops = new(BuildGilShops);
        specialShops = new(BuildSpecialShops);
        shopNpcs = new(BuildShopNpcs);
        npcLevels = new(BuildNpcLevels);
        actions = new(BuildActions);
        quests = new(BuildQuests);
        duties = new(BuildDuties);
        classJobs = new(BuildClassJobs);
        instanceUnlockQuests = new(BuildInstanceUnlockQuests);
    }

    public static GameDataIndex For(IDataManager data)
    {
        var language = data.Language.ToLumina();
        lock (Gate)
        {
            if (current == null || !ReferenceEquals(current.Module, data.Excel) || current.Language != language)
            {
                current = new GameDataIndex(data.Excel, language, data.GameData);
                current.StartWarmup();
            }

            return current;
        }
    }

    /// <summary>
    /// Builds the search indexes on a low-priority background thread so the first search is fast.
    /// Never runs on the framework thread; failures only mean the index is built on first use instead.
    /// </summary>
    private void StartWarmup()
    {
        var token = warmupCancel.Token;
        var thread = new Thread(() =>
        {
            try
            {
                if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) return;
                _ = items.Value;
                if (token.IsCancellationRequested) return;
                _ = recipes.Value;
                if (token.IsCancellationRequested) return;
                _ = gathering.Value;
            }
            catch
            {
                // Not cached on failure: the index is simply rebuilt (and the error surfaced) on first use.
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "XivMcp game data index warmup",
        };
        thread.Start();
        warmupThread = thread;
    }

    // warmupCancel's token is waited on through token.WaitHandle, so the source owns a real OS
    // wait handle: leaving it undisposed leaks a handle on every plugin reload.
    private readonly CancellationTokenSource warmupCancel = new();
    private Thread? warmupThread;

    /// <summary>
    /// Drops the shared index and stops background warmup (called when the plugin unloads its providers) so no
    /// thread keeps the plugin's load context alive.
    /// </summary>
    public static void Release()
    {
        GameDataIndex? released;
        lock (Gate)
        {
            released = current;
            current = null;
        }

        released?.Dispose();
    }

    /// <summary>
    /// Stops the warmup thread and releases its wait handle. Public so the unload path (and the
    /// leak tests) can assert the handle is gone; <see cref="Release"/> is the normal caller.
    /// </summary>
    public void Dispose()
    {
        try
        {
            warmupCancel.Cancel();
        }
        catch (Exception)
        {
            // Already cancelled or disposed; the join and dispose below still apply.
        }

        // The thread is waiting on warmupCancel's wait handle: let it observe the cancel before
        // the source (and with it the handle) is disposed.
        var thread = Interlocked.Exchange(ref warmupThread, null);
        try
        {
            thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // A thread that will not join must not block plugin unload.
        }

        warmupCancel.Dispose();
    }

    public ExcelModule Module { get; }

    public Language Language { get; }

    public LuminaGameData GameData { get; }

    public ExcelSheet<T> Sheet<T>() where T : struct, IExcelRow<T> => Module.GetSheet<T>(Language);

    public SubrowExcelSheet<T> SubrowSheet<T>() where T : struct, IExcelSubrow<T> => Module.GetSubrowSheet<T>(Language);

    public T? Row<T>(uint rowId) where T : struct, IExcelRow<T> =>
        Sheet<T>().TryGetRow(rowId, out var row) ? row : null;

    // ------------------------------------------------------------------ items

    internal sealed record ItemEntry(
        uint Id,
        string Name,
        string Lower,
        int ItemLevel,
        int EquipLevel,
        uint UiCategory,
        uint EquipSlotCategory,
        uint ClassJobCategory,
        uint SearchCategory,
        int Rarity,
        bool Untradable,
        uint Icon,
        byte FilterGroup)
    {
        public bool IsMarketable => SearchCategory != 0;

        /// <summary>Item.FilterGroup 11 = crystals/shards/clusters.</summary>
        public bool IsCrystal => FilterGroup == 11;
    }

    internal sealed record ItemTable(ItemEntry[] List, FrozenDictionary<uint, ItemEntry> ById);

    private readonly Cached<ItemTable> items;

    public IReadOnlyList<ItemEntry> Items => items.Value.List;

    public ItemEntry? Item(uint id) => items.Value.ById.GetValueOrDefault(id);

    /// <summary>Item name, or "Item #id" when unknown; HQ offset (1,000,000) is ignored.</summary>
    public string ItemName(uint id)
    {
        if (id > 1_000_000 && id < 2_000_000) id -= 1_000_000;
        // Only consult the name index once built: live tools call this on the framework thread and must not trigger a full build.
        if (items.IsValueCreated && Item(id) is { } entry) return entry.Name;
        return Row<Sheets.Item>(id) is { } row && SheetJson.Text(row.Name) is { Length: > 0 } name ? name : $"Item #{id}";
    }

    private ItemTable BuildItems()
    {
        var list = new List<ItemEntry>(52000);
        foreach (var row in Sheet<Sheets.Item>())
        {
            var name = SheetJson.Text(row.Name);
            if (name.Length == 0) continue;
            list.Add(new ItemEntry(
                row.RowId,
                name,
                name.ToLowerInvariant(),
                (int)row.LevelItem.RowId,
                row.LevelEquip,
                row.ItemUICategory.RowId,
                row.EquipSlotCategory.RowId,
                row.ClassJobCategory.RowId,
                row.ItemSearchCategory.RowId,
                row.Rarity,
                row.IsUntradable,
                row.Icon,
                row.FilterGroup));
        }

        var array = list.ToArray();
        return new ItemTable(array, array.ToFrozenDictionary(e => e.Id));
    }

    // ------------------------------------------------------------------ class jobs

    internal sealed record ClassJobEntry(uint Id, string Name, string Abbreviation, string EnglishAbbreviation, uint ParentId, PropertyInfo? CategoryColumn);

    private readonly Cached<ClassJobEntry[]> classJobs;

    public IReadOnlyList<ClassJobEntry> ClassJobs => classJobs.Value;

    public ClassJobEntry? ClassJob(uint id) => classJobs.Value.FirstOrDefault(c => c.Id == id);

    /// <summary>Resolves an abbreviation (WAR, client or English), a name (Warrior) or a numeric id.</summary>
    public ClassJobEntry? FindClassJob(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (uint.TryParse(t, out var id)) return ClassJob(id);
        return classJobs.Value.FirstOrDefault(c => c.Abbreviation.Equals(t, StringComparison.OrdinalIgnoreCase)
                                                   || c.EnglishAbbreviation.Equals(t, StringComparison.OrdinalIgnoreCase))
               ?? classJobs.Value.FirstOrDefault(c => c.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when ClassJobCategory row <paramref name="categoryId"/> includes the class/job.</summary>
    public bool CategoryIncludes(uint categoryId, ClassJobEntry job)
    {
        if (job.CategoryColumn == null || categoryId == 0) return false;
        if (!Sheet<Sheets.ClassJobCategory>().TryGetRow(categoryId, out var row)) return false;
        try
        {
            return job.CategoryColumn.GetValue(row) is true;
        }
        catch
        {
            return false;
        }
    }

    public string? ClassJobCategoryName(uint categoryId) =>
        categoryId != 0 && Row<Sheets.ClassJobCategory>(categoryId) is { } row ? NullIfEmpty(SheetJson.Text(row.Name)) : null;

    private ClassJobEntry[] BuildClassJobs()
    {
        var english = Module.GetSheet<Sheets.ClassJob>(Language.English);
        var list = new List<ClassJobEntry>();
        foreach (var row in Sheet<Sheets.ClassJob>())
        {
            var abbr = SheetJson.Text(row.Abbreviation);
            var enAbbr = english.TryGetRow(row.RowId, out var en) ? SheetJson.Text(en.Abbreviation) : abbr;
            if (abbr.Length == 0 && enAbbr.Length == 0) continue;
            var column = typeof(Sheets.ClassJobCategory).GetProperty(enAbbr, BindingFlags.Public | BindingFlags.Instance);
            var name = SheetJson.Text(row.Name);
            list.Add(new ClassJobEntry(row.RowId, name.Length > 0 ? Capitalize(name) : SheetJson.Text(row.NameEnglish),
                abbr.Length > 0 ? abbr : enAbbr, enAbbr, row.ClassJobParent.RowId,
                column?.PropertyType == typeof(bool) ? column : null));
        }

        return list.ToArray();
    }

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ------------------------------------------------------------------ recipes

    internal sealed record RecipeEntry(
        uint Id,
        uint ItemId,
        string ItemName,
        string Lower,
        uint CraftType,
        int Level,
        int Stars);

    internal sealed class RecipeIndex
    {
        public required RecipeEntry[] All { get; init; }

        public required FrozenDictionary<uint, uint[]> ByResult { get; init; }

        public required FrozenDictionary<uint, (uint RecipeId, int Amount)[]> ByIngredient { get; init; }
    }

    private readonly Cached<RecipeIndex> recipes;

    public RecipeIndex Recipes => recipes.Value;

    public bool IsCraftable(uint itemId) => recipes.Value.ByResult.ContainsKey(itemId);

    private RecipeIndex BuildRecipes()
    {
        var all = new List<RecipeEntry>(12000);
        var byResult = new Dictionary<uint, List<uint>>();
        var byIngredient = new Dictionary<uint, List<(uint, int)>>();
        foreach (var row in Sheet<Sheets.Recipe>())
        {
            var result = row.ItemResult.RowId;
            if (result == 0) continue;
            var name = ItemName(result);
            var level = row.RecipeLevelTable.ValueNullable;
            all.Add(new RecipeEntry(row.RowId, result, name, name.ToLowerInvariant(), row.CraftType.RowId,
                level?.ClassJobLevel ?? 0, level?.Stars ?? 0));
            Add(byResult, result, row.RowId);
            for (var i = 0; i < row.Ingredient.Count; i++)
            {
                var ingredient = row.Ingredient[i].RowId;
                var amount = row.AmountIngredient[i];
                if (ingredient == 0 || amount == 0) continue;
                Add(byIngredient, ingredient, (row.RowId, (int)amount));
            }
        }

        return new RecipeIndex
        {
            All = all.ToArray(),
            ByResult = byResult.ToFrozenDictionary(k => k.Key, v => v.Value.ToArray()),
            ByIngredient = byIngredient.ToFrozenDictionary(k => k.Key, v => v.Value.ToArray()),
        };
    }

    public string CraftTypeName(uint craftType) =>
        Row<Sheets.CraftType>(craftType) is { } row && SheetJson.Text(row.Name) is { Length: > 0 } n ? n : $"CraftType #{craftType}";

    // ------------------------------------------------------------------ gathering

    internal sealed class GatheringIndex
    {
        /// <summary>Item id → GatheringItem row ids.</summary>
        public required FrozenDictionary<uint, uint[]> ByItem { get; init; }

        /// <summary>GatheringItem id → GatheringPointBase ids.</summary>
        public required FrozenDictionary<uint, uint[]> BasesByGatheringItem { get; init; }

        /// <summary>GatheringPointBase id → GatheringPoint ids.</summary>
        public required FrozenDictionary<uint, uint[]> PointsByBase { get; init; }
    }

    private readonly Cached<GatheringIndex> gathering;

    public GatheringIndex Gathering => gathering.Value;

    public bool IsGatherable(uint itemId) => gathering.Value.ByItem.ContainsKey(itemId);

    private GatheringIndex BuildGathering()
    {
        var byItem = new Dictionary<uint, List<uint>>();
        foreach (var row in Sheet<Sheets.GatheringItem>())
        {
            var item = row.Item;
            if (item.RowId == 0 || !item.Is<Sheets.Item>()) continue;
            Add(byItem, item.RowId, row.RowId);
        }

        var basesByItem = new Dictionary<uint, List<uint>>();
        foreach (var row in Sheet<Sheets.GatheringPointBase>())
        {
            foreach (var reference in row.Item)
            {
                if (reference.RowId == 0 || !reference.Is<Sheets.GatheringItem>()) continue;
                Add(basesByItem, reference.RowId, row.RowId);
            }
        }

        var pointsByBase = new Dictionary<uint, List<uint>>();
        foreach (var row in Sheet<Sheets.GatheringPoint>())
        {
            if (row.GatheringPointBase.RowId == 0) continue;
            Add(pointsByBase, row.GatheringPointBase.RowId, row.RowId);
        }

        return new GatheringIndex
        {
            ByItem = byItem.ToFrozenDictionary(k => k.Key, v => v.Value.Distinct().ToArray()),
            BasesByGatheringItem = basesByItem.ToFrozenDictionary(k => k.Key, v => v.Value.Distinct().ToArray()),
            PointsByBase = pointsByBase.ToFrozenDictionary(k => k.Key, v => v.Value.ToArray()),
        };
    }

    // ------------------------------------------------------------------ shops & npcs

    private readonly Cached<FrozenDictionary<uint, uint[]>> gilShops;

    /// <summary>Item id → GilShop ids selling it.</summary>
    public FrozenDictionary<uint, uint[]> GilShopsByItem => gilShops.Value;

    private FrozenDictionary<uint, uint[]> BuildGilShops()
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var shop in SubrowSheet<Sheets.GilShopItem>())
        {
            foreach (var entry in shop)
            {
                if (entry.Item.RowId != 0) Add(map, entry.Item.RowId, entry.RowId);
            }
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.Distinct().ToArray());
    }

    private readonly Cached<FrozenDictionary<uint, uint[]>> specialShops;

    /// <summary>Item id → SpecialShop ids that hand it out.</summary>
    public FrozenDictionary<uint, uint[]> SpecialShopsByItem => specialShops.Value;

    private FrozenDictionary<uint, uint[]> BuildSpecialShops()
    {
        var map = new Dictionary<uint, List<uint>>();
        foreach (var shop in Sheet<Sheets.SpecialShop>())
        {
            foreach (var entry in shop.Item)
            {
                foreach (var receive in entry.ReceiveItems)
                {
                    if (receive.Item.RowId != 0) Add(map, receive.Item.RowId, shop.RowId);
                }
            }
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.Distinct().ToArray());
    }

    private readonly Cached<FrozenDictionary<uint, uint[]>> shopNpcs;

    /// <summary>Shop id (GilShop/SpecialShop/...) → ENpc ids that open it directly, via TopicSelect or via PreHandler.</summary>
    public FrozenDictionary<uint, uint[]> NpcsByShop => shopNpcs.Value;

    private FrozenDictionary<uint, uint[]> BuildShopNpcs()
    {
        var map = new Dictionary<uint, List<uint>>();
        var topicSelect = Sheet<Sheets.TopicSelect>();
        var preHandler = Sheet<Sheets.PreHandler>();
        foreach (var npc in Sheet<Sheets.ENpcBase>())
        {
            foreach (var data in npc.ENpcData)
            {
                var id = data.RowId;
                if (id == 0) continue;
                if (topicSelect.TryGetRow(id, out var topic))
                {
                    foreach (var shop in topic.Shop)
                    {
                        if (shop.RowId == 0) continue;
                        if (preHandler.TryGetRow(shop.RowId, out var nested)) Add(map, nested.Target.RowId, npc.RowId);
                        else Add(map, shop.RowId, npc.RowId);
                    }
                }
                else if (preHandler.TryGetRow(id, out var handler))
                {
                    if (handler.Target.RowId != 0) Add(map, handler.Target.RowId, npc.RowId);
                }
                else
                {
                    Add(map, id, npc.RowId);
                }
            }
        }

        return map.ToFrozenDictionary(k => k.Key, v => v.Value.Distinct().ToArray());
    }

    private readonly Cached<FrozenDictionary<uint, uint>> npcLevels;

    private FrozenDictionary<uint, uint> BuildNpcLevels()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var level in Sheet<Sheets.Level>())
        {
            if (level.Type != 8) continue;
            var npc = level.Object.RowId;
            if (npc != 0) map.TryAdd(npc, level.RowId);
        }

        return map.ToFrozenDictionary();
    }

    public string NpcName(uint npcId) =>
        Row<Sheets.ENpcResident>(npcId) is { } row && SheetJson.Text(row.Singular) is { Length: > 0 } n ? Capitalize(n) : $"NPC #{npcId}";

    /// <summary>First placement of an ENpc in the Level sheet, as zone + map coordinates.</summary>
    public MapLocation? NpcLocation(uint npcId) =>
        npcLevels.Value.TryGetValue(npcId, out var levelId) ? Location(levelId) : null;

    /// <summary>Converts a Level row to zone name and in-game map coordinates (the X/Y shown on the map).</summary>
    public MapLocation? Location(uint levelId)
    {
        if (levelId == 0 || Row<Sheets.Level>(levelId) is not { } level) return null;
        var territory = level.Territory.ValueNullable;
        var map = level.Map.ValueNullable ?? territory?.Map.ValueNullable;
        double? x = null, y = null;
        if (map is { } m)
        {
            x = ToMapCoordinate(level.X, m.SizeFactor, m.OffsetX);
            y = ToMapCoordinate(level.Z, m.SizeFactor, m.OffsetY);
        }

        return new MapLocation(
            level.Territory.RowId,
            territory is { } t ? NullIfEmpty(SheetJson.Text(t.PlaceName.ValueNullable?.Name ?? default)) : null,
            map?.RowId ?? 0,
            x,
            y,
            territory is { } t2 ? NullIfEmpty(SheetJson.Text(t2.PlaceNameRegion.ValueNullable?.Name ?? default)) : null);
    }

    /// <summary>World X/Z → in-game map coordinate (Map.SizeFactor / OffsetX|OffsetY), one decimal.</summary>
    public static double ToMapCoordinate(float world, ushort sizeFactor, short offset)
    {
        var c = (sizeFactor == 0 ? 100 : sizeFactor) / 100.0;
        var value = 41.0 / c * (((world + offset) * c + 1024.0) / 2048.0) + 1.0;
        return Math.Round(value, 1);
    }

    public string? TerritoryName(uint territoryId) =>
        Row<Sheets.TerritoryType>(territoryId) is { } t ? NullIfEmpty(SheetJson.Text(t.PlaceName.ValueNullable?.Name ?? default)) : null;

    // ------------------------------------------------------------------ actions

    internal sealed record ActionEntry(uint Id, string Name, string Lower, uint ClassJob, uint ClassJobCategory, int Level, bool IsPvp, bool IsRole, uint Category);

    private readonly Cached<ActionEntry[]> actions;

    public IReadOnlyList<ActionEntry> Actions => actions.Value;

    private ActionEntry[] BuildActions()
    {
        var list = new List<ActionEntry>(8000);
        foreach (var row in Sheet<Sheets.Action>())
        {
            var name = SheetJson.Text(row.Name);
            if (name.Length == 0) continue;
            // NPC/boss actions share names with player ones; keep actions players can actually learn.
            if (!row.IsPlayerAction && !(row.ClassJobCategory.RowId != 0 && row.ClassJobLevel > 0)) continue;
            var classJob = row.ClassJob.RowId == uint.MaxValue ? 0 : row.ClassJob.RowId;
            list.Add(new ActionEntry(row.RowId, name, name.ToLowerInvariant(), classJob, row.ClassJobCategory.RowId,
                row.ClassJobLevel, row.IsPvP, row.IsRoleAction, row.ActionCategory.RowId));
        }

        return list.ToArray();
    }

    // ------------------------------------------------------------------ quests

    internal sealed record QuestEntry(uint Id, string Name, string Lower, int Level, uint Genre, uint Expansion);

    private readonly Cached<QuestEntry[]> quests;

    public IReadOnlyList<QuestEntry> Quests => quests.Value;

    private QuestEntry[] BuildQuests()
    {
        var list = new List<QuestEntry>(6000);
        foreach (var row in Sheet<Sheets.Quest>())
        {
            var name = SheetJson.Text(row.Name);
            if (name.Length == 0) continue;
            list.Add(new QuestEntry(row.RowId, name, name.ToLowerInvariant(), QuestLevel(row), row.JournalGenre.RowId, row.Expansion.RowId));
        }

        return list.ToArray();
    }

    public static int QuestLevel(Sheets.Quest row) => row.ClassJobLevel.Count > 0 ? row.ClassJobLevel[0] : 0;

    public string? QuestName(uint questId) =>
        Row<Sheets.Quest>(questId) is { } q ? NullIfEmpty(SheetJson.Text(q.Name)) : null;

    private readonly Cached<FrozenDictionary<uint, uint>> instanceUnlockQuests;

    /// <summary>InstanceContent id → first quest whose reward unlocks it.</summary>
    public FrozenDictionary<uint, uint> InstanceUnlockQuests => instanceUnlockQuests.Value;

    private FrozenDictionary<uint, uint> BuildInstanceUnlockQuests()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var row in Sheet<Sheets.Quest>())
        {
            var id = row.InstanceContentUnlock.RowId;
            if (id != 0) map.TryAdd(id, row.RowId);
        }

        return map.ToFrozenDictionary();
    }

    // ------------------------------------------------------------------ duties

    internal sealed record DutyEntry(uint Id, string Name, string Lower, uint ContentType, int Level, int ItemLevel);

    private readonly Cached<DutyEntry[]> duties;

    public IReadOnlyList<DutyEntry> Duties => duties.Value;

    private DutyEntry[] BuildDuties()
    {
        var list = new List<DutyEntry>(1200);
        foreach (var row in Sheet<Sheets.ContentFinderCondition>())
        {
            var name = SheetJson.Text(row.Name);
            if (name.Length == 0) continue;
            name = Capitalize(name);
            list.Add(new DutyEntry(row.RowId, name, name.ToLowerInvariant(), row.ContentType.RowId, row.ClassJobLevelRequired, row.ItemLevelRequired));
        }

        return list.ToArray();
    }

    // ------------------------------------------------------------------ helpers

    public static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void Add<TValue>(Dictionary<uint, List<TValue>> map, uint key, TValue value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<TValue>(2);
        list.Add(value);
    }
}

/// <summary>Thread-safe lazy value that, unlike <see cref="Lazy{T}"/>, does not cache a failed build.</summary>
internal sealed class Cached<T>(Func<T> factory)
    where T : class
{
    private readonly object gate = new();
    private T? value;

    public bool IsValueCreated => Volatile.Read(ref value) != null;

    public T Value
    {
        get
        {
            var current = Volatile.Read(ref value);
            if (current != null) return current;
            lock (gate)
            {
                return value ??= factory();
            }
        }
    }
}

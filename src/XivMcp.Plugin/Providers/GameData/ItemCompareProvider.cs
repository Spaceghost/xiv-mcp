using System.Globalization;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Side-by-side comparison of equipment from game data: item level, equip level, the substats each piece
/// carries and where they differ. Pure sheet reads, so it needs no character and no login.
/// </summary>
[McpProvider("gamedata")]
public sealed class ItemCompareProvider
{
    private const int MaxItems = 6;

    private readonly GameDataIndex index;

    public ItemCompareProvider(IGameDataSource data) => index = GameDataIndex.For(data);

    public sealed record CompareItemDto(
        uint ItemId,
        string? Name,
        int ItemLevel,
        int EquipLevel,
        string? Category,
        string? Jobs,
        int MateriaSlots,
        bool CanBeHq,
        int? PhysicalDamage,
        int? MagicDamage,
        int? PhysicalDefense,
        int? MagicDefense,
        Dictionary<string, int> Stats);

    public sealed record StatDiffDto(string Stat, Dictionary<string, int> Values, int Spread, string Best);

    public sealed record CompareResult(
        List<CompareItemDto> Items,
        List<StatDiffDto> Differences,
        List<string> SameStats,
        string? SlotWarning,
        string Note);

    [McpTool("compare_items",
        Availability = ToolAvailability.Static,
        Sources = ["lumina:Item", "lumina:BaseParam"],
        Title = "Compare equipment",
        GameThread = false,
        RequiresLogin = false,
        Description =
            "Compares 2-6 pieces of equipment from game data side by side: per item the itemLevel, equipLevel, category, jobs, " +
            "materiaSlots, canBeHq, weapon damage / defence and every substat (critical hit, determination, direct hit rate, skill and " +
            "spell speed, tenacity, piety, craftsmanship, control, CP, gathering, perception, GP), then differences — each stat where " +
            "the items disagree, with every item's value, the spread and which item wins — and sameStats. " +
            "hq=true uses the high-quality values where an item has them. " +
            "slotWarning says so when the items do not all go in the same slot, which usually means the comparison is not meaningful. " +
            "This is game data only: it does not know the character's melds or what is equipped — pair it with get_equipment and " +
            "get_attributes for that.")]
    public CompareResult CompareItems(
        [McpParam("Item ids to compare (2-6). Use search_items or get_item to find them.")] uint[] itemIds,
        [McpParam("Use the high-quality stat values where the item has them.")] bool hq = false)
    {
        if (itemIds == null || itemIds.Length < 2)
        {
            throw new McpToolException("Give at least two item ids to compare.");
        }

        if (itemIds.Length > MaxItems)
        {
            throw new McpToolException($"At most {MaxItems} items per call.");
        }

        var items = new List<CompareItemDto>();
        var slots = new HashSet<uint>();
        foreach (var itemId in itemIds.Distinct())
        {
            if (index.Row<Sheets.Item>(itemId) is not { } row)
            {
                throw new McpToolException($"Item {itemId} not found.");
            }

            slots.Add(row.EquipSlotCategory.RowId);
            items.Add(new CompareItemDto(
                itemId,
                GameDataIndex.NullIfEmpty(SheetJson.Text(row.Name)),
                (int)row.LevelItem.RowId,
                row.LevelEquip,
                GameDataIndex.NullIfEmpty(SheetJson.Text(row.ItemUICategory.ValueNullable?.Name ?? default)),
                index.ClassJobCategoryName(row.ClassJobCategory.RowId),
                row.MateriaSlotCount,
                row.CanBeHq,
                row.DamagePhys > 0 ? row.DamagePhys : null,
                row.DamageMag > 0 ? row.DamageMag : null,
                row.DefensePhys > 0 ? row.DefensePhys : null,
                row.DefenseMag > 0 ? row.DefenseMag : null,
                Stats(row, hq)));
        }

        if (items.Count < 2)
        {
            throw new McpToolException("Give at least two different item ids to compare.");
        }

        var names = items.ToDictionary(i => i.ItemId, i => i.Name ?? i.ItemId.ToString(CultureInfo.InvariantCulture));
        var allStats = items.SelectMany(i => i.Stats.Keys).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var differences = new List<StatDiffDto>();
        var same = new List<string>();

        foreach (var stat in allStats)
        {
            var values = items.ToDictionary(i => names[i.ItemId], i => i.Stats.GetValueOrDefault(stat));
            var min = values.Values.Min();
            var max = values.Values.Max();
            if (min == max)
            {
                same.Add(stat);
                continue;
            }

            differences.Add(new StatDiffDto(
                stat,
                values,
                max - min,
                values.First(v => v.Value == max).Key));
        }

        // Item level is the headline comparison, so surface it like a stat.
        if (items.Select(i => i.ItemLevel).Distinct().Count() > 1)
        {
            var levels = items.ToDictionary(i => names[i.ItemId], i => i.ItemLevel);
            differences.Insert(0, new StatDiffDto(
                "Item Level",
                levels,
                levels.Values.Max() - levels.Values.Min(),
                levels.First(v => v.Value == levels.Values.Max()).Key));
        }

        return new CompareResult(
            items,
            differences,
            same,
            slots.Count > 1 ? "These items do not all use the same equipment slot, so the comparison may not be meaningful." : null,
            hq
                ? "Stat values are the high-quality ones where the item has them."
                : "Stat values are the normal-quality ones; pass hq=true for HQ values.");
    }

    private Dictionary<string, int> Stats(Sheets.Item row, bool hq)
    {
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < row.BaseParam.Count; i++)
        {
            var param = row.BaseParam[i];
            if (param.RowId == 0)
            {
                continue;
            }

            var name = GameDataIndex.NullIfEmpty(SheetJson.Text(param.ValueNullable?.Name ?? default));
            if (name == null)
            {
                continue;
            }

            var value = (int)row.BaseParamValue[i];
            if (value != 0)
            {
                stats[name] = stats.GetValueOrDefault(name) + value;
            }
        }

        if (!hq)
        {
            return stats;
        }

        for (var i = 0; i < row.BaseParamSpecial.Count; i++)
        {
            var param = row.BaseParamSpecial[i];
            if (param.RowId == 0)
            {
                continue;
            }

            var name = GameDataIndex.NullIfEmpty(SheetJson.Text(param.ValueNullable?.Name ?? default));
            if (name == null)
            {
                continue;
            }

            var bonus = (int)row.BaseParamValueSpecial[i];
            if (bonus != 0)
            {
                stats[name] = stats.GetValueOrDefault(name) + bonus;
            }
        }

        return stats;
    }
}

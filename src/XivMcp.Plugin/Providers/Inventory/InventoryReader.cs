using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Inventory;

/// <summary>
/// Framework-thread helpers that read InventoryManager containers and copy slots into DTOs.
/// Never retains pointers past a call.
/// </summary>
internal static unsafe class InventoryReader
{
    internal sealed record ContainerGroup(string Key, InventoryType[] Types, string UnloadedNote);

    private const string RetainerNote =
        "not loaded: open a retainer at a summoning bell (only the most recently opened retainer's containers are in memory)";

    public static readonly ContainerGroup[] Groups =
    [
        new("bags", [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4],
            "not loaded yet (character still loading)"),
        new("equipped", [InventoryType.EquippedItems], "not loaded yet (character still loading)"),
        new("armory",
        [
            InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
            InventoryType.ArmoryHands, InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
            InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
            InventoryType.ArmorySoulCrystal,
        ], "not loaded yet (character still loading)"),
        new("crystals", [InventoryType.Crystals], "not loaded yet (character still loading)"),
        new("currency", [InventoryType.Currency], "not loaded yet (character still loading)"),
        new("keyItems", [InventoryType.KeyItems], "not loaded yet (character still loading)"),
        new("saddlebag", [InventoryType.SaddleBag1, InventoryType.SaddleBag2],
            "not loaded: open the chocobo saddlebag once this session"),
        new("premiumSaddlebag", [InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2],
            "not loaded: requires the Companion premium saddlebag and opening the saddlebag once this session"),
        new("retainer",
        [
            InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
            InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
        ], RetainerNote),
        new("retainerEquipped", [InventoryType.RetainerEquippedItems], RetainerNote),
        new("retainerMarket", [InventoryType.RetainerMarket], RetainerNote),
    ];

    public static readonly string[] GroupKeys = Groups.Select(g => g.Key).ToArray();

    public static bool IsRetainerGroup(ContainerGroup group) => group.Key.StartsWith("retainer", StringComparison.Ordinal);

    /// <summary>Resolves group keys (case-insensitive); "all" or empty selects every group.</summary>
    public static List<ContainerGroup> Resolve(IEnumerable<string>? keys)
    {
        var list = keys?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList() ?? [];
        if (list.Count == 0 || list.Any(k => k.Equals("all", StringComparison.OrdinalIgnoreCase))) return Groups.ToList();
        var result = new List<ContainerGroup>();
        foreach (var key in list)
        {
            var group = Groups.FirstOrDefault(g => g.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new XivMcp.Core.McpToolException(
                            $"Unknown container \"{key}\". Use: {string.Join(", ", GroupKeys)} or all.");
            if (!result.Contains(group)) result.Add(group);
        }

        return result;
    }

    public static InventoryContainer* Container(InventoryType type)
    {
        var manager = InventoryManager.Instance();
        return manager == null ? null : manager->GetInventoryContainer(type);
    }

    public static bool IsLoaded(InventoryContainer* container) => container != null && container->IsLoaded && container->Size > 0;

    /// <summary>Name of the retainer whose containers are currently in memory, if known.</summary>
    public static string? ActiveRetainerName()
    {
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady) return null;
        var active = manager->GetActiveRetainer();
        if (active == null || active->RetainerId == 0) return null;
        var name = active->NameString;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static string ItemName(GameDataIndex index, uint itemId, InventoryType container)
    {
        if (container == InventoryType.KeyItems)
        {
            return index.Row<Sheets.EventItem>(itemId) is { } ev && SheetJson.Text(ev.Name) is { Length: > 0 } evName
                ? evName
                : index.Row<Sheets.EventItem>(itemId) is { } ev2 && SheetJson.Text(ev2.Singular) is { Length: > 0 } single
                    ? single
                    : $"Key item #{itemId}";
        }

        return index.ItemName(itemId);
    }

    /// <summary>Copies one non-empty slot into a DTO. Gear-only fields are omitted for non-equipment.</summary>
    public static InventorySlot ToSlot(GameDataIndex index, InventoryItem* item, InventoryType container, string group, int slot)
    {
        var itemId = item->ItemId;
        var name = ItemName(index, itemId, container);
        var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var collectable = (item->Flags & InventoryItem.ItemFlags.Collectable) != 0;
        Sheets.Item? row = container == InventoryType.KeyItems ? null : index.Row<Sheets.Item>(itemId);
        var isGear = row is { } r && r.EquipSlotCategory.RowId != 0;

        ulong? price = null;
        if (container == InventoryType.RetainerMarket)
        {
            var manager = InventoryManager.Instance();
            if (manager != null && slot >= 0 && slot < manager->RetainerMarketPrices.Length) price = manager->RetainerMarketPrices[slot];
        }

        return new InventorySlot(
            ContainerName(container),
            group,
            slot,
            itemId,
            name,
            item->Quantity,
            hq ? true : null,
            collectable ? true : null,
            collectable ? item->SpiritbondOrCollectability : null,
            isGear ? Math.Round(item->Condition / 300.0, 1) : null,
            isGear && !collectable ? Math.Round(item->SpiritbondOrCollectability / 100.0, 2) : null,
            isGear ? Materia(index, item) : null,
            isGear && item->GlamourId != 0 ? item->GlamourId : null,
            isGear && item->GlamourId != 0 ? index.ItemName(item->GlamourId) : null,
            isGear ? Dyes(index, item) : null,
            price,
            isGear ? (int)row!.Value.LevelItem.RowId : null);
    }

    public static List<string>? Materia(GameDataIndex index, InventoryItem* item)
    {
        List<string>? list = null;
        var ids = item->Materia;
        var grades = item->MateriaGrades;
        for (var i = 0; i < ids.Length && i < grades.Length; i++)
        {
            var materiaId = ids[i];
            if (materiaId == 0) continue;
            var grade = grades[i];
            string text;
            if (index.Row<Sheets.Materia>(materiaId) is { } materia && grade < materia.Item.Count && materia.Item[grade].RowId != 0)
            {
                text = index.ItemName(materia.Item[grade].RowId);
                var value = grade < materia.Value.Count ? materia.Value[grade] : (short)0;
                if (materia.BaseParam.RowId != 0 && value != 0)
                {
                    var stat = SheetJson.Text(materia.BaseParam.ValueNullable?.Name ?? default);
                    if (stat.Length > 0) text += $" (+{value} {stat})";
                }
            }
            else
            {
                text = $"Materia #{materiaId} grade {grade + 1}";
            }

            (list ??= []).Add(text);
        }

        return list;
    }

    public static List<string>? Dyes(GameDataIndex index, InventoryItem* item)
    {
        List<string>? list = null;
        var stains = item->Stains;
        for (var i = 0; i < stains.Length; i++)
        {
            if (stains[i] == 0) continue;
            var name = index.Row<Sheets.Stain>(stains[i]) is { } stain && SheetJson.Text(stain.Name) is { Length: > 0 } n
                ? GameDataIndex.Capitalize(n)
                : $"Dye #{stains[i]}";
            (list ??= []).Add(name);
        }

        return list;
    }

    public static string ContainerName(InventoryType type) => type switch
    {
        InventoryType.ArmoryFeets => "ArmoryFeet",
        _ => type.ToString(),
    };
}

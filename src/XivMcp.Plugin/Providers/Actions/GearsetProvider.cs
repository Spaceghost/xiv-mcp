using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>Gear set listing and switching.</summary>
[McpProvider("actions")]
public sealed unsafe class GearsetProvider
{
    private const int MaxGearsets = 100;

    private readonly IDataManager dataManager;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;

    public GearsetProvider(IDataManager dataManager, ICondition condition, IObjectTable objectTable, IClientState clientState)
    {
        this.dataManager = dataManager;
        this.condition = condition;
        this.objectTable = objectTable;
        this.clientState = clientState;
    }

    [McpTool("list_gearsets",
        Title = "List gear sets",
        Description =
            "Lists the character's saved gear sets. Each entry: id (the gear set number shown in the Gear Set list and used by /gearset change and equip_gearset, 1-100), name, classJobId, classJob (abbreviation, e.g. WHM) and classJobName, itemLevel (average item level saved with the set), glamourPlate (linked glamour plate number, omitted if none), mainHandMissing (the set's main-hand weapon is missing, so it cannot be fully equipped), isCurrent (the set currently equipped). " +
            "Use before equip_gearset to pick the right set.",
        Permission = ToolPermission.Read)]
    public GearsetListResult ListGearsets()
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            throw new McpToolException("Gear set data is not loaded yet.");

        var list = new List<GearsetInfo>();
        var entries = module->Entries;
        for (var i = 0; i < Math.Min(entries.Length, MaxGearsets); i++)
        {
            ref var entry = ref entries[i];
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
                continue;
            list.Add(Describe(module, i, ref entry));
        }

        // The game stores 0xFF (255) when no gear set is equipped, so only report indexes of real sets.
        var current = module->CurrentGearsetIndex;
        return new GearsetListResult(list, current >= 0 && current < MaxGearsets && module->IsValidGearset(current) ? current + 1 : null);
    }

    [McpTool("equip_gearset",
        Title = "Equip gear set",
        Description =
            "Equips one of the character's saved gear sets (which also changes class/job when the set belongs to another job), exactly like /gearset change. " +
            "Select with id (the number from list_gearsets, 1-100) or name (case-insensitive; exact match preferred, otherwise a unique substring). " +
            "Refused while in combat, casting, crafting, gathering, in a cutscene/event, changing zones, or when switching to a different job inside a duty. " +
            "The swap is applied by the game over the next moments; call list_gearsets to confirm isCurrent. Returns the chosen set and the game's result code.",
        Permission = ToolPermission.Action)]
    public EquipGearsetResult EquipGearset(
        [McpParam("Gear set number (1-100) as shown in list_gearsets.", Minimum = 1, Maximum = 100)] int? id = null,
        [McpParam("Gear set name.")] string? name = null)
    {
        if (id.HasValue == !string.IsNullOrWhiteSpace(name))
            throw new McpToolException("Pass exactly one of id or name.");

        var module = RaptureGearsetModule.Instance();
        if (module == null)
            throw new McpToolException("Gear set data is not loaded yet.");

        var index = id.HasValue ? id.Value - 1 : FindByName(module, name!.Trim());
        if (index < 0 || index >= MaxGearsets || !module->IsValidGearset(index))
            throw new McpToolException($"Gear set {index + 1} does not exist. Use list_gearsets to see saved sets.");

        ref var entry = ref module->Entries[index];
        var info = Describe(module, index, ref entry);

        PlayerGuards.EnsureNotBusy(condition, "change gear sets");
        if (clientState.IsPvP)
            throw new McpToolException("Gear sets cannot be changed from here while in PvP.");

        var player = objectTable.LocalPlayer;
        if (PlayerGuards.IsBoundByDuty(condition) && player != null && player.ClassJob.RowId != entry.ClassJob)
            throw new McpToolException($"Cannot switch to a different job ({info.ClassJob}) while bound by duty. Only gear sets for the current job can be equipped inside a duty.");

        if (module->CurrentGearsetIndex == index)
            return new EquipGearsetResult(info, 0, "Already equipped; nothing changed.");

        var result = module->EquipGearset(index);
        if (result < 0)
            throw new McpToolException($"The game refused to equip gear set {index + 1} ({info.Name}), result code {result}.");

        return new EquipGearsetResult(info, result, "Requested; the game applies it asynchronously. Check list_gearsets for isCurrent.");
    }

    private static int FindByName(RaptureGearsetModule* module, string name)
    {
        var entries = module->Entries;
        int exact = -1, partial = -1, partialCount = 0;
        for (var i = 0; i < Math.Min(entries.Length, MaxGearsets); i++)
        {
            ref var entry = ref entries[i];
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
                continue;
            var entryName = entry.NameString;
            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
            {
                if (exact < 0)
                    exact = i;
            }
            else if (entryName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                partialCount++;
                partial = i;
            }
        }

        if (exact >= 0)
            return exact;
        if (partialCount == 1)
            return partial;
        if (partialCount > 1)
            throw new McpToolException($"Gear set name '{name}' matches {partialCount} sets; use a more specific name or the id from list_gearsets.");
        throw new McpToolException($"No gear set named '{name}'. Use list_gearsets to see saved sets.");
    }

    private GearsetInfo Describe(RaptureGearsetModule* module, int index, ref RaptureGearsetModule.GearsetEntry entry)
    {
        string? abbreviation = null, jobName = null;
        if (dataManager.GetExcelSheet<ClassJob>().TryGetRow(entry.ClassJob, out var job))
        {
            abbreviation = job.Abbreviation.ExtractText();
            jobName = job.Name.ExtractText();
        }

        return new GearsetInfo(
            index + 1,
            entry.NameString,
            entry.ClassJob,
            string.IsNullOrEmpty(abbreviation) ? null : abbreviation,
            string.IsNullOrEmpty(jobName) ? null : jobName,
            entry.ItemLevel,
            entry.GlamourSetLink != 0 ? entry.GlamourSetLink : null,
            (entry.Flags & RaptureGearsetModule.GearsetFlag.MainHandMissing) != 0 ? true : null,
            module->CurrentGearsetIndex == index);
    }

    public sealed record GearsetInfo(
        int Id,
        string Name,
        int ClassJobId,
        string? ClassJob,
        string? ClassJobName,
        int ItemLevel,
        int? GlamourPlate,
        bool? MainHandMissing,
        bool IsCurrent);

    public sealed record GearsetListResult(IReadOnlyList<GearsetInfo> Gearsets, int? CurrentId);

    public sealed record EquipGearsetResult(GearsetInfo Gearset, int ResultCode, string Note);
}

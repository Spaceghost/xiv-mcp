using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>Teleports to an attuned aetheryte through the game's own Teleport action.</summary>
[McpProvider("actions")]
public sealed unsafe class TeleportProvider
{
    private readonly IAetheryteList aetherytes;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;

    public TeleportProvider(IAetheryteList aetherytes, ICondition condition, IClientState clientState, IDataManager dataManager)
    {
        this.aetherytes = aetherytes;
        this.condition = condition;
        this.clientState = clientState;
        this.dataManager = dataManager;
    }

    [McpTool("teleport",
        Title = "Teleport to aetheryte",
        Description =
            "Starts the Teleport spell to one of the character's attuned aetherytes (or free-company/private estate and apartment entries), exactly like choosing it in the Teleport window. Costs gil (reported as gilCost) and takes a ~5 second cast that can be interrupted. " +
            "Select with aetheryteId (Aetheryte row id) or name (aetheryte or zone name, case-insensitive; exact match preferred, otherwise a unique substring, e.g. \"Limsa Lominsa Lower Decks\", \"Tuliyollal\", \"Kugane\"). Only attuned destinations work. " +
            "Refused in combat, in a duty, while casting, flying or diving, in cutscenes/events, crafting, gathering, while dead, changing zones or in PvP. " +
            "Returns the destination, its territory, gilCost and started=true when the cast began; the arrival itself happens after the cast and loading screen.",
        Permission = ToolPermission.Action, Idempotent = false)]
    public TeleportResult Teleport(
        [McpParam("Aetheryte row id.")] uint? aetheryteId = null,
        [McpParam("Aetheryte or zone name.")] string? name = null)
    {
        if (aetheryteId.HasValue == !string.IsNullOrWhiteSpace(name))
            throw new McpToolException("Pass exactly one of aetheryteId or name.");

        PlayerGuards.EnsureNotBusy(condition, "teleport");
        if (PlayerGuards.IsBoundByDuty(condition))
            throw new McpToolException("Cannot teleport while bound by duty. Leave the duty first.");
        if (condition[ConditionFlag.InFlight] || condition[ConditionFlag.Diving])
            throw new McpToolException("Cannot teleport while flying or diving. Land first.");
        if (clientState.IsPvP)
            throw new McpToolException("Cannot teleport while in PvP.");

        var candidates = new List<Destination>();
        foreach (var entry in aetherytes)
        {
            if (entry != null)
                candidates.Add(Describe(entry));
        }

        if (candidates.Count == 0)
            throw new McpToolException("No teleport destinations are available (the aetheryte list is empty).");

        Destination target;
        if (aetheryteId is { } id)
        {
            target = candidates.Where(c => c.AetheryteId == id).OrderBy(c => c.IsHousing ? 1 : 0).FirstOrDefault()
                     ?? throw new McpToolException($"Aetheryte {id} is not attuned or not a valid teleport destination.");
        }
        else
        {
            target = FindByName(candidates, name!.Trim());
        }

        var telepo = Telepo.Instance();
        if (telepo == null)
            throw new McpToolException("The teleport system is not available right now.");

        if (!telepo->Teleport(target.AetheryteId, target.SubIndex))
            throw new McpToolException($"The game refused to teleport to {target.Name} (not enough gil, or teleporting is not possible right now).");

        return new TeleportResult(target.AetheryteId, target.Name, target.TerritoryId, target.TerritoryName, target.GilCost, target.IsFavourite ? true : null, true);
    }

    private static Destination FindByName(List<Destination> candidates, string query)
    {
        var exact = candidates.Where(c => string.Equals(c.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0)
            exact = candidates.Where(c => !c.IsHousing && string.Equals(c.TerritoryName, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
            return exact.OrderBy(c => c.IsHousing ? 1 : 0).ThenBy(c => c.GilCost).First();

        var partial = candidates
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (!c.IsHousing && c.TerritoryName != null && c.TerritoryName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (partial.Count == 1)
            return partial[0];
        if (partial.Count > 1)
        {
            var names = string.Join(", ", partial.Select(p => p.Name).Distinct().Take(10));
            throw new McpToolException($"'{query}' matches {partial.Count} destinations: {names}{(partial.Count > 10 ? ", ..." : "")}. Use the full name or aetheryteId.");
        }

        throw new McpToolException($"No attuned teleport destination matches '{query}'.");
    }

    private Destination Describe(IAetheryteEntry entry)
    {
        var row = entry.AetheryteData.ValueNullable;
        var aetheryteName = row?.PlaceName.ValueNullable?.Name.ExtractText();
        string? territoryName = null;
        if (dataManager.GetExcelSheet<TerritoryType>().TryGetRow(entry.TerritoryId, out var territory))
            territoryName = territory.PlaceName.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(territoryName))
            territoryName = null;

        var isHousing = entry.Ward != 0 || entry.Plot != 0 || entry.IsApartment || entry.IsSharedHouse;
        string name;
        if (entry.IsApartment)
            name = $"Apartment ({territoryName ?? "residential district"}, ward {entry.Ward})";
        else if (entry.IsSharedHouse)
            name = $"Shared Estate ({territoryName ?? "residential district"}, ward {entry.Ward}, plot {entry.Plot})";
        else if (isHousing)
            name = $"{(string.IsNullOrEmpty(aetheryteName) ? "Estate Hall" : aetheryteName)} ({territoryName ?? "residential district"}, ward {entry.Ward}, plot {entry.Plot})";
        else
            name = string.IsNullOrEmpty(aetheryteName) ? territoryName ?? $"Aetheryte {entry.AetheryteId}" : aetheryteName;

        return new Destination(entry.AetheryteId, entry.SubIndex, name, entry.TerritoryId, territoryName, entry.GilCost, entry.IsFavourite, isHousing);
    }

    private sealed record Destination(uint AetheryteId, byte SubIndex, string Name, uint TerritoryId, string? TerritoryName, uint GilCost, bool IsFavourite, bool IsHousing);

    public sealed record TeleportResult(uint AetheryteId, string Destination, uint TerritoryId, string? TerritoryName, uint GilCost, bool? Favourite, bool Started);
}

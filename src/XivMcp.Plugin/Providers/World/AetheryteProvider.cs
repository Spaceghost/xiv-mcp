using Dalamud.Plugin.Services;
using XivMcp.Core;
using CsTelepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo;
using LAetheryte = Lumina.Excel.Sheets.Aetheryte;
using LTerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace XivMcp.Plugin.Providers.World;

[McpProvider("world")]
public sealed class AetheryteProvider
{
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDataManager data;

    public AetheryteProvider(IObjectTable objects, IClientState clientState, IPlayerState playerState, IDataManager data)
    {
        this.objects = objects;
        this.clientState = clientState;
        this.playerState = playerState;
        this.data = data;
    }

    public sealed record AetheryteDto(
        uint AetheryteId,
        int SubIndex,
        string? Name,
        uint TerritoryId,
        string? Zone,
        string? Region,
        uint GilCost,
        bool IsFavourite,
        bool IsFreeDestination,
        bool IsHomePoint,
        bool IsInCurrentZone,
        bool IsHousing,
        bool IsSharedHouse,
        bool IsApartment,
        int? Ward,
        int? Plot,
        bool Unlocked);

    public sealed record AetherytesDto(int Total, int Offset, int Returned, bool Truncated, List<AetheryteDto> Aetherytes);

    [McpTool("list_aetherytes",
        Sources = ["dalamud:IAetheryteList", "lumina:Aetheryte"],
        Title = "List teleport destinations",
        Description = "The player's teleport list (the in-game Teleport window): every attuned aetheryte plus housing destinations " +
                      "(own/FC house, shared estates, apartments). Each entry: aetheryteId, subIndex, name, territoryId, zone, region, gilCost " +
                      "(the current price, which already reflects favourites/free destination), isFavourite, isFreeDestination, isHomePoint, " +
                      "isInCurrentZone, housing flags with ward/plot, unlocked (always true: only attuned destinations appear). Filter with " +
                      "nameContains (matches aetheryte, zone or region name). Use to answer 'can I teleport to X' / 'what does it cost' " +
                      "before suggesting a teleport; aethernet shards inside cities are not teleport destinations and are not listed.")]
    public unsafe AetherytesDto ListAetherytes(
        [McpParam("Case-insensitive substring matched against the aetheryte, zone or region name.")] string? nameContains = null,
        [McpParam("Maximum entries to return (1-500). Default 50.", Minimum = 1, Maximum = 500)] int limit = 50,
        [McpParam("Entries to skip, for paging (>= 0).", Minimum = 0)] int offset = 0)
    {
        if (objects.LocalPlayer == null)
        {
            throw new McpToolException("No character is logged in.");
        }

        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var telepo = CsTelepo.Instance();
        if (telepo == null)
        {
            throw new McpToolException("The teleport list is not available right now.");
        }

        telepo->UpdateAetheryteList();
        var count = telepo->TeleportList.Count;
        var aetherytes = data.GetExcelSheet<LAetheryte>();
        var territories = data.GetExcelSheet<LTerritoryType>();
        var home = playerState.IsLoaded ? playerState.HomeAetheryte.RowId : 0;
        var currentTerritory = clientState.TerritoryType;
        var all = new List<AetheryteDto>(count);

        for (var i = 0; i < count; i++)
        {
            var info = telepo->TeleportList[i];
            if (info.AetheryteId == 0)
            {
                continue;
            }

            string? name = null;
            if (aetherytes.TryGetRow(info.AetheryteId, out var row))
            {
                name = Text(row.PlaceName.ValueNullable?.Name) ?? Text(row.AethernetName.ValueNullable?.Name);
            }

            string? zone = null;
            string? region = null;
            if (territories.TryGetRow(info.TerritoryId, out var territory))
            {
                zone = Text(territory.PlaceName.ValueNullable?.Name);
                region = Text(territory.PlaceNameRegion.ValueNullable?.Name);
            }

            var isHousing = info.Plot != 0 || info.Ward != 0 || info.IsSharedHouse || info.IsApartment;
            all.Add(new AetheryteDto(
                info.AetheryteId,
                info.SubIndex,
                name,
                info.TerritoryId,
                zone,
                region,
                info.GilCost,
                info.IsFavourite,
                info.IsFreeAetheryte,
                home != 0 && info.AetheryteId == home && !isHousing,
                info.TerritoryId == currentTerritory,
                isHousing,
                info.IsSharedHouse,
                info.IsApartment,
                isHousing && info.Ward != 0 ? info.Ward : null,
                isHousing && info.Plot != 0 ? info.Plot : null,
                true));
        }

        IEnumerable<AetheryteDto> filtered = all;
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            var needle = nameContains.Trim();
            filtered = all.Where(a =>
                (a.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.Zone?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.Region?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var matches = filtered.ToList();
        var page = matches.Skip(offset).Take(limit).ToList();
        return new AetherytesDto(matches.Count, offset, page.Count, offset + page.Count < matches.Count, page);
    }

    private static string? Text(Lumina.Text.ReadOnly.ReadOnlySeString? value)
    {
        if (value is not { } text)
        {
            return null;
        }

        var extracted = text.ExtractText();
        return string.IsNullOrEmpty(extracted) ? null : extracted;
    }
}

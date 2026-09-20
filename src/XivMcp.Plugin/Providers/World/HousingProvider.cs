using System.Globalization;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using CsHousingManager = FFXIVClientStructs.FFXIV.Client.Game.HousingManager;

namespace XivMcp.Plugin.Providers.World;

/// <summary>Where the player is in the housing system, read from HousingManager on the framework thread.</summary>
[McpProvider("world")]
public sealed unsafe class HousingProvider
{
    private readonly GameDataIndex index;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;

    public HousingProvider(IDataManager data, IClientState clientState, IObjectTable objects)
    {
        index = GameDataIndex.For(data);
        this.clientState = clientState;
        this.objects = objects;
    }

    public sealed record HousingResult(
        bool InHousingArea,
        uint TerritoryId,
        string? Zone,
        int? Ward,
        int? Plot,
        int? Division,
        int? Room,
        string? HouseId,
        bool Inside,
        bool Outside,
        bool InWorkshop,
        bool HasPermissions,
        string? FreeCompanyTag,
        string Note);

    [McpTool("get_housing_info",
        Title = "Get housing location",
        Description =
            "Where the player is in the housing system: inHousingArea, territoryId/zone, ward (1-based), plot (1-based, null in an " +
            "apartment or subdivision entrance), division (1 = main area, 2 = subdivision), room (apartment/FC room number), houseId " +
            "(64-bit, as a string), inside/outside/inWorkshop, hasPermissions (the character may place or move furnishings here) and the " +
            "player's freeCompanyTag. " +
            "Everything is null or false when the character is not in a housing district or estate. " +
            "Use list_aetherytes for the housing destinations the character can teleport to, and get_location for the general position.")]
    public HousingResult GetHousingInfo()
    {
        var manager = CsHousingManager.Instance();
        var territory = clientState.TerritoryType;
        var zone = index.TerritoryName(territory);
        var fcTag = SafeString(() => objects.LocalPlayer?.CompanyTag.TextValue);

        if (manager == null)
        {
            return new HousingResult(false, territory, zone, null, null, null, null, null, false, false, false, false, fcTag,
                "The housing manager is not available right now.");
        }

        var ward = SafeInt(() => manager->GetCurrentWard());
        var plot = SafeInt(() => manager->GetCurrentPlot());
        var division = SafeInt(() => manager->GetCurrentDivision());
        var room = SafeInt(() => manager->GetCurrentRoom());
        var houseId = SafeLong(() => (long)(ulong)manager->GetCurrentHouseId());
        var inside = SafeBool(() => manager->IsInside());
        var outside = SafeBool(() => manager->IsOutside());
        var workshop = SafeBool(() => manager->IsInWorkshop());
        var permissions = SafeBool(() => manager->HasHousePermissions());
        var inHousing = ward is > 0 || plot is > 0 || room is > 0 || inside || workshop;

        return new HousingResult(
            inHousing,
            territory,
            zone,
            ward is > 0 ? ward : null,
            plot is > 0 ? plot : null,
            division is > 0 ? division : null,
            room is > 0 ? room : null,
            houseId is > 0 ? houseId.Value.ToString(CultureInfo.InvariantCulture) : null,
            inside,
            outside,
            workshop,
            permissions,
            fcTag,
            inHousing
                ? "Ward and plot are 1-based, matching what the game shows on the placard and in the housing menus."
                : "The character is not in a housing district or estate right now.");
    }

    private static int? SafeInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static long? SafeLong(Func<long> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeString(Func<string?> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}

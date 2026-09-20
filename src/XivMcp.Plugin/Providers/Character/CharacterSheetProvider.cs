using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Inventory;

namespace XivMcp.Plugin.Providers.Character;

/// <summary>
/// One composite "summarise my character" snapshot. It owns private instances of the existing character providers and calls
/// their tool methods, so game memory is read exactly the way those tools read it; this class touches no game memory itself.
/// </summary>
[McpProvider("character")]
public sealed class CharacterSheetProvider : IDisposable
{
    private readonly PlayerProvider player;
    private readonly AttributesProvider attributes;
    private readonly InventoryProvider inventory;
    private readonly RetainerProvider retainers;

    public CharacterSheetProvider(
        IObjectTable objects,
        IClientState clientState,
        IPlayerState playerState,
        ICondition condition,
        IDataManager data,
        IGameInventory gameInventory)
    {
        player = new PlayerProvider(objects, clientState, playerState, condition, data);
        attributes = new AttributesProvider(data);
        // The inner inventory provider only serves reads here; its change notifications belong to the registered instance.
        inventory = new InventoryProvider(data, playerState, gameInventory, SilentNotifier.Instance);
        retainers = new RetainerProvider(data);
    }

    public void Dispose() => inventory.Dispose();

    [McpTool("get_character_sheet",
        Sources =
        [
            "dalamud:IObjectTable", "dalamud:IClientState", "client:PlayerState", "client:InventoryManager", "client:CurrencyManager",
            "client:RetainerManager", "lumina:Item", "lumina:Materia", "lumina:BaseParam",
        ],
        Title = "Get character sheet",
        Description =
            "One condensed snapshot for 'summarise my character': identity (name, home world and data center, title, grand company and " +
            "rank, free company tag), job (abbreviation, name, role, level, synced level, max HP/MP), attributes (non-zero stats relevant " +
            "to the current job as {name, value}), gear (averageItemLevel computed with the character window's rule, lowest item level " +
            "slot, lowest condition %, melded materia count, and each piece with slot, itemId, name, itemLevel, hq, condition % and materia " +
            "names with their stat bonus), currencies (gil, grand company seals, and the first topCurrencies non-zero balances with caps " +
            "and weekly tomestone progress) and retainers (count, total gil, completed ventures; loaded=false until a summoning bell has " +
            "been used this session). A section that cannot be read is omitted and explained under unavailable instead of failing the " +
            "call. For full detail use get_player, get_attributes, get_equipment, get_currencies, get_retainers, get_job_levels.",
        RequiresLogin = true)]
    public CharacterSheet GetCharacterSheet(
        [McpParam("How many non-zero currencies to include (0-50).", Minimum = 0, Maximum = 50)] int topCurrencies = 12,
        [McpParam("Include the retainer summary.")] bool includeRetainers = true)
    {
        var unavailable = new List<string>();
        var playerDto = Section("identity/job", unavailable, player.GetPlayer);
        if (playerDto == null && unavailable.Count > 0 && unavailable[0].Contains("logged in", StringComparison.OrdinalIgnoreCase))
            throw new McpToolException("No character is logged in.");

        return CharacterSheetShaper.Build(
            playerDto,
            Section("attributes", unavailable, () => attributes.GetAttributes()),
            Section("gear", unavailable, inventory.GetEquipment),
            Section("currencies", unavailable, inventory.GetCurrencies),
            includeRetainers ? Section("retainers", unavailable, retainers.GetRetainers) : null,
            topCurrencies,
            unavailable);
    }

    private static T? Section<T>(string name, List<string> unavailable, Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (McpToolException ex)
        {
            unavailable.Add($"{name}: {ex.Message}");
            return null;
        }
    }
}

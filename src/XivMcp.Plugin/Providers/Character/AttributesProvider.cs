using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using CsPlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Character;

/// <summary>
/// The character's attribute table, which is where crafting and gathering stats live: craftsmanship,
/// control, CP, gathering, perception, GP, as well as the combat stats. Read from PlayerState on the
/// framework thread.
/// </summary>
[McpProvider("character")]
public sealed unsafe class AttributesProvider
{
    /// <summary>BaseParam ids the crafting/gathering summary uses.</summary>
    private const int Cp = 11;
    private const int Gp = 10;
    private const int Craftsmanship = 70;
    private const int Control = 71;
    private const int Gathering = 72;
    private const int Perception = 73;

    private readonly GameDataIndex index;

    public AttributesProvider(IDataManager data) => index = GameDataIndex.For(data);

    public sealed record AttributeDto(uint BaseParamId, string Name, int Value);

    public sealed record CraftingStatsDto(int Craftsmanship, int Control, int Cp, int GatheringStat, int Perception, int Gp);

    public sealed record AttributesResult(
        uint ClassJobId,
        string? Job,
        CraftingStatsDto Crafting,
        List<AttributeDto> Attributes,
        string Note);

    [McpTool("get_attributes",
        Sources = ["client:PlayerState", "lumina:BaseParam"],
        Title = "Get character attributes",
        Description =
            "Every attribute the client tracks for the logged-in character, as {baseParamId, name, value} — including the crafter and " +
            "gatherer stats an agent needs before planning a craft: craftsmanship, control, CP, gathering, perception and GP, which are " +
            "also summarised under crafting. Combat stats (strength, dexterity, vitality, intelligence, mind, piety, tenacity, direct hit " +
            "rate, critical hit, determination, skill/spell speed) come back in the same list. " +
            "Values already include gear, food and any active bonuses, exactly as the Character window shows them, and change when the " +
            "player swaps gearset or class. Zero-valued attributes are omitted unless includeZero=true. " +
            "Use with get_recipe to check whether a recipe is craftable, and with get_gathering_info for perception requirements.")]
    public AttributesResult GetAttributes(
        [McpParam("Include attributes whose value is 0.")] bool includeZero = false)
    {
        var state = CsPlayerState.Instance();
        if (state == null)
        {
            throw new McpToolException("Player state is not loaded yet; try again once the character is in the world.");
        }

        var attributes = state->Attributes;
        var list = new List<AttributeDto>();
        foreach (var row in index.Sheet<Sheets.BaseParam>())
        {
            if (row.RowId == 0 || row.RowId >= (uint)attributes.Length)
            {
                continue;
            }

            var name = SheetJson.Text(row.Name);
            if (name.Length == 0)
            {
                continue;
            }

            var value = attributes[(int)row.RowId];
            if (value == 0 && !includeZero)
            {
                continue;
            }

            list.Add(new AttributeDto(row.RowId, name, value));
        }

        uint classJobId = state->CurrentClassJobId;
        var job = index.ClassJob(classJobId);

        return new AttributesResult(
            classJobId,
            job != null ? $"{job.Name} ({job.Abbreviation})" : null,
            new CraftingStatsDto(
                Read(attributes, Craftsmanship),
                Read(attributes, Control),
                Read(attributes, Cp),
                Read(attributes, Gathering),
                Read(attributes, Perception),
                Read(attributes, Gp)),
            list,
            "Attributes are the live values for the class/job the character is on right now, gear and food included.");
    }

    private static int Read(Span<int> attributes, int index) =>
        index >= 0 && index < attributes.Length ? attributes[index] : 0;
}

using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using CsMirageManager = FFXIVClientStructs.FFXIV.Client.Game.MirageManager;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Inventory;

/// <summary>The character's glamour plates, read from MirageManager on the framework thread.</summary>
[McpProvider("inventory")]
public sealed unsafe class GlamourProvider
{
    /// <summary>Slot order of a glamour plate, matching the plate UI.</summary>
    private static readonly string[] SlotNames =
        ["MainHand", "OffHand", "Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RingRight", "RingLeft"];

    private readonly GameDataIndex index;

    public GlamourProvider(IDataManager data) => index = GameDataIndex.For(data);

    public sealed record PlateSlotDto(string Slot, uint ItemId, string? Name, bool Hq, int? Dye1, int? Dye2);

    public sealed record PlateDto(int PlateNumber, bool Empty, int FilledSlots, List<PlateSlotDto> Slots);

    public sealed record PlatesResult(bool Loaded, int Total, int NonEmpty, List<PlateDto> Plates, string? Note);

    [McpTool("get_glamour_plates",
        Title = "Get glamour plates",
        Description =
            "The character's glamour plates (the Glamour Dresser plate slots) with, per plate, plateNumber, empty, filledSlots and the " +
            "occupied slots as {slot, itemId, name, hq, dye1, dye2}. Slots are MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, " +
            "Neck, Wrists, RingRight, RingLeft; empty slots are left out. " +
            "The client only holds plate data after the Glamour Plate window has been opened once this session: until then loaded=false " +
            "and the list is empty, which the note explains. Read-only — this never applies a plate. " +
            "Pass plateNumber to return one plate, or includeEmpty=true to see unused plates too.")]
    public PlatesResult GetGlamourPlates(
        [McpParam("Only this plate (1-20). Omit for all.", Minimum = 1, Maximum = 20)] int? plateNumber = null,
        [McpParam("Include plates with no items in them.")] bool includeEmpty = false)
    {
        var manager = CsMirageManager.Instance();
        if (manager == null)
        {
            return new PlatesResult(false, 0, 0, [], "The glamour manager is not available right now.");
        }

        var plates = new List<PlateDto>();
        var nonEmpty = 0;
        var total = 0;
        var anyData = false;

        for (var i = 0; i < manager->GlamourPlates.Length; i++)
        {
            total++;
            var plate = manager->GlamourPlates[i];
            var slots = new List<PlateSlotDto>();
            for (var slot = 0; slot < plate.ItemIds.Length && slot < SlotNames.Length; slot++)
            {
                var rawId = plate.ItemIds[slot];
                if (rawId == 0)
                {
                    continue;
                }

                anyData = true;
                var hq = rawId >= 1_000_000;
                var itemId = hq ? rawId - 1_000_000 : rawId;
                slots.Add(new PlateSlotDto(
                    SlotNames[slot],
                    itemId,
                    index.Item(itemId) is { } entry ? entry.Name : index.ItemName(itemId),
                    hq,
                    slot < plate.Stain0Ids.Length && plate.Stain0Ids[slot] != 0 ? plate.Stain0Ids[slot] : null,
                    slot < plate.Stain1Ids.Length && plate.Stain1Ids[slot] != 0 ? plate.Stain1Ids[slot] : null));
            }

            if (slots.Count > 0)
            {
                nonEmpty++;
            }

            if (plateNumber is { } wanted && wanted != i + 1)
            {
                continue;
            }

            if (slots.Count == 0 && !includeEmpty)
            {
                continue;
            }

            plates.Add(new PlateDto(i + 1, slots.Count == 0, slots.Count, slots));
        }

        return new PlatesResult(
            anyData,
            total,
            nonEmpty,
            plates,
            anyData
                ? null
                : "No plate data in memory: open the Glamour Plate window (at a summoning bell or the Glamour Dresser) once this session, then call again.");
    }

    /// <summary>Dye names, resolved lazily so an unknown id never throws.</summary>
    internal string? DyeName(int stainId) =>
        stainId <= 0 ? null : GameDataIndex.NullIfEmpty(SheetJson.Text(index.Row<Sheets.Stain>((uint)stainId)?.Name ?? default));
}

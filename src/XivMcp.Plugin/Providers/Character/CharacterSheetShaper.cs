using XivMcp.Plugin.Providers.Inventory;

namespace XivMcp.Plugin.Providers.Character;

public sealed record SheetIdentity(
    string Name,
    string? HomeWorld,
    string? DataCenter,
    string? CurrentWorld,
    string? Title,
    string? GrandCompany,
    string? GrandCompanyRank,
    string? FreeCompanyTag);

public sealed record SheetJob(string? Abbreviation, string? Name, string? Role, int Level, int? SyncedLevel, uint MaxHp, uint? MaxMp);

public sealed record SheetStat(string Name, int Value);

public sealed record SheetGearPiece(
    string Slot,
    uint ItemId,
    string Name,
    int ItemLevel,
    bool? Hq,
    double? ConditionPercent,
    IReadOnlyList<string>? Materia);

public sealed record SheetGear(
    int AverageItemLevel,
    string AverageItemLevelNote,
    int? LowestItemLevel,
    string? LowestItemLevelSlot,
    double? LowestConditionPercent,
    int MateriaMelded,
    IReadOnlyList<SheetGearPiece> Pieces,
    IReadOnlyList<string>? EmptySlots);

public sealed record SheetCurrency(string Name, long Count, long? Max, long? WeeklyAcquired, long? WeeklyLimit);

public sealed record SheetCurrencies(long Gil, string? GrandCompanySeals, int NonZero, IReadOnlyList<SheetCurrency> Top);

public sealed record SheetRetainer(string Name, string? ClassJob, int Level, long Gil, int MarketItems, string? Venture, bool? VentureComplete);

public sealed record SheetRetainers(bool Loaded, string? Note, int Count, long TotalGil, int VenturesComplete, IReadOnlyList<SheetRetainer> Retainers);

public sealed record CharacterSheet(
    SheetIdentity? Identity,
    SheetJob? Job,
    IReadOnlyList<SheetStat>? Attributes,
    SheetGear? Gear,
    SheetCurrencies? Currencies,
    SheetRetainers? Retainers,
    IReadOnlyList<string>? Unavailable,
    string Note);

/// <summary>Pure shaping for get_character_sheet: condenses the snapshots of the existing character tools into one summary.</summary>
internal static class CharacterSheetShaper
{
    public const int MaxStats = 40;

    public const string Note =
        "A condensed composite of get_player, get_attributes, get_equipment, get_currencies and get_retainers, read the same way those " +
        "tools read them; call them for full detail. averageItemLevel is computed from the equipped gear with the character window's " +
        "rule (12 slots, a two-handed main hand counted twice, floored), not read from the client.";

    /// <summary>Crafter/gatherer stats only make sense on those jobs; combat stats are hidden there in turn.</summary>
    private static readonly HashSet<uint> HandLandStats = [10, 11, 70, 71, 72, 73]; // BaseParam: GP, CP, craftsmanship, control, gathering, perception

    public static CharacterSheet Build(
        PlayerProvider.PlayerDto? player,
        AttributesProvider.AttributesResult? attributes,
        EquipmentResult? equipment,
        CurrenciesResult? currencies,
        RetainersResult? retainers,
        int topCurrencies,
        IReadOnlyList<string>? unavailable)
    {
        return new CharacterSheet(
            player == null ? null : Identity(player),
            player == null ? null : Job(player),
            attributes == null ? null : Stats(attributes, player?.Job?.Role),
            equipment == null ? null : Gear(equipment),
            currencies == null ? null : Currencies(currencies, topCurrencies),
            retainers == null ? null : Retainers(retainers),
            unavailable is { Count: > 0 } ? unavailable : null,
            Note);
    }

    public static SheetIdentity Identity(PlayerProvider.PlayerDto p) => new(
        p.Name,
        p.HomeWorld?.Name,
        p.HomeWorld?.DataCenter,
        p.IsWorldVisiting ? p.CurrentWorld?.Name : null,
        p.Title?.Name,
        p.GrandCompany?.Name,
        p.GrandCompany?.RankName,
        string.IsNullOrWhiteSpace(p.FreeCompanyTag) ? null : p.FreeCompanyTag);

    public static SheetJob Job(PlayerProvider.PlayerDto p) => new(
        p.Job?.Abbreviation,
        p.Job?.Name,
        p.Job?.Role,
        p.Level,
        p.IsLevelSynced ? p.SyncedLevel : null,
        p.Hp.Max,
        p.Mp?.Max);

    public static List<SheetStat> Stats(AttributesProvider.AttributesResult attributes, string? role)
    {
        var handLand = IsHandOrLand(role);
        return attributes.Attributes
            .Where(a => a.Value != 0 && !string.IsNullOrWhiteSpace(a.Name))
            .Where(a => role == null || HandLandStats.Contains(a.BaseParamId) == handLand)
            .Take(MaxStats)
            .Select(a => new SheetStat(a.Name, a.Value))
            .ToList();
    }

    public static SheetGear Gear(EquipmentResult equipment)
    {
        var gear = equipment.Slots.Where(s => !s.Slot.Equals("SoulCrystal", StringComparison.Ordinal)).ToList();
        var lowest = gear.Count > 0 ? gear.MinBy(s => s.ItemLevel) : null;
        var conditions = gear.Where(s => s.ConditionPercent != null).Select(s => s.ConditionPercent!.Value).ToList();
        return new SheetGear(
            equipment.AverageItemLevel,
            equipment.AverageItemLevelNote,
            lowest?.ItemLevel,
            lowest?.Slot,
            conditions.Count > 0 ? conditions.Min() : null,
            gear.Sum(s => s.Materia?.Count ?? 0),
            gear.Select(s => new SheetGearPiece(s.Slot, s.ItemId, s.Name, s.ItemLevel, s.Hq, s.ConditionPercent, s.Materia)).ToList(),
            equipment.EmptySlots.Count > 0 ? equipment.EmptySlots : null);
    }

    public static SheetCurrencies Currencies(CurrenciesResult currencies, int top)
    {
        top = Math.Clamp(top, 0, 50);
        var held = currencies.Currencies.Where(c => c.Count > 0).ToList();
        var seals = currencies.GrandCompany is { } gc
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{gc.Seals}/{gc.Max} ({gc.Name})")
            : null;
        return new SheetCurrencies(
            currencies.Gil,
            seals,
            held.Count,
            held.Take(top).Select(c => new SheetCurrency(c.Name, c.Count, c.Max, c.WeeklyAcquired, c.WeeklyLimit)).ToList());
    }

    public static SheetRetainers Retainers(RetainersResult retainers)
    {
        var list = retainers.Retainers.Where(r => r.Available).ToList();
        return new SheetRetainers(
            retainers.Loaded,
            retainers.Note,
            list.Count,
            list.Sum(r => r.Gil),
            list.Count(r => r.Venture is { Complete: true }),
            list.Select(r => new SheetRetainer(r.Name, r.ClassJob, r.Level, r.Gil, r.MarketItemCount, r.Venture?.Name, r.Venture?.Complete)).ToList());
    }

    private static bool IsHandOrLand(string? role) => role is "crafter" or "gatherer";
}

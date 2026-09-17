namespace XivMcp.Plugin.Providers.Inventory;

// Output DTOs for live character-state tools. Serialized camelCase with nulls omitted.

public sealed record InventorySlot(
    string Container,
    string Group,
    int Slot,
    uint ItemId,
    string Name,
    int Quantity,
    bool? Hq = null,
    bool? Collectable = null,
    int? Collectability = null,
    double? ConditionPercent = null,
    double? SpiritbondPercent = null,
    IReadOnlyList<string>? Materia = null,
    uint? GlamourItemId = null,
    string? Glamour = null,
    IReadOnlyList<string>? Dyes = null,
    ulong? MarketPrice = null,
    int? ItemLevel = null);

public sealed record ContainerStatus(string Container, string Group, bool Loaded, int Size, int Used);

public sealed record InventoryResult(
    IReadOnlyList<ContainerStatus> Containers,
    IReadOnlyList<string>? Unavailable,
    string? Retainer,
    int Total,
    int Offset,
    int Returned,
    bool Truncated,
    IReadOnlyList<InventorySlot> Items);

public sealed record OwnedLocation(string Group, string Container, int Quantity, int Slots, int? HqQuantity = null);

public sealed record OwnedItem(
    uint ItemId,
    string Name,
    int Total,
    int? Hq,
    IReadOnlyList<OwnedLocation> Locations);

public sealed record OwnedItemsResult(
    int Total,
    int Returned,
    bool Truncated,
    IReadOnlyList<OwnedItem> Items,
    IReadOnlyList<string>? NotSearched,
    string? Retainer);

public sealed record EquippedSlot(
    string Slot,
    uint ItemId,
    string Name,
    int ItemLevel,
    int EquipLevel,
    bool? Hq,
    double? ConditionPercent,
    double? SpiritbondPercent,
    IReadOnlyList<string>? Materia,
    uint? GlamourItemId,
    string? Glamour,
    IReadOnlyList<string>? Dyes);

public sealed record EquipmentResult(
    string? ClassJob,
    int AverageItemLevel,
    string AverageItemLevelNote,
    string? SoulCrystal,
    IReadOnlyList<EquippedSlot> Slots,
    IReadOnlyList<string> EmptySlots);

public sealed record CurrencyEntry(
    string Category,
    uint ItemId,
    string Name,
    long Count,
    long? Max = null,
    long? WeeklyAcquired = null,
    long? WeeklyLimit = null);

public sealed record GrandCompanySeals(uint GrandCompanyId, string Name, uint SealItemId, long Seals, long Max);

public sealed record CurrenciesResult(long Gil, GrandCompanySeals? GrandCompany, IReadOnlyList<CurrencyEntry> Currencies);

public sealed record VentureInfo(
    uint VentureId,
    string? Name,
    DateTimeOffset? CompletesAt,
    bool Complete,
    int? MinutesRemaining);

public sealed record RetainerInfo(
    string RetainerId,
    string Name,
    bool Available,
    string? ClassJob,
    int Level,
    long Gil,
    int ItemCount,
    int MarketItemCount,
    DateTimeOffset? MarketExpiresAt,
    string? Town,
    VentureInfo? Venture);

public sealed record RetainersResult(bool Loaded, string? Note, int MaxRetainers, string? ActiveRetainer, IReadOnlyList<RetainerInfo> Retainers);

public sealed record QuestStatus(uint QuestId, string? Name, bool Exists, bool Completed, bool Accepted, int? Sequence, bool Repeatable);

public sealed record QuestStatusResult(int Completed, int Accepted, IReadOnlyList<QuestStatus> Quests);

public sealed record CollectionEntry(uint Id, string Name, bool Owned);

public sealed record CollectionResult(
    string Kind,
    int TotalInGame,
    int Owned,
    int Missing,
    int Matching,
    int Offset,
    int Returned,
    bool Truncated,
    IReadOnlyList<CollectionEntry> Items,
    string? Note);

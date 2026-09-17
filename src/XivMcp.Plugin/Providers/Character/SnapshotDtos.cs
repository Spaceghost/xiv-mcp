// DTOs shared by the character / world / party / duty providers. Serialized camelCase, nulls omitted.
namespace XivMcp.Plugin.Providers.Character;

public sealed record Vec3Dto(double X, double Y, double Z);

/// <summary>Map coordinates as shown in game (X/Y truncated to one decimal); Z is the height readout.</summary>
public sealed record MapCoordsDto(double X, double Y, double? Z = null);

public sealed record IdNameDto(uint Id, string? Name);

public sealed record PoolDto(uint Current, uint Max, double Percent);

public sealed record RotationDto(double Radians, double HeadingDegrees, string Compass);

public sealed record WorldDto(uint Id, string? Name, string? DataCenter);

public sealed record JobDto(uint Id, string Abbreviation, string Name, string Role);

public sealed record StatusDto(
    uint Id,
    string? Name,
    int Param,
    int? Stacks,
    double? RemainingSeconds,
    bool IsPermanent,
    uint? SourceEntityId,
    string? SourceName);

public sealed record CastDto(
    uint ActionId,
    string ActionType,
    string? ActionName,
    double ElapsedSeconds,
    double TotalSeconds,
    double RemainingSeconds,
    bool Interruptible,
    string? TargetGameObjectId);

/// <summary>Compact row used by list_nearby_objects.</summary>
public sealed record ObjectSummaryDto(
    string Kind,
    string? SubKind,
    string Name,
    uint? EntityId,
    string GameObjectId,
    uint BaseId,
    int ObjectIndex,
    double Distance,
    double HorizontalDistance,
    Vec3Dto Position,
    MapCoordsDto? MapCoordinates,
    bool IsTargetable,
    bool? IsDead,
    int? Level,
    double? HpPercent,
    string? Job,
    string? World,
    bool? IsHostile,
    bool? InCombat,
    bool? IsCasting,
    uint? OwnerEntityId);

/// <summary>Full detail used by get_target.</summary>
public sealed record ObjectDetailDto(
    string Kind,
    string? SubKind,
    string Name,
    uint? EntityId,
    string GameObjectId,
    uint BaseId,
    uint? NameId,
    int ObjectIndex,
    int? Level,
    PoolDto? Hp,
    PoolDto? Mp,
    int? ShieldPercent,
    JobDto? Job,
    WorldDto? HomeWorld,
    WorldDto? CurrentWorld,
    string? FreeCompanyTag,
    IdNameDto? OnlineStatus,
    double Distance,
    double HorizontalDistance,
    double? EdgeDistance,
    double HitboxRadius,
    Vec3Dto Position,
    MapCoordsDto? MapCoordinates,
    RotationDto Rotation,
    bool IsTargetable,
    bool? IsDead,
    bool? IsHostile,
    bool? InCombat,
    bool? IsPartyMember,
    bool? IsAllianceMember,
    bool? IsFriend,
    uint? OwnerEntityId,
    string? TargetGameObjectId,
    string? TargetName,
    CastDto? Cast,
    int? StatusCount,
    List<StatusDto>? Statuses,
    bool? StatusesTruncated);

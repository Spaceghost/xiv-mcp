using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using XivMcp.Plugin.Util;
using CsActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;
using CsCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using LAction = Lumina.Excel.Sheets.Action;
using LClassJob = Lumina.Excel.Sheets.ClassJob;
using LMap = Lumina.Excel.Sheets.Map;
using LTerritoryType = Lumina.Excel.Sheets.TerritoryType;
using LTerritoryTypeTransient = Lumina.Excel.Sheets.TerritoryTypeTransient;
using LWorld = Lumina.Excel.Sheets.World;

namespace XivMcp.Plugin.Providers.Character;

/// <summary>Map parameters for converting world positions in the current territory to map coordinates.</summary>
internal readonly struct MapContext
{
    public readonly uint TerritoryId;
    public readonly uint MapId;
    public readonly ushort SizeFactor;
    public readonly short OffsetX;
    public readonly short OffsetY;
    public readonly short OffsetZ;
    public readonly bool Valid;

    public MapContext(uint territoryId, uint mapId, ushort sizeFactor, short offsetX, short offsetY, short offsetZ)
    {
        TerritoryId = territoryId;
        MapId = mapId;
        SizeFactor = sizeFactor;
        OffsetX = offsetX;
        OffsetY = offsetY;
        OffsetZ = offsetZ;
        Valid = mapId != 0;
    }

    /// <summary>Current territory/map from IClientState (falls back to the territory's default map).</summary>
    public static MapContext Current(IClientState clientState, IDataManager data) =>
        For(data, clientState.TerritoryType, clientState.MapId);

    public static MapContext For(IDataManager data, uint territoryId, uint mapId)
    {
        if (mapId == 0 && data.GetExcelSheet<LTerritoryType>().TryGetRow(territoryId, out var territory))
        {
            mapId = territory.Map.RowId;
        }

        if (mapId == 0 || !data.GetExcelSheet<LMap>().TryGetRow(mapId, out var map))
        {
            return default;
        }

        short offsetZ = 0;
        if (data.GetExcelSheet<LTerritoryTypeTransient>().TryGetRow(territoryId, out var transient))
        {
            offsetZ = transient.OffsetZ;
        }

        return new MapContext(territoryId, mapId, map.SizeFactor, map.OffsetX, map.OffsetY, offsetZ);
    }

    public MapCoordsDto? ToMap(Vector3 world)
    {
        if (!Valid)
        {
            return null;
        }

        return new MapCoordsDto(
            GameMath.DisplayCoordinate(GameMath.WorldToMapCoordinate(world.X, SizeFactor, OffsetX)),
            GameMath.DisplayCoordinate(GameMath.WorldToMapCoordinate(world.Z, SizeFactor, OffsetY)),
            GameMath.DisplayCoordinate(GameMath.WorldHeightToMapZ(world.Y, OffsetZ)));
    }
}

/// <summary>Copies game objects into DTOs. Call only on the framework thread; never retain inputs.</summary>
internal static class Snapshots
{
    public const uint InvalidEntityId = 0xE0000000;

    public static uint? EntityIdOrNull(uint entityId) =>
        entityId is 0 or InvalidEntityId ? null : entityId;

    public static Vec3Dto Vec(Vector3 v) => new(GameMath.Round(v.X), GameMath.Round(v.Y), GameMath.Round(v.Z));

    public static PoolDto Pool(uint current, uint max) =>
        new(current, max, max == 0 ? 0 : Math.Round(current * 100.0 / max, 1));

    public static RotationDto Rotation(float radians)
    {
        var heading = GameMath.RotationToHeadingDegrees(radians);
        return new RotationDto(GameMath.Round(radians, 3), heading, GameMath.HeadingToCompass(heading));
    }

    public static string KindName(ObjectKind kind) => kind switch
    {
        ObjectKind.Pc => "player",
        ObjectKind.BattleNpc => "battleNpc",
        ObjectKind.EventNpc => "eventNpc",
        ObjectKind.Treasure => "treasure",
        ObjectKind.Aetheryte => "aetheryte",
        ObjectKind.GatheringPoint => "gatheringPoint",
        ObjectKind.EventObj => "eventObj",
        ObjectKind.Mount => "mount",
        ObjectKind.Companion => "companion",
        ObjectKind.Retainer => "retainer",
        ObjectKind.AreaObject => "areaObject",
        ObjectKind.HousingEventObject => "housing",
        ObjectKind.Cutscene => "cutscene",
        ObjectKind.ReactionEventObject => "reactionEventObj",
        ObjectKind.Ornament => "ornament",
        ObjectKind.CardStand => "cardStand",
        ObjectKind.FollowMount => "followMount",
        _ => "none",
    };

    public static string? SubKindName(IGameObject obj) => obj is IBattleNpc npc
        ? npc.BattleNpcKind switch
        {
            BattleNpcSubKind.Combatant => "combatant",
            BattleNpcSubKind.Pet => "pet",
            BattleNpcSubKind.Buddy => "buddy",
            BattleNpcSubKind.BNpcPart => "part",
            BattleNpcSubKind.Player => "player",
            BattleNpcSubKind.RaceChocobo => "raceChocobo",
            BattleNpcSubKind.LovmMinion => "lovmMinion",
            BattleNpcSubKind.NpcPartyMember => "npcPartyMember",
            _ => null,
        }
        : null;

    /// <summary>tank | healer | melee | physicalRanged | magicalRanged | crafter | gatherer | none.</summary>
    public static string RoleName(in LClassJob job) => job.Role switch
    {
        1 => "tank",
        2 => "melee",
        3 => job.PrimaryStat is 4 or 5 ? "magicalRanged" : "physicalRanged",
        4 => "healer",
        _ => job.ClassJobCategory.RowId switch
        {
            33 => "crafter",
            32 => "gatherer",
            _ => "none",
        },
    };

    public static JobDto? Job(RowRef<LClassJob> jobRef)
    {
        if (!jobRef.TryGetValue(out var job))
        {
            return null;
        }

        return Job(job);
    }

    public static JobDto Job(in LClassJob job) =>
        new(job.RowId, job.Abbreviation.ExtractText(), job.Name.ExtractText(), RoleName(job));

    public static WorldDto? World(RowRef<LWorld> worldRef)
    {
        if (worldRef.RowId == 0 || !worldRef.TryGetValue(out var world))
        {
            return null;
        }

        return new WorldDto(world.RowId, world.Name.ExtractText(), world.DataCenter.ValueNullable?.Name.ExtractText());
    }

    public static WorldDto? World(IDataManager data, uint worldId)
    {
        if (worldId == 0 || !data.GetExcelSheet<LWorld>().TryGetRow(worldId, out var world))
        {
            return null;
        }

        return new WorldDto(world.RowId, world.Name.ExtractText(), world.DataCenter.ValueNullable?.Name.ExtractText());
    }

    public static StatusDto Status(IStatus status)
    {
        var row = status.GameData.ValueNullable;
        var permanent = row?.IsPermanent ?? false;
        var remaining = status.RemainingTime;
        uint? sourceId = EntityIdOrNull(status.SourceId);
        string? sourceName = null;
        if (sourceId != null)
        {
            try
            {
                sourceName = status.SourceObject?.Name.TextValue;
            }
            catch (Exception)
            {
                sourceName = null;
            }
        }

        return new StatusDto(
            status.StatusId,
            row?.Name.ExtractText(),
            status.Param,
            row is { MaxStacks: > 0 } ? status.Param : null,
            !permanent && remaining > 0 ? GameMath.Round(remaining, 1) : null,
            permanent || remaining <= 0,
            sourceId,
            string.IsNullOrEmpty(sourceName) ? null : sourceName);
    }

    public static List<StatusDto> Statuses(StatusList list, int limit, out int total)
    {
        var result = new List<StatusDto>();
        total = 0;
        foreach (var status in list)
        {
            total++;
            if (result.Count < limit)
            {
                result.Add(Status(status));
            }
        }

        return result;
    }

    public static CastDto? Cast(IDataManager data, IBattleChara chara)
    {
        if (!chara.IsCasting)
        {
            return null;
        }

        var type = (CsActionType)chara.CastActionType;
        string? actionName = null;
        if (type == CsActionType.Action && data.GetExcelSheet<LAction>().TryGetRow(chara.CastActionId, out var action))
        {
            actionName = action.Name.ExtractText();
        }

        var total = chara.TotalCastTime;
        var elapsed = chara.CurrentCastTime;
        var target = chara.CastTargetObjectId;
        return new CastDto(
            chara.CastActionId,
            type.ToString(),
            string.IsNullOrEmpty(actionName) ? null : actionName,
            GameMath.Round(elapsed, 2),
            GameMath.Round(total, 2),
            GameMath.Round(Math.Max(0, total - elapsed), 2),
            chara.IsCastInterruptible,
            target is 0 or InvalidEntityId ? null : target.ToString(CultureInfo.InvariantCulture));
    }

    public static ObjectSummaryDto Summary(IGameObject obj, Vector3 origin, in MapContext map)
    {
        var position = obj.Position;
        var chara = obj as ICharacter;
        var battle = obj as IBattleChara;
        var player = obj as IPlayerCharacter;
        var flags = chara?.StatusFlags ?? StatusFlags.None;
        return new ObjectSummaryDto(
            KindName(obj.ObjectKind),
            SubKindName(obj),
            obj.Name.TextValue,
            EntityIdOrNull(obj.EntityId),
            obj.GameObjectId.ToString(CultureInfo.InvariantCulture),
            obj.BaseId,
            obj.ObjectIndex,
            GameMath.Round(GameMath.Distance3D(origin, position)),
            GameMath.Round(GameMath.DistanceHorizontal(origin, position)),
            Vec(position),
            map.ToMap(position),
            obj.IsTargetable,
            chara != null ? obj.IsDead : null,
            chara != null ? chara.Level : null,
            chara is { MaxHp: > 0 } ? Math.Round(chara.CurrentHp * 100.0 / chara.MaxHp, 1) : null,
            player != null ? player.ClassJob.ValueNullable?.Abbreviation.ExtractText() : null,
            player != null ? player.HomeWorld.ValueNullable?.Name.ExtractText() : null,
            obj.ObjectKind == ObjectKind.BattleNpc ? (flags & StatusFlags.Hostile) != 0 : null,
            chara != null ? (flags & StatusFlags.InCombat) != 0 : null,
            battle != null ? battle.IsCasting : null,
            EntityIdOrNull(obj.OwnerId));
    }

    public static unsafe ObjectDetailDto Detail(IDataManager data, IGameObject obj, IGameObject? origin, in MapContext map, int statusLimit)
    {
        var position = obj.Position;
        var chara = obj as ICharacter;
        var battle = obj as IBattleChara;
        var player = obj as IPlayerCharacter;
        var flags = chara?.StatusFlags ?? StatusFlags.None;
        var originPos = origin?.Position ?? position;
        var horizontal = GameMath.DistanceHorizontal(originPos, position);
        double? edge = origin != null && !ReferenceEquals(origin, obj) && origin.Address != obj.Address
            ? GameMath.Round(Math.Max(0, horizontal - obj.HitboxRadius - origin.HitboxRadius))
            : null;

        List<StatusDto>? statuses = null;
        int? statusCount = null;
        bool? statusesTruncated = null;
        if (battle != null)
        {
            statuses = Statuses(battle.StatusList, Math.Max(0, statusLimit), out var total);
            statusCount = total;
            statusesTruncated = total > statuses.Count ? true : null;
        }

        IdNameDto? onlineStatus = null;
        if (player != null && player.OnlineStatus.RowId != 0)
        {
            onlineStatus = new IdNameDto(player.OnlineStatus.RowId, player.OnlineStatus.ValueNullable?.Name.ExtractText());
        }

        string? targetId = null;
        string? targetName = null;
        var targetObjectId = obj.TargetObjectId;
        if (targetObjectId is not (0 or InvalidEntityId))
        {
            targetId = targetObjectId.ToString(CultureInfo.InvariantCulture);
            try
            {
                targetName = obj.TargetObject?.Name.TextValue;
            }
            catch (Exception)
            {
                targetName = null;
            }
        }

        string? companyTag = null;
        if (player != null)
        {
            companyTag = player.CompanyTag.TextValue;
            if (string.IsNullOrEmpty(companyTag))
            {
                companyTag = null;
            }
        }

        return new ObjectDetailDto(
            KindName(obj.ObjectKind),
            SubKindName(obj),
            obj.Name.TextValue,
            EntityIdOrNull(obj.EntityId),
            obj.GameObjectId.ToString(CultureInfo.InvariantCulture),
            obj.BaseId,
            chara is { NameId: > 0 } ? chara.NameId : null,
            obj.ObjectIndex,
            chara != null ? chara.Level : null,
            chara != null ? Pool(chara.CurrentHp, chara.MaxHp) : null,
            chara is { MaxMp: > 0 } ? Pool(chara.CurrentMp, chara.MaxMp) : null,
            chara != null ? chara.ShieldPercentage : null,
            chara != null && chara.ClassJob.RowId != 0 ? Job(chara.ClassJob) : null,
            player != null ? World(player.HomeWorld) : null,
            player != null ? World(player.CurrentWorld) : null,
            companyTag,
            onlineStatus,
            GameMath.Round(GameMath.Distance3D(originPos, position)),
            GameMath.Round(horizontal),
            edge,
            GameMath.Round(obj.HitboxRadius),
            Vec(position),
            map.ToMap(position),
            Rotation(obj.Rotation),
            obj.IsTargetable,
            chara != null ? obj.IsDead : null,
            obj.ObjectKind == ObjectKind.BattleNpc ? (flags & StatusFlags.Hostile) != 0 : null,
            chara != null ? (flags & StatusFlags.InCombat) != 0 : null,
            chara != null ? (flags & StatusFlags.PartyMember) != 0 : null,
            chara != null ? (flags & StatusFlags.AllianceMember) != 0 : null,
            player != null ? (flags & StatusFlags.Friend) != 0 : null,
            EntityIdOrNull(obj.OwnerId),
            targetId,
            string.IsNullOrEmpty(targetName) ? null : targetName,
            battle != null ? Cast(data, battle) : null,
            statusCount,
            statuses,
            statusesTruncated);
    }

    /// <summary>Title id from the native Character struct (0 when none).</summary>
    public static unsafe ushort TitleId(ICharacter chara)
    {
        var native = (CsCharacter*)chara.Address;
        return native == null ? (ushort)0 : native->CharacterData.TitleId;
    }
}

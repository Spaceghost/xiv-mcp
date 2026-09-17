using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using LGCRankGridaniaFemaleText = Lumina.Excel.Sheets.GCRankGridaniaFemaleText;
using LGCRankGridaniaMaleText = Lumina.Excel.Sheets.GCRankGridaniaMaleText;
using LGCRankLimsaFemaleText = Lumina.Excel.Sheets.GCRankLimsaFemaleText;
using LGCRankLimsaMaleText = Lumina.Excel.Sheets.GCRankLimsaMaleText;
using LGCRankUldahFemaleText = Lumina.Excel.Sheets.GCRankUldahFemaleText;
using LGCRankUldahMaleText = Lumina.Excel.Sheets.GCRankUldahMaleText;
using LMap = Lumina.Excel.Sheets.Map;
using LTerritoryType = Lumina.Excel.Sheets.TerritoryType;
using LTitle = Lumina.Excel.Sheets.Title;
using Sex = Dalamud.Game.Player.Sex;

namespace XivMcp.Plugin.Providers.Character;

[McpProvider("character")]
public sealed class PlayerProvider
{
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IDataManager data;

    public PlayerProvider(IObjectTable objects, IClientState clientState, IPlayerState playerState, ICondition condition, IDataManager data)
    {
        this.objects = objects;
        this.clientState = clientState;
        this.playerState = playerState;
        this.condition = condition;
        this.data = data;
    }

    public sealed record GrandCompanyDto(uint Id, string? Name, int Rank, string? RankName);

    public sealed record TitleDto(uint Id, string? Name, bool IsPrefix);

    public sealed record PlayerDto(
        string Name,
        uint EntityId,
        string ContentId,
        WorldDto? HomeWorld,
        WorldDto? CurrentWorld,
        bool IsWorldVisiting,
        JobDto? Job,
        int Level,
        int? SyncedLevel,
        bool IsLevelSynced,
        PoolDto Hp,
        PoolDto? Mp,
        PoolDto? Cp,
        PoolDto? Gp,
        int ShieldPercent,
        Vec3Dto Position,
        MapCoordsDto? MapCoordinates,
        RotationDto Rotation,
        IdNameDto Territory,
        IdNameDto? Map,
        IdNameDto? OnlineStatus,
        GrandCompanyDto? GrandCompany,
        string? FreeCompanyTag,
        TitleDto? Title,
        IdNameDto? Mount,
        IdNameDto? Minion,
        bool IsCasting,
        CastDto? Cast,
        bool InCombat,
        bool IsDead,
        bool WeaponDrawn,
        int StatusCount,
        List<StatusDto> Statuses);

    [McpTool("get_player",
        Title = "Get player character",
        Description = "Snapshot of the logged-in player character. Returns name, entityId, contentId (string), home/current world with data center " +
                      "(isWorldVisiting when they differ), job {id, abbreviation, name, role}, level plus syncedLevel when level-synced, hp/mp " +
                      "(and cp/gp on crafters/gatherers) as {current, max, percent}, shieldPercent, world position {x,y,z}, mapCoordinates {x,y,z} " +
                      "as displayed on the in-game map, rotation {radians, headingDegrees (0=N, 90=E), compass}, territory and map {id, name}, " +
                      "online status, grand company with rank name, free company tag, title, current mount/minion, isCasting with cast details, " +
                      "inCombat, isDead, weaponDrawn and every active status {id, name, param, stacks, remainingSeconds, isPermanent, source}. " +
                      "Use this first to answer anything about 'me'/'my character'; use get_location for zone details and get_job_levels for all jobs.")]
    public PlayerDto GetPlayer() => BuildPlayer();

    [McpResource("ffxiv://player",
        Name = "Player character",
        Description = "Same JSON as the get_player tool: the logged-in character's identity, job, level, HP/MP, position, statuses.")]
    public PlayerDto PlayerResource() => BuildPlayer();

    private unsafe PlayerDto BuildPlayer()
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        var map = MapContext.Current(clientState, data);
        var sex = playerState.IsLoaded ? playerState.Sex : Sex.Male;

        var home = Snapshots.World(player.HomeWorld);
        var current = Snapshots.World(player.CurrentWorld);

        var territoryId = clientState.TerritoryType;
        string? territoryName = null;
        if (data.GetExcelSheet<LTerritoryType>().TryGetRow(territoryId, out var territory))
        {
            territoryName = territory.PlaceName.ValueNullable?.Name.ExtractText();
        }

        IdNameDto? mapDto = null;
        if (map.Valid && data.GetExcelSheet<LMap>().TryGetRow(map.MapId, out var mapRow))
        {
            var mapName = mapRow.PlaceName.ValueNullable?.Name.ExtractText();
            var sub = mapRow.PlaceNameSub.ValueNullable?.Name.ExtractText();
            mapDto = new IdNameDto(map.MapId, string.IsNullOrEmpty(sub) ? mapName : $"{mapName} - {sub}");
        }

        IdNameDto? onlineStatus = null;
        if (player.OnlineStatus.RowId != 0)
        {
            onlineStatus = new IdNameDto(player.OnlineStatus.RowId, player.OnlineStatus.ValueNullable?.Name.ExtractText());
        }

        GrandCompanyDto? gc = null;
        if (playerState.IsLoaded && playerState.GrandCompany.RowId != 0 && playerState.GrandCompany.TryGetValue(out var gcRow))
        {
            var rank = playerState.GetGrandCompanyRank(gcRow);
            gc = new GrandCompanyDto(gcRow.RowId, gcRow.Name.ExtractText(), rank, GcRankName(gcRow.RowId, rank, sex));
        }

        TitleDto? title = null;
        var titleId = Snapshots.TitleId(player);
        if (titleId != 0 && data.GetExcelSheet<LTitle>().TryGetRow(titleId, out var titleRow))
        {
            var text = sex == Sex.Female ? titleRow.Feminine.ExtractText() : titleRow.Masculine.ExtractText();
            title = new TitleDto(titleId, text, titleRow.IsPrefix);
        }

        IdNameDto? mount = null;
        if (player.CurrentMount is { RowId: not 0 } mountRef)
        {
            mount = new IdNameDto(mountRef.RowId, mountRef.ValueNullable?.Singular.ExtractText());
        }

        IdNameDto? minion = null;
        if (player.CurrentMinion is { RowId: not 0 } minionRef)
        {
            minion = new IdNameDto(minionRef.RowId, minionRef.ValueNullable?.Singular.ExtractText());
        }

        var statuses = Snapshots.Statuses(player.StatusList, 60, out var statusCount);
        var synced = playerState.IsLoaded && playerState.IsLevelSynced;
        var level = playerState.IsLoaded && playerState.Level > 0 ? playerState.Level : player.Level;
        var tag = player.CompanyTag.TextValue;

        return new PlayerDto(
            player.Name.TextValue,
            player.EntityId,
            playerState.ContentId.ToString(),
            home,
            current,
            home != null && current != null && home.Id != current.Id,
            Snapshots.Job(player.ClassJob),
            level,
            synced ? playerState.EffectiveLevel : null,
            synced,
            Snapshots.Pool(player.CurrentHp, player.MaxHp),
            player.MaxMp > 0 ? Snapshots.Pool(player.CurrentMp, player.MaxMp) : null,
            player.MaxCp > 0 ? Snapshots.Pool(player.CurrentCp, player.MaxCp) : null,
            player.MaxGp > 0 ? Snapshots.Pool(player.CurrentGp, player.MaxGp) : null,
            player.ShieldPercentage,
            Snapshots.Vec(player.Position),
            map.ToMap(player.Position),
            Snapshots.Rotation(player.Rotation),
            new IdNameDto(territoryId, territoryName),
            mapDto,
            onlineStatus,
            gc,
            string.IsNullOrEmpty(tag) ? null : tag,
            title,
            mount,
            minion,
            player.IsCasting,
            Snapshots.Cast(data, player),
            condition[ConditionFlag.InCombat],
            player.IsDead,
            (player.StatusFlags & StatusFlags.WeaponOut) != 0,
            statusCount,
            statuses);
    }

    private string? GcRankName(uint gcId, int rank, Sex sex)
    {
        if (rank <= 0)
        {
            return null;
        }

        var row = (uint)rank;
        var female = sex == Sex.Female;
        return gcId switch
        {
            1 when female => data.GetExcelSheet<LGCRankLimsaFemaleText>().GetRowOrDefault(row)?.Singular.ExtractText(),
            1 => data.GetExcelSheet<LGCRankLimsaMaleText>().GetRowOrDefault(row)?.Singular.ExtractText(),
            2 when female => data.GetExcelSheet<LGCRankGridaniaFemaleText>().GetRowOrDefault(row)?.Singular.ExtractText(),
            2 => data.GetExcelSheet<LGCRankGridaniaMaleText>().GetRowOrDefault(row)?.Singular.ExtractText(),
            3 when female => data.GetExcelSheet<LGCRankUldahFemaleText>().GetRowOrDefault(row)?.Singular.ExtractText(),
            3 => data.GetExcelSheet<LGCRankUldahMaleText>().GetRowOrDefault(row)?.Singular.ExtractText(),
            _ => null,
        };
    }
}

using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Character;
using XivMcp.Plugin.Util;
using CsFateManager = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager;

namespace XivMcp.Plugin.Providers.World;

[McpProvider("world")]
public sealed class FateProvider
{
    private readonly IFateTable fates;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IDataManager data;

    public FateProvider(IFateTable fates, IObjectTable objects, IClientState clientState, IDataManager data)
    {
        this.fates = fates;
        this.objects = objects;
        this.clientState = clientState;
        this.data = data;
    }

    public sealed record FateDto(
        uint Id,
        string Name,
        string? Objective,
        int Level,
        int MaxLevel,
        string State,
        int ProgressPercent,
        long? TimeRemainingSeconds,
        int DurationSeconds,
        DateTimeOffset? StartTimeUtc,
        bool HasBonus,
        int HandInCount,
        Vec3Dto Position,
        MapCoordsDto? MapCoordinates,
        double Distance,
        double Radius,
        bool PlayerInsideRadius,
        bool IsOccupiedByPlayer,
        bool IsLevelSyncedByPlayer);

    public sealed record FatesDto(int Total, int Returned, bool Truncated, uint? CurrentFateId, List<FateDto> Fates);

    [McpTool("list_fates",
        Sources = ["dalamud:IFateTable"],
        Title = "List active FATEs",
        Description = "FATEs currently known in the player's zone, nearest first. Each entry: id, name, objective, level and maxLevel (the " +
                      "level sync cap), state (Preparing|Running|Ending|Ended|Failed|...), progressPercent, timeRemainingSeconds, durationSeconds, " +
                      "startTimeUtc, hasBonus (EXP bonus), handInCount (for item turn-in FATEs), position, mapCoordinates, distance from the " +
                      "player, radius, playerInsideRadius, isOccupiedByPlayer (the player is currently participating, i.e. it is the client's " +
                      "current FATE) and isLevelSyncedByPlayer. Also returns currentFateId. Use for 'any FATEs nearby', 'which FATE should I do'.")]
    public unsafe FatesDto ListFates(
        [McpParam("Maximum FATEs to return (1-100). Default 25.", Minimum = 1, Maximum = 100)] int limit = 25)
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        limit = Math.Clamp(limit, 1, 100);
        var origin = player.Position;
        var map = MapContext.Current(clientState, data);

        ushort currentFateId = 0;
        ushort syncedFateId = 0;
        var manager = CsFateManager.Instance();
        if (manager != null)
        {
            if (manager->CurrentFate != null)
            {
                currentFateId = manager->CurrentFate->FateId;
            }

            syncedFateId = manager->SyncedFateId;
        }

        var rows = new List<(float Distance, FateDto Dto)>();
        foreach (var fate in fates)
        {
            if (fate == null || fate.FateId == 0)
            {
                continue;
            }

            var position = fate.Position;
            var distance = GameMath.Distance3D(origin, position);
            var horizontal = GameMath.DistanceHorizontal(origin, position);
            var remaining = fate.TimeRemaining;
            rows.Add((distance, new FateDto(
                fate.FateId,
                fate.Name.TextValue,
                NullIfEmpty(fate.Objective.TextValue),
                fate.Level,
                fate.MaxLevel,
                StateName(fate.State),
                fate.Progress,
                remaining > 0 ? remaining : null,
                fate.Duration,
                fate.StartTimeEpoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(fate.StartTimeEpoch) : null,
                fate.HasBonus,
                fate.HandInCount,
                Snapshots.Vec(position),
                map.ToMap(position),
                GameMath.Round(distance, 1),
                GameMath.Round(fate.Radius, 1),
                fate.Radius > 0 && horizontal <= fate.Radius,
                currentFateId != 0 && fate.FateId == currentFateId,
                syncedFateId != 0 && fate.FateId == syncedFateId)));
        }

        rows.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        var page = rows.Take(limit).Select(r => r.Dto).ToList();
        return new FatesDto(rows.Count, page.Count, page.Count < rows.Count, currentFateId == 0 ? null : currentFateId, page);
    }

    private static string StateName(FateState state) => state switch
    {
        FateState.Preparing => "Preparing",
        FateState.Running => "Running",
        FateState.Ending => "Ending",
        FateState.Ended => "Ended",
        FateState.Failed => "Failed",
        _ => state.ToString(),
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

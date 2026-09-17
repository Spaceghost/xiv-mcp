using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Character;
using XivMcp.Plugin.Util;

namespace XivMcp.Plugin.Providers.World;

[McpProvider("world")]
public sealed class NearbyObjectsProvider
{
    private static readonly string[] KindValues =
    [
        "all", "player", "battleNpc", "enemy", "eventNpc", "treasure", "aetheryte", "gatheringPoint", "eventObj", "mount",
        "companion", "retainer", "areaObject", "housing", "cutscene", "reactionEventObj", "ornament", "cardStand", "followMount",
    ];

    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IDataManager data;

    public NearbyObjectsProvider(IObjectTable objects, IClientState clientState, IDataManager data)
    {
        this.objects = objects;
        this.clientState = clientState;
        this.data = data;
    }

    public sealed record NearbyObjectsDto(
        string Kind,
        double? Radius,
        int Total,
        int Offset,
        int Returned,
        bool Truncated,
        Vec3Dto Origin,
        List<ObjectSummaryDto> Objects);

    [McpTool("list_nearby_objects",
        Title = "List nearby objects",
        Description = "Game objects loaded around the player (the client only knows objects within roughly 100 yalms, fewer in crowded areas), " +
                      "sorted nearest first; the local player is excluded. Filter by kind: all, player, battleNpc (any combat NPC incl. pets/" +
                      "chocobos), enemy (hostile battleNpc only), eventNpc (quest/vendor NPCs), treasure (coffers), aetheryte, gatheringPoint, " +
                      "eventObj (interactables), mount, companion (minions), retainer, areaObject, housing, cutscene, reactionEventObj, ornament, " +
                      "cardStand, followMount. Each row: kind, subKind, name, entityId, gameObjectId (string), baseId, objectIndex, distance, " +
                      "horizontalDistance, position, mapCoordinates, isTargetable, and for characters isDead, level, hpPercent, isHostile, inCombat, " +
                      "isCasting, ownerEntityId; players also get job and world. Returns total matches, offset, returned and truncated. Use " +
                      "get_target for full detail on one object.")]
    public NearbyObjectsDto ListNearbyObjects(
        [McpParam("Object kind filter.", Enum = ["all", "player", "battleNpc", "enemy", "eventNpc", "treasure", "aetheryte", "gatheringPoint",
            "eventObj", "mount", "companion", "retainer", "areaObject", "housing", "cutscene", "reactionEventObj", "ornament", "cardStand",
            "followMount"])]
        string kind = "all",
        [McpParam("Maximum 3D distance in yalms from the player (1-1000). Default 50.", Minimum = 1, Maximum = 1000)] double radius = 50,
        [McpParam("Case-insensitive substring the object's name must contain.")] string? nameContains = null,
        [McpParam("Maximum rows to return (1-500). Default 25.", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Rows to skip, for paging (>= 0).", Minimum = 0)] int offset = 0,
        [McpParam("Only include objects that can currently be targeted.")] bool targetableOnly = false)
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        var normalizedKind = KindValues.FirstOrDefault(k => string.Equals(k, kind?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpToolException($"Unknown kind '{kind}'. Use one of: {string.Join(", ", KindValues)}.");
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var maxDistance = double.IsFinite(radius) ? Math.Clamp(radius, 1, 1000) : 50;
        var origin = player.Position;
        var playerAddress = player.Address;
        var map = MapContext.Current(clientState, data);

        var matches = new List<(float Distance, IGameObject Obj)>();
        foreach (var obj in objects)
        {
            if (obj == null || obj.Address == playerAddress || !Matches(obj, normalizedKind))
            {
                continue;
            }

            if (targetableOnly && !obj.IsTargetable)
            {
                continue;
            }

            var distance = GameMath.Distance3D(origin, obj.Position);
            if (!float.IsFinite(distance) || distance > maxDistance)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(nameContains) &&
                !obj.Name.TextValue.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add((distance, obj));
        }

        matches.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        var page = matches.Skip(offset).Take(limit).Select(m => Snapshots.Summary(m.Obj, origin, map)).ToList();
        return new NearbyObjectsDto(
            normalizedKind,
            GameMath.Round(maxDistance, 1),
            matches.Count,
            offset,
            page.Count,
            offset + page.Count < matches.Count,
            Snapshots.Vec(origin),
            page);
    }

    private static bool Matches(IGameObject obj, string kind) => kind switch
    {
        "all" => obj.ObjectKind != ObjectKind.None,
        "enemy" => obj.ObjectKind == ObjectKind.BattleNpc && obj is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Combatant } npc
                   && (npc.StatusFlags & StatusFlags.Hostile) != 0,
        _ => Snapshots.KindName(obj.ObjectKind) == kind,
    };
}

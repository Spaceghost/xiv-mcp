using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>Target and focus target selection.</summary>
[McpProvider("actions")]
public sealed class TargetProvider
{
    private const uint InvalidEntityId = 0xE0000000;

    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;

    public TargetProvider(ITargetManager targetManager, IObjectTable objectTable)
    {
        this.targetManager = targetManager;
        this.objectTable = objectTable;
    }

    [McpTool("set_target",
        Sources = ["dalamud:ITargetManager", "dalamud:IObjectTable"],
        ApprovalSummary = "Target {name} {entityId} {gameObjectId}.",
        Title = "Set target",
        Description =
            "Sets the user's current target, like clicking an object. Choose the object with exactly one of: entityId (number from object/party tools), gameObjectId (64-bit id as a decimal or 0x-hex string), or name (case-insensitive; exact name match preferred, otherwise substring; the nearest targetable match wins). " +
            "Only targetable objects in the local object table (roughly within 100 yalms) can be targeted. Returns the chosen object's entityId, gameObjectId, name, objectKind, distance (yalms from the player), matchedBy and, for name searches, how many objects matched. " +
            "This only selects a target; it never attacks or interacts.",
        Permission = ToolPermission.Action)]
    public TargetResult SetTarget(
        [McpParam("EntityId of the object.")] uint? entityId = null,
        [McpParam("GameObjectId of the object as a string (decimal or 0x-hex).")] string? gameObjectId = null,
        [McpParam("Name to search for among nearby targetable objects.")] string? name = null)
    {
        var (obj, matchedBy, candidates) = Resolve(entityId, gameObjectId, name);
        targetManager.Target = obj;
        return Describe(obj, matchedBy, candidates);
    }

    [McpTool("clear_target",
        Sources = ["dalamud:ITargetManager"],
        ApprovalSummary = "Clear your current target.",
        Title = "Clear target",
        Description = "Clears the user's current target (like pressing Escape on a target). Returns whether there was a target and its name.",
        Permission = ToolPermission.Action)]
    public ClearTargetResult ClearTarget()
    {
        var previous = targetManager.Target;
        var previousName = previous?.Name.TextValue;
        targetManager.Target = null;
        return new ClearTargetResult(previous != null, string.IsNullOrEmpty(previousName) ? null : previousName);
    }

    [McpTool("set_focus_target",
        Sources = ["dalamud:ITargetManager", "dalamud:IObjectTable"],
        ApprovalSummary = "Set your focus target to {name} {entityId} {gameObjectId} (clear: {clear}).",
        Title = "Set or clear focus target",
        Description =
            "Sets the user's focus target (the secondary tracked target shown in the Focus Target bar), or clears it with clear=true. " +
            "Select the object with exactly one of entityId, gameObjectId (string) or name (nearest targetable match), same rules as set_target. " +
            "Returns the focused object (or cleared=true).",
        Permission = ToolPermission.Action)]
    public FocusTargetResult SetFocusTarget(
        [McpParam("EntityId of the object.")] uint? entityId = null,
        [McpParam("GameObjectId of the object as a string (decimal or 0x-hex).")] string? gameObjectId = null,
        [McpParam("Name to search for among nearby targetable objects.")] string? name = null,
        [McpParam("Clear the focus target instead of setting it.")] bool clear = false)
    {
        if (clear)
        {
            if (entityId.HasValue || !string.IsNullOrWhiteSpace(gameObjectId) || !string.IsNullOrWhiteSpace(name))
                throw new McpToolException("Pass either clear=true or an object selector, not both.");
            var had = targetManager.FocusTarget != null;
            targetManager.FocusTarget = null;
            return new FocusTargetResult(true, had, null);
        }

        var (obj, matchedBy, candidates) = Resolve(entityId, gameObjectId, name);
        targetManager.FocusTarget = obj;
        return new FocusTargetResult(null, null, Describe(obj, matchedBy, candidates));
    }

    private (IGameObject Obj, string MatchedBy, int? Candidates) Resolve(uint? entityId, string? gameObjectId, string? name)
    {
        var selectors = (entityId.HasValue ? 1 : 0) + (string.IsNullOrWhiteSpace(gameObjectId) ? 0 : 1) + (string.IsNullOrWhiteSpace(name) ? 0 : 1);
        if (selectors != 1)
            throw new McpToolException("Pass exactly one of entityId, gameObjectId or name.");

        if (entityId is { } id)
        {
            if (id == 0 || id == InvalidEntityId)
                throw new McpToolException($"EntityId {id} is not a valid object id.");
            var byEntity = objectTable.SearchByEntityId(id) ?? throw new McpToolException($"No object with entityId {id} is nearby.");
            EnsureTargetable(byEntity);
            return (byEntity, "entityId", null);
        }

        if (!string.IsNullOrWhiteSpace(gameObjectId))
        {
            var text = gameObjectId.Trim();
            var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var goid)
                : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out goid);
            if (!ok || goid == 0)
                throw new McpToolException($"gameObjectId '{text}' is not a valid 64-bit id (decimal or 0x-hex string).");
            var byId = objectTable.SearchById(goid) ?? throw new McpToolException($"No object with gameObjectId {text} is nearby.");
            EnsureTargetable(byId);
            return (byId, "gameObjectId", null);
        }

        var query = name!.Trim();
        var origin = objectTable.LocalPlayer?.Position;
        IGameObject? bestExact = null, bestPartial = null;
        float bestExactDistance = float.MaxValue, bestPartialDistance = float.MaxValue;
        int exactCount = 0, partialCount = 0;

        foreach (var obj in objectTable)
        {
            if (obj == null || !obj.IsTargetable)
                continue;
            var objName = obj.Name.TextValue;
            if (string.IsNullOrEmpty(objName))
                continue;

            var distance = origin is { } o ? Vector3.Distance(o, obj.Position) : 0f;
            if (string.Equals(objName, query, StringComparison.OrdinalIgnoreCase))
            {
                exactCount++;
                if (distance < bestExactDistance)
                {
                    bestExact = obj;
                    bestExactDistance = distance;
                }
            }
            else if (objName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                partialCount++;
                if (distance < bestPartialDistance)
                {
                    bestPartial = obj;
                    bestPartialDistance = distance;
                }
            }
        }

        if (bestExact != null)
            return (bestExact, "name (exact, nearest)", exactCount);
        if (bestPartial != null)
            return (bestPartial, "name (partial, nearest)", partialCount);
        throw new McpToolException($"No targetable object named like '{query}' is nearby.");
    }

    private static void EnsureTargetable(IGameObject obj)
    {
        if (!obj.IsTargetable)
            throw new McpToolException($"'{obj.Name.TextValue}' cannot be targeted right now.");
    }

    private TargetResult Describe(IGameObject obj, string matchedBy, int? candidates)
    {
        var origin = objectTable.LocalPlayer?.Position;
        float? distance = origin is { } o ? MathF.Round(Vector3.Distance(o, obj.Position), 1) : null;
        return new TargetResult(
            obj.EntityId,
            obj.GameObjectId.ToString(CultureInfo.InvariantCulture),
            obj.Name.TextValue,
            obj.ObjectKind.ToString(),
            distance,
            matchedBy,
            candidates);
    }

    public sealed record TargetResult(uint EntityId, string GameObjectId, string Name, string ObjectKind, float? Distance, string MatchedBy, int? Candidates);

    public sealed record ClearTargetResult(bool HadTarget, string? PreviousName);

    public sealed record FocusTargetResult(bool? Cleared, bool? HadFocusTarget, TargetResult? Focus);
}

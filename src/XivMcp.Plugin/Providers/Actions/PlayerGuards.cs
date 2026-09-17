using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>Refuses state-changing actions while the character is busy (framework thread).</summary>
internal static class PlayerGuards
{
    private static readonly (ConditionFlag Flag, string Reason)[] Busy =
    [
        (ConditionFlag.InCombat, "in combat"),
        (ConditionFlag.Casting, "casting"),
        (ConditionFlag.Casting87, "casting"),
        (ConditionFlag.BetweenAreas, "changing zones"),
        (ConditionFlag.BetweenAreas51, "changing zones"),
        (ConditionFlag.WatchingCutscene, "watching a cutscene"),
        (ConditionFlag.WatchingCutscene78, "watching a cutscene"),
        (ConditionFlag.OccupiedInCutSceneEvent, "in a cutscene"),
        (ConditionFlag.OccupiedInEvent, "talking to an NPC or in an event"),
        (ConditionFlag.OccupiedInQuestEvent, "in a quest event"),
        (ConditionFlag.Occupied, "occupied"),
        (ConditionFlag.Crafting, "crafting"),
        (ConditionFlag.ExecutingCraftingAction, "crafting"),
        (ConditionFlag.Gathering, "gathering"),
        (ConditionFlag.ExecutingGatheringAction, "gathering"),
        (ConditionFlag.Fishing, "fishing"),
        (ConditionFlag.Unconscious, "knocked out"),
        (ConditionFlag.LoggingOut, "logging out"),
        (ConditionFlag.BeingMoved, "being moved"),
        (ConditionFlag.Jumping, "jumping"),
        (ConditionFlag.Jumping61, "jumping"),
        (ConditionFlag.Performing, "performing music"),
    ];

    private static readonly ConditionFlag[] DutyFlags = [ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95];

    public static void EnsureNotBusy(ICondition condition, string action)
    {
        foreach (var (flag, reason) in Busy)
        {
            if (condition[flag])
                throw new McpToolException($"Cannot {action} while {reason}. Try again when the character is idle.");
        }
    }

    public static bool IsBoundByDuty(ICondition condition) => DutyFlags.Any(f => condition[f]);
}

using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Character;

[McpProvider("character")]
public sealed class ConditionsProvider
{
    private static readonly Dictionary<int, string> FlagNames = BuildFlagNames();

    private readonly ICondition condition;
    private readonly IClientState clientState;

    public ConditionsProvider(ICondition condition, IClientState clientState)
    {
        this.condition = condition;
        this.clientState = clientState;
    }

    public sealed record ConditionsDto(
        bool LoggedIn,
        List<string> Active,
        bool InCombat,
        bool BoundByDuty,
        bool Mounted,
        bool InFlight,
        bool Swimming,
        bool Casting,
        bool Crafting,
        bool Gathering,
        bool Fishing,
        bool BetweenAreas,
        bool InCutscene,
        bool WatchingCutscene,
        bool OccupiedInQuestEvent,
        bool Occupied,
        bool Performing,
        bool Dead,
        bool InDutyQueue,
        bool IsClientIdle,
        string? IdleBlockedBy);

    [McpTool("get_conditions",
        Sources = ["dalamud:ICondition"],
        Title = "Get client conditions",
        RequiresLogin = false,
        Description = "The game's condition flags (what state the client is in). Returns active: the names of every currently set ConditionFlag " +
                      "(e.g. InCombat, Mounted, BoundByDuty, OccupiedInQuestEvent, WatchingCutscene, Crafting, InDutyQueue), plus convenience " +
                      "booleans that fold related flags together: inCombat, boundByDuty, mounted, inFlight, swimming, casting, crafting, gathering, " +
                      "fishing, betweenAreas (loading), inCutscene, watchingCutscene, occupiedInQuestEvent, occupied (any talk/menu/event lock), " +
                      "performing, dead, inDutyQueue, and isClientIdle with idleBlockedBy (the flag that blocks it). Check this before " +
                      "suggesting actions that need the character to be free (teleport, gearset change, crafting).")]
    public ConditionsDto GetConditions()
    {
        var active = new List<string>();
        foreach (var flag in condition.AsReadOnlySet())
        {
            var value = (int)flag;
            active.Add(FlagNames.TryGetValue(value, out var name) ? name : $"Unknown{value}");
        }

        active.Sort(StringComparer.Ordinal);
        var idle = clientState.IsClientIdle(out var blocking);

        bool Any(params ConditionFlag[] flags)
        {
            foreach (var flag in flags)
            {
                if (condition[flag])
                {
                    return true;
                }
            }

            return false;
        }

        return new ConditionsDto(
            clientState.IsLoggedIn,
            active,
            Any(ConditionFlag.InCombat),
            Any(ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95),
            Any(ConditionFlag.Mounted, ConditionFlag.RidingPillion),
            Any(ConditionFlag.InFlight),
            Any(ConditionFlag.Swimming, ConditionFlag.Diving),
            Any(ConditionFlag.Casting, ConditionFlag.Casting87),
            Any(ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.ExecutingCraftingAction),
            Any(ConditionFlag.Gathering, ConditionFlag.ExecutingGatheringAction),
            Any(ConditionFlag.Fishing),
            Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51),
            Any(ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78),
            Any(ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78),
            Any(ConditionFlag.OccupiedInQuestEvent),
            Any(ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
                ConditionFlag.Occupied33, ConditionFlag.Occupied38, ConditionFlag.Occupied39, ConditionFlag.OccupiedSummoningBell,
                ConditionFlag.OccupiedInCutSceneEvent),
            Any(ConditionFlag.Performing),
            Any(ConditionFlag.Unconscious),
            Any(ConditionFlag.InDutyQueue, ConditionFlag.WaitingForDuty, ConditionFlag.WaitingForDutyFinder),
            idle,
            idle ? null : FlagNames.GetValueOrDefault((int)blocking, blocking.ToString()));
    }

    private static Dictionary<int, string> BuildFlagNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var field in typeof(ConditionFlag).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetCustomAttribute<ObsoleteAttribute>() != null)
            {
                continue;
            }

            names.TryAdd((int)(ConditionFlag)field.GetValue(null)!, field.Name);
        }

        return names;
    }
}

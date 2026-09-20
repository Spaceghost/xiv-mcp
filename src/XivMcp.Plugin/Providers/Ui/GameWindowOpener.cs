using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Ui;

/// <summary>
/// The native half of open_game_window (framework thread only). Only functions that OPEN a window are called here:
/// AgentInterface.Show / FocusAddon and the agents' own Open* functions. Nothing in this file sends a callback to an
/// addon, so nothing inside a window is ever pressed.
/// </summary>
internal static unsafe class GameWindowOpener
{
    /// <returns>True when the window was already open and was only brought to the front.</returns>
    public static bool Open(GameWindowKind kind, uint? id, uint? mapTerritory)
    {
        if (id is { } rowId)
        {
            OpenOn(kind, rowId, mapTerritory ?? 0);
            return false;
        }

        var agent = Agent(AgentIdOf(kind), GameWindows.Name(kind));
        if (agent->IsAgentActive())
        {
            agent->FocusAddon();
            return true;
        }

        agent->Show();
        return false;
    }

    private static void OpenOn(GameWindowKind kind, uint id, uint mapTerritory)
    {
        switch (kind)
        {
            case GameWindowKind.Map:
                Ready(AgentMap.Instance(), kind)->OpenMapByMapId(id, mapTerritory);
                break;
            case GameWindowKind.Journal:
                Ready(AgentQuestJournal.Instance(), kind)->OpenForQuest(GameWindows.ClientQuestId(id), 1);
                break;
            case GameWindowKind.Recipe:
                Ready(AgentRecipeNote.Instance(), kind)->OpenRecipeByRecipeId(id);
                break;
            case GameWindowKind.RecipeSearch:
                Ready(AgentRecipeNote.Instance(), kind)->SearchRecipeByItemId(id);
                break;
            case GameWindowKind.GatheringLog:
                Ready(AgentGatheringNote.Instance(), kind)->OpenGatherableByItemId((ushort)id);
                break;
            case GameWindowKind.DutyFinder:
                Ready(AgentContentsFinder.Instance(), kind)->OpenRegularDuty(id);
                break;
            case GameWindowKind.Achievements:
                Ready(AgentAchievement.Instance(), kind)->OpenById(id);
                break;
            default:
                throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"{GameWindows.Name(kind)} does not take an id.");
        }
    }

    private static AgentId AgentIdOf(GameWindowKind kind) => kind switch
    {
        GameWindowKind.Map => AgentId.Map,
        GameWindowKind.Journal => AgentId.QuestJournal,
        GameWindowKind.Recipe or GameWindowKind.RecipeSearch => AgentId.RecipeNote,
        GameWindowKind.GatheringLog => AgentId.GatheringNote,
        GameWindowKind.Teleport => AgentId.Teleport,
        GameWindowKind.Character => AgentId.Status,
        GameWindowKind.Armoury => AgentId.ArmouryBoard,
        GameWindowKind.Inventory => AgentId.Inventory,
        GameWindowKind.DutyFinder => AgentId.ContentsFinder,
        GameWindowKind.Achievements => AgentId.Achievement,
        GameWindowKind.Currency => AgentId.Currency,
        GameWindowKind.Gearsets => AgentId.GearSet,
        GameWindowKind.Macros => AgentId.Macro,
        _ => throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"Unknown window '{kind}'."),
    };

    private static AgentInterface* Agent(AgentId id, string name)
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(id);
        if (agent == null)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"The game's {name} window is not available yet; try again once the character is fully loaded.", retryable: true);
        return agent;
    }

    private static T* Ready<T>(T* agent, GameWindowKind kind)
        where T : unmanaged
    {
        if (agent == null)
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"The game's {GameWindows.Name(kind)} window is not available yet; try again once the character is fully loaded.", retryable: true);
        return agent;
    }
}

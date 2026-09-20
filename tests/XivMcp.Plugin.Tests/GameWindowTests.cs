using System.Reflection;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Actions;
using XivMcp.Plugin.Providers.Ui;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>open_game_window: the window table and the argument rules.</summary>
public class GameWindowTests
{
    private static McpToolException Rejects(Action call) => Assert.Throws<McpToolException>(call);

    [Fact]
    public void EveryKindHasExactlyOneRowAndOnlyOpeningFunctions()
    {
        Assert.Equal(Enum.GetValues<GameWindowKind>().Order(), GameWindows.All.Select(s => s.Kind).Order());
        foreach (var spec in GameWindows.All)
        {
            Assert.Equal("AgentInterface.Show", spec.OpenApi);
            Assert.Equal(spec.IdKind == GameWindowIdKind.None, spec.IdApi is null);
            if (spec.IdApi is { } api)
            {
                var function = api[(api.IndexOf('.', StringComparison.Ordinal) + 1)..];
                Assert.True(function.StartsWith("Open", StringComparison.Ordinal) || function.StartsWith("Search", StringComparison.Ordinal), api);
            }

            Assert.DoesNotContain("Callback", spec.IdApi ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(GameWindowKind.Map, "Map", "AgentMap.OpenMapByMapId")]
    [InlineData(GameWindowKind.Journal, "QuestJournal", "AgentQuestJournal.OpenForQuest")]
    [InlineData(GameWindowKind.Recipe, "RecipeNote", "AgentRecipeNote.OpenRecipeByRecipeId")]
    [InlineData(GameWindowKind.RecipeSearch, "RecipeNote", "AgentRecipeNote.SearchRecipeByItemId")]
    [InlineData(GameWindowKind.GatheringLog, "GatheringNote", "AgentGatheringNote.OpenGatherableByItemId")]
    [InlineData(GameWindowKind.DutyFinder, "ContentsFinder", "AgentContentsFinder.OpenRegularDuty")]
    [InlineData(GameWindowKind.Achievements, "Achievement", "AgentAchievement.OpenById")]
    [InlineData(GameWindowKind.Teleport, "Teleport", null)]
    [InlineData(GameWindowKind.Character, "Status", null)]
    [InlineData(GameWindowKind.Armoury, "ArmouryBoard", null)]
    [InlineData(GameWindowKind.Inventory, "Inventory", null)]
    [InlineData(GameWindowKind.Currency, "Currency", null)]
    [InlineData(GameWindowKind.Gearsets, "GearSet", null)]
    [InlineData(GameWindowKind.Macros, "Macro", null)]
    public void Table(GameWindowKind kind, string agent, string? idApi)
    {
        var spec = GameWindows.Spec(kind);
        Assert.Equal(agent, spec.Agent);
        Assert.Equal(idApi, spec.IdApi);
    }

    [Fact]
    public void ArgumentRules()
    {
        Assert.Equal(GameWindowKind.Teleport, GameWindows.Validate(GameWindowKind.Teleport, null, null).Kind);
        Assert.Equal(GameWindowKind.Map, GameWindows.Validate(GameWindowKind.Map, null, 132).Kind);
        Assert.Equal(GameWindowKind.Map, GameWindows.Validate(GameWindowKind.Map, 2, null).Kind);

        Assert.Contains("does not take an id", Rejects(() => GameWindows.Validate(GameWindowKind.Inventory, 5, null)).Message, StringComparison.Ordinal);
        Assert.Contains("only used with kind=map", Rejects(() => GameWindows.Validate(GameWindowKind.Journal, null, 132)).Message, StringComparison.Ordinal);
        Assert.Contains("not both", Rejects(() => GameWindows.Validate(GameWindowKind.Map, 2, 132)).Message, StringComparison.Ordinal);
        Assert.Contains("needs id", Rejects(() => GameWindows.Validate(GameWindowKind.RecipeSearch, null, null)).Message, StringComparison.Ordinal);
        Assert.Contains("id 0", Rejects(() => GameWindows.Validate(GameWindowKind.Recipe, 0, null)).Message, StringComparison.Ordinal);
        Assert.Contains("16-bit", Rejects(() => GameWindows.Validate(GameWindowKind.GatheringLog, 70000, null)).Message, StringComparison.Ordinal);
        Assert.All(
            new Action[] { () => GameWindows.Validate(GameWindowKind.Inventory, 5, null), () => GameWindows.Validate(GameWindowKind.Map, 2, 132) },
            call => Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(call).Code));
    }

    [Fact]
    public void QuestIdsLoseTheirSheetBase()
    {
        Assert.Equal(1u, GameWindows.ClientQuestId(65537));
        Assert.Equal(4521u, GameWindows.ClientQuestId(65536 + 4521));
    }

    [Fact]
    public void ProviderRejectsBadArgumentsBeforeAnythingElse()
    {
        var provider = new GameWindowProvider(FakeProxy.Create<IDataManager>(), FakeProxy.Create<ICondition>());
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => provider.OpenGameWindow(GameWindowKind.Currency, id: 3)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => provider.OpenGameWindow(GameWindowKind.RecipeSearch)).Code);
    }

    [Fact]
    public void ApprovalSentencesReadWellWithOneSelector()
    {
        var gearset = typeof(GearsetProvider).GetMethod(nameof(GearsetProvider.EquipGearset))!.GetCustomAttribute<McpToolAttribute>()!.ApprovalSummary;
        Assert.Equal(
            "Change to your gear set number (default) / named Tank (only one of the two is given); this can also change your job.",
            McpServer.RenderApprovalSummary(gearset, "equip_gearset", new JsonObject { ["name"] = "Tank" }));
        Assert.Equal(
            "Change to your gear set number 4 / named (default) (only one of the two is given); this can also change your job.",
            McpServer.RenderApprovalSummary(gearset, "equip_gearset", new JsonObject { ["id"] = 4 }));
    }
}

using System.Reflection;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Providers.Prompts;

namespace XivMcp.Plugin.Tests;

/// <summary>Contract and pure-logic checks for the static planning tools (sources, zones, roulettes) and their prompts.</summary>
public class StaticDataToolsTests
{
    // CA1861: array arguments in assertions are hoisted so they are allocated once.
    private static readonly int[] FirstPage = [1, 2, 3];
    private static readonly int[] LastPage = [7];
    private static readonly int[] SingleItem = [1];
    private static readonly int[] CutSection = [5, 6];
    private static readonly int[] WholeSection = [5, 6, 7];
    private static readonly string[] PlanningPromptNames = ["plan_daily_reset", "weather_hunt", "what_do_i_need_to_craft", "where_do_i_get"];

    private static readonly string[] NewTools =
    [
        "get_recipe_tree", "get_item_sources", "get_item_uses", "search_zones", "get_zone_info",
        "find_weather_windows", "get_duty_unlock", "list_roulettes",
    ];

    private static readonly Type[] Providers =
    [
        typeof(RecipeTreeProvider), typeof(ItemSourcesProvider), typeof(ZoneDataProvider), typeof(DutyUnlockProvider),
        typeof(RecipeDataProvider), typeof(QuestDataProvider), typeof(DutyDataProvider),
    ];

    [Fact]
    public void EveryNewToolIsAStaticReadToolWithSources()
    {
        var tools = Providers.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpToolAttribute>())
            .Where(a => a != null)
            .ToDictionary(a => a!.Name, a => a!);

        foreach (var name in NewTools)
        {
            Assert.True(tools.TryGetValue(name, out var tool), $"{name} is not declared");
            Assert.Equal(ToolAvailability.Static, tool!.Availability);
            Assert.Equal(ToolPermission.Read, tool.Permission);
            Assert.False(tool.GameThread, $"{name} must not need the framework thread");
            Assert.False(tool.RequiresLogin, $"{name} must work at the title screen");
            Assert.False(tool.NeedsApproval);
            Assert.False(tool.Destructive);
            Assert.NotNull(tool.Sources);
            Assert.NotEmpty(tool.Sources!);
            Assert.All(tool.Sources!, s => Assert.True(s.StartsWith("lumina:", StringComparison.Ordinal) || s == "clock:host", s));
            Assert.False(string.IsNullOrWhiteSpace(tool.Title));
            Assert.True(tool.Description.Length > 200, $"{name} needs a real description");
        }
    }

    [Fact]
    public void StaticProvidersTakeOnlyTheGameDataSource()
    {
        foreach (var provider in Providers)
        {
            var parameters = provider.GetConstructors().Single().GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(IGameDataSource), parameters[0].ParameterType);
        }
    }

    [Theory]
    [InlineData("ffxiv://quest/{questId}")]
    [InlineData("ffxiv://recipe/{recipeId}")]
    [InlineData("ffxiv://zone/{territoryId}")]
    [InlineData("ffxiv://duty/{dutyId}")]
    public void ResourceTemplatesBindTheirVariableAndNeedNoLogin(string template)
    {
        var (method, attribute) = Providers.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpResourceTemplateAttribute>()))
            .Single(x => x.Attribute?.UriTemplate == template);
        Assert.False(attribute!.RequiresLogin);
        Assert.False(attribute.GameThread);
        var variable = template[(template.IndexOf('{', StringComparison.Ordinal) + 1)..^1];
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal(variable, parameter.Name);
    }

    [Fact]
    public void SourceKindsComeInPreferenceOrderAndFallBackToOther()
    {
        Assert.Equal(new[] { SourceMerge.Other }, SourceMerge.Kinds(default(SourceMerge.Facts)));
        Assert.Equal(
            new[]
            {
                SourceMerge.GilVendor, SourceMerge.Gathering, SourceMerge.Crafting, SourceMerge.SpecialShop, SourceMerge.GcSeals,
                SourceMerge.RetainerVenture, SourceMerge.Quest, SourceMerge.Achievement,
            },
            SourceMerge.Kinds(new SourceMerge.Facts(true, true, 2, 1, true, 1, 3, 1)));
        Assert.Equal(new[] { SourceMerge.Gathering, SourceMerge.Quest }, SourceMerge.Kinds(new SourceMerge.Facts(Gatherable: true, Quests: 1)));
    }

    [Fact]
    public void GilTotalsOnlyForVendorItems()
    {
        Assert.Equal(3_000_000_000L, SourceMerge.GilTotal(true, 300_000, 10_000));
        Assert.Null(SourceMerge.GilTotal(false, 50, 10));
        Assert.Null(SourceMerge.GilTotal(true, 0, 10));
    }

    [Fact]
    public void SectionsReportTotalsAndTruncation()
    {
        var all = Enumerable.Range(1, 7).ToList();
        var first = SourceMerge.Slice(all, 0, 3);
        Assert.Equal((7, true), (first.Total, first.Truncated));
        Assert.Equal(FirstPage, first.Results);

        var last = SourceMerge.Slice(all, 6, 3);
        Assert.Equal(LastPage, last.Results);
        Assert.False(last.Truncated);

        Assert.Empty(SourceMerge.Slice(all, 50, 3).Results);
        Assert.Equal(SingleItem, SourceMerge.Slice(all, -5, 0).Results);

        // A page cut by the lookup itself: 2 shown from offset 4 of 7 leaves one more.
        Assert.True(SourceMerge.Section(CutSection, 7, 4).Truncated);
        Assert.False(SourceMerge.Section(WholeSection, 7, 4).Truncated);
        Assert.False(SourceMerge.Section(Array.Empty<int>(), 0, 0).Truncated);
    }

    [Fact]
    public void RouletteColumnsMatchRoulettesByEnglishName()
    {
        RouletteMatch.Roulette[] roulettes =
        [
            new(1, "Duty Roulette: Leveling"),
            new(2, "Duty Roulette: High-level Dungeons"),
            new(3, "Duty Roulette: Main Scenario"),
            new(5, "Duty Roulette: Expert"),
            new(7, "Daily Challenge: Frontline"),
            new(15, "Duty Roulette: Alliance Raids"),
            new(17, "Duty Roulette: Normal Raids"),
            new(30, "Duty Roulette: Expert (copy)"),
        ];

        Assert.Equal(1u, RouletteMatch.Find("LevelingRoulette", roulettes));
        Assert.Equal(2u, RouletteMatch.Find("HighLevelRoulette", roulettes));
        Assert.Equal(3u, RouletteMatch.Find("MSQRoulette", roulettes));
        Assert.Equal(5u, RouletteMatch.Find("ExpertRoulette", roulettes));
        Assert.Equal(7u, RouletteMatch.Find("DailyFrontlineChallenge", roulettes));
        Assert.Equal(15u, RouletteMatch.Find("AllianceRoulette", roulettes));
        Assert.Equal(17u, RouletteMatch.Find("NormalRaidRoulette", roulettes));
        Assert.Null(RouletteMatch.Find("MentorRoulette", roulettes));
        Assert.Null(RouletteMatch.Find("FeastTeamRoulette", roulettes));
        Assert.Null(RouletteMatch.Find("LevelingRoulette", []));
    }

    [Fact]
    public void EveryRouletteFlagColumnIsKnownToTheDutyProvider() =>
        Assert.Contains(DutyDataProvider.RouletteColumns, p => p.Name == "LevelingRoulette");

    [Theory]
    [InlineData(0u, 0u, false, "city")]
    [InlineData(1u, 0u, false, "field")]
    [InlineData(2u, 0u, false, "inn")]
    [InlineData(13u, 0u, false, "housing")]
    [InlineData(3u, 4u, false, "duty")]
    [InlineData(99u, 4u, false, "duty")]
    [InlineData(99u, 0u, false, "other")]
    [InlineData(1u, 0u, true, "pvp")]
    public void ZoneKindIsCoarseAndTotal(uint intendedUse, uint duty, bool pvp, string expected)
    {
        Assert.Equal(expected, ZoneLookup.KindOf(intendedUse, duty, pvp));
        Assert.Contains(expected, ZoneLookup.Kinds);
    }

    private static GameDataIndex.ZoneEntry Zone(uint id, string name, uint use = 1, uint duty = 0, bool weather = true) =>
        new(id, name, name.ToLowerInvariant(), "Region", use, duty, false, weather, 0);

    [Fact]
    public void ZoneNamesResolveToTheOpenWorldCopyAndAmbiguityIsReported()
    {
        GameDataIndex.ZoneEntry[] zones =
        [
            Zone(900, "Eastern La Noscea", use: 9, weather: false),
            Zone(137, "Eastern La Noscea"),
            Zone(138, "Western La Noscea"),
            Zone(1000, "Western La Noscea", use: 3, duty: 12),
            Zone(128, "Limsa Lominsa Upper Decks", use: 0),
            Zone(129, "Limsa Lominsa Lower Decks", use: 0),
        ];

        Assert.Equal(137u, ZoneLookup.Resolve(zones, "eastern la noscea").Match!.Id);
        Assert.Equal(137u, ZoneLookup.Resolve(zones, "  EASTERN  ").Match!.Id);
        Assert.Equal(138u, ZoneLookup.Resolve(zones, "Western La Noscea").Match!.Id);
        Assert.Equal(900u, ZoneLookup.Resolve(zones, "900").Match!.Id);
        Assert.Equal(900u, ZoneLookup.Resolve(zones, "Eastern La Noscea", z => !z.HasWeather).Match!.Id);

        var ambiguous = ZoneLookup.Resolve(zones, "limsa");
        Assert.Null(ambiguous.Match);
        Assert.Equal(2, ambiguous.Candidates.Count);

        var none = ZoneLookup.Resolve(zones, "Ishgard");
        Assert.Null(none.Match);
        Assert.Empty(none.Candidates);
        Assert.Null(ZoneLookup.Resolve(zones, "4242").Match);
        Assert.Null(ZoneLookup.Resolve(zones, "  ").Match);
    }

    [Fact]
    public void PlanningPromptsOrchestrateToolsAndNeverAutomate()
    {
        var prompts = new PlanningPromptsProvider();
        var texts = new[]
        {
            prompts.PlanDailyReset("90"),
            prompts.WhatDoINeedToCraft("Grade 8 Tincture", "3"),
            prompts.WhereDoIGet("Cobalt Ore"),
            prompts.WeatherHunt("Eastern La Noscea", "Rain", "Clear Skies", "18-6"),
        }.Select(p => Assert.Single(p.Messages).Content.Text ?? "").ToArray();

        Assert.All(texts, t =>
        {
            Assert.Contains("get_server_info", t, StringComparison.Ordinal);
            Assert.Contains("standalone", t, StringComparison.Ordinal);
            Assert.Contains("never gather, craft, buy, travel or queue", t, StringComparison.Ordinal);
        });
        Assert.Contains("get_roulette_status", texts[0], StringComparison.Ordinal);
        Assert.Contains("90 minutes", texts[0], StringComparison.Ordinal);
        Assert.Contains("get_recipe_tree", texts[1], StringComparison.Ordinal);
        Assert.Contains("3 × Grade 8 Tincture", texts[1], StringComparison.Ordinal);
        Assert.Contains("get_item_sources", texts[2], StringComparison.Ordinal);
        Assert.Contains("find_weather_windows", texts[3], StringComparison.Ordinal);
        Assert.Contains("post_objective", texts[3], StringComparison.Ordinal);
        Assert.Contains("right after Clear Skies", texts[3], StringComparison.Ordinal);

        var names = typeof(PlanningPromptsProvider).GetMethods().Select(m => m.GetCustomAttribute<McpPromptAttribute>()?.Name).OfType<string>();
        Assert.Equal(PlanningPromptNames, names.Order(StringComparer.Ordinal).ToArray());
    }
}

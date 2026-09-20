using XivMcp.Core;
using XivMcp.Plugin.Providers.Actions;
using XivMcp.Plugin.Providers.Character;
using XivMcp.Plugin.Providers.Duty;
using XivMcp.Plugin.Providers.Inventory;
using XivMcp.Plugin.Providers.Party;
using XivMcp.Plugin.Providers.World;

namespace XivMcp.Plugin.Tests;

/// <summary>The pure halves of the live read tools, driven with hand-made records: no game, no Dalamud services.</summary>
public class QuestJournalShaperTests
{
    private static readonly QuestStaticInfo Info = new(
        "Coming to Gridania",
        1,
        "Seventh Umbral Era",
        "Main Scenario",
        false,
        [new QuestTodo(0, 1, "Speak with Mother Miounne."), new QuestTodo(1, 2, "Visit the Conjurers' Guild."), new QuestTodo(2, 2, "Visit the Lancers' Guild."), new QuestTodo(3, 255, "Report to Mother Miounne.")]);

    private static QuestStaticInfo? Lookup(uint id) => id switch
    {
        65575 => Info,
        65576 => Info with { Name = "Close to Home", Todos = [] },
        _ => null,
    };

    [Theory]
    [InlineData("SubFst010_00039", "quest/000/SubFst010_00039")]
    [InlineData("ManFst004_00124", "quest/001/ManFst004_00124")]
    [InlineData(" AktKmh105_04412 ", "quest/044/AktKmh105_04412")]
    public void BuildsTheTextSheetPath(string key, string expected) => Assert.Equal(expected, QuestJournalShaper.TextSheetPath(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NoNumber")]
    [InlineData("Short_12")]
    [InlineData("Bad_12a45")]
    public void RejectsKeysWithoutAQuestNumber(string? key) => Assert.Null(QuestJournalShaper.TextSheetPath(key));

    [Theory]
    [InlineData("TEXT_SUBFST010_00039_TODO_00", 0)]
    [InlineData("TEXT_SUBFST010_00039_TODO_12", 12)]
    [InlineData("text_x_00001_todo_03", 3)]
    public void ReadsTodoIndexes(string key, int expected) => Assert.Equal(expected, QuestJournalShaper.TodoIndex(key));

    [Theory]
    [InlineData("TEXT_SUBFST010_00039_SEQ_00")]
    [InlineData("TEXT_SUBFST010_00039_TODO_")]
    [InlineData("TEXT_SUBFST010_00039_TODO_AB")]
    [InlineData("TEXT_SUBFST010_00039_TODO_1234")]
    [InlineData(null)]
    public void IgnoresOtherTextKeys(string? key) => Assert.Null(QuestJournalShaper.TodoIndex(key));

    [Fact]
    public void PicksEveryObjectiveOfTheCurrentStep()
    {
        Assert.Equal(["Visit the Conjurers' Guild.", "Visit the Lancers' Guild."], QuestJournalShaper.Objectives(Info, 2));
        Assert.Equal(["Report to Mother Miounne."], QuestJournalShaper.Objectives(Info, 255));
        Assert.Empty(QuestJournalShaper.Objectives(Info, 7));
    }

    [Fact]
    public void CapsObjectiveTextAndDropsDuplicates()
    {
        var info = Info with { Todos = [new QuestTodo(0, 1, new string('x', 900)), new QuestTodo(1, 1, "Same."), new QuestTodo(2, 1, " Same. "), new QuestTodo(3, 1, null)] };
        var objectives = QuestJournalShaper.Objectives(info, 1);
        Assert.Equal(2, objectives.Count);
        Assert.Equal(QuestJournalShaper.MaxObjectiveLength + 3, objectives[0].Length);
    }

    [Fact]
    public void ShapesAQuestWithIdsNamesAndFlags()
    {
        var quest = QuestJournalShaper.Shape(new QuestWorkRecord(39, 255, true, false, true, 5, true, true), Info, job => job == 5 ? "Archer" : null);
        Assert.Equal(65575u, quest.QuestId);
        Assert.True(quest.ReadyToComplete);
        Assert.True(quest.ObjectiveResolved);
        Assert.Equal("Main Scenario", quest.Category);
        Assert.True(quest.Priority);
        Assert.True(quest.Tracked);
        Assert.Null(quest.Hidden);
        Assert.True(quest.Daily);
        Assert.True(quest.DailyCompleted);
        Assert.Equal("Archer", quest.AcceptedAs);
    }

    [Fact]
    public void SaysSoWhenTheObjectiveCannotBeResolved()
    {
        var unknown = QuestJournalShaper.Shape(new QuestWorkRecord(999, 3, false, false, false, 0), null, _ => null);
        Assert.Null(unknown.Name);
        Assert.Null(unknown.Objectives);
        Assert.False(unknown.ObjectiveResolved);
        Assert.Equal(3, unknown.Sequence);
        Assert.Null(unknown.Daily);
        Assert.Null(unknown.DailyCompleted);
        Assert.Null(unknown.AcceptedAs);
    }

    [Fact]
    public void FiltersSortsAndPages()
    {
        QuestWorkRecord[] records =
        [
            new(40, 1, false, false, false, 0),
            new(39, 2, false, false, true, 0),
            new(0, 9, true, false, true, 0),
            new(500, 1, false, true, false, 0),
        ];

        var all = QuestJournalShaper.Build(records, Lookup, _ => null, null, false, true, null, 50, 0);
        Assert.Equal(3, all.Total);
        Assert.Equal([65575u, 66036u, 65576u], all.Quests.Select(q => q.QuestId));
        Assert.False(all.Truncated);

        Assert.Equal(2, QuestJournalShaper.Build(records, Lookup, _ => null, null, false, false, null, 50, 0).Total);
        Assert.Equal([65575u], QuestJournalShaper.Build(records, Lookup, _ => null, null, true, true, null, 50, 0).Quests.Select(q => q.QuestId));
        Assert.Equal([65576u], QuestJournalShaper.Build(records, Lookup, _ => null, null, false, true, "HOME", 50, 0).Quests.Select(q => q.QuestId));

        var page = QuestJournalShaper.Build(records, Lookup, _ => null, null, false, true, null, 1, 1);
        Assert.Equal(1, page.Returned);
        Assert.Equal(1, page.Offset);
        Assert.True(page.Truncated);

        var clamped = QuestJournalShaper.Build(records, Lookup, _ => null, null, false, true, null, 100000, -5);
        Assert.Equal(0, clamped.Offset);
        Assert.Equal(3, clamped.Returned);
    }
}

public class HotbarShaperTests
{
    private static string? Names(string kind, uint id) => (kind, id) switch
    {
        ("action", 7) => "Cure",
        ("action", 8) => "Cure II",
        ("generalAction", 4) => "Sprint",
        ("macro", 3) => "Greeting",
        ("macro", 258) => "Shared one",
        _ => null,
    };

    private static HotbarSlotRecord Slot(int slot, string type, uint id, string? apparentType = null, uint? apparentId = null, string? popUp = null, string? key = null) =>
        new(slot, type, id, apparentType ?? type, apparentId ?? id, 405, popUp, key);

    [Theory]
    [InlineData("Action", "action")]
    [InlineData("GeneralAction", "generalAction")]
    [InlineData("GearSet", "gearset")]
    [InlineData("Macro", "macro")]
    [InlineData("PvPQuickChat", "pvpQuickChat")]
    [InlineData("McGuffin", "collectionItem")]
    [InlineData("Unknown23", "unknown")]
    [InlineData("37", "unknown")]
    [InlineData("Empty", "empty")]
    [InlineData("", "empty")]
    [InlineData(null, "empty")]
    public void MapsSlotTypesToKinds(string? type, string expected) => Assert.Equal(expected, HotbarShaper.Kind(type));

    [Fact]
    public void DecodesMacroNumbers()
    {
        Assert.Equal(("individual", 0), HotbarShaper.Macro(0));
        Assert.Equal(("individual", 99), HotbarShaper.Macro(99));
        Assert.Equal(("shared", 0), HotbarShaper.Macro(256));
        Assert.Equal(("shared", 99), HotbarShaper.Macro(355));
        Assert.Null(HotbarShaper.Macro(100));
        Assert.Null(HotbarShaper.Macro(356));
    }

    [Theory]
    [InlineData("Sprint [Ctrl+1]", "Ctrl+1", "Sprint")]
    [InlineData("Sprint [Ctrl+1]", null, "Sprint")]
    [InlineData("Sprint", null, "Sprint")]
    [InlineData("  ", "1", null)]
    [InlineData(null, "1", null)]
    public void TakesTheNameOutOfTheHoverText(string? popUp, string? key, string? expected) =>
        Assert.Equal(expected, HotbarShaper.NameFromPopUp(popUp, key));

    [Fact]
    public void ShapesAssignedSlots()
    {
        var action = HotbarShaper.Shape(Slot(1, "Action", 7, key: " 1 "), Names);
        Assert.Equal(("action", 7u, "Cure", "1"), (action.Kind, action.Id, action.Name, action.Keybind));
        Assert.Equal(405u, action.IconId);
        Assert.Null(action.ShownAsKind);
        Assert.Null(action.MacroSet);

        var macro = HotbarShaper.Shape(Slot(2, "Macro", 258), Names);
        Assert.Equal(("macro", "Shared one", "shared", 2), (macro.Kind, macro.Name, macro.MacroSet, macro.MacroIndex));

        var unnamed = HotbarShaper.Shape(Slot(3, "Emote", 12, popUp: "Wave [2]", key: "2"), Names);
        Assert.Equal("Wave", unnamed.Name);
    }

    [Fact]
    public void ReportsWhatASlotCurrentlyShowsWhenItDiffers()
    {
        var upgraded = HotbarShaper.Shape(Slot(1, "Action", 7, "Action", 8), Names);
        Assert.Equal("Cure", upgraded.Name);
        Assert.Equal(("action", 8u, "Cure II"), (upgraded.ShownAsKind, upgraded.ShownAsId, upgraded.ShownAsName));
    }

    [Fact]
    public void EmptySlotsCarryOnlyTheirKeybind()
    {
        var empty = HotbarShaper.Shape(Slot(4, "Empty", 0, key: "4"), Names);
        Assert.Equal(new HotbarSlotDto(4, "empty", 0, null, "4", null, null, null, null, null, null), empty);
        Assert.Equal("empty", HotbarShaper.Shape(Slot(5, "Action", 0), Names).Kind);
    }

    [Fact]
    public void SelectsBarsByKindAndNumber()
    {
        Assert.Equal(10, HotbarShaper.Select(null, null).Count);
        Assert.Equal([("standard", 3, 2)], HotbarShaper.Select("Standard", 3));
        Assert.Equal([("cross", 8, 17)], HotbarShaper.Select("cross", 8));
        Assert.Equal([("pet", 1, -1)], HotbarShaper.Select("pet", null));
        var all = HotbarShaper.Select("all", null);
        Assert.Equal(20, all.Count);
        Assert.Equal(("petCross", 1, -2), all[^1]);
    }

    [Theory]
    [InlineData("standard", 11)]
    [InlineData("cross", 9)]
    [InlineData("all", 1)]
    [InlineData("pet", 1)]
    [InlineData("sideways", null)]
    public void RejectsBadBarSelections(string bars, int? number)
    {
        var ex = Assert.Throws<McpToolException>(() => HotbarShaper.Select(bars, number));
        Assert.Equal(McpErrorCodes.InvalidArguments, ex.Code);
    }

    [Fact]
    public void ShapesABarAndHidesCrossOnlySlotsOnStandardBars()
    {
        var slots = Enumerable.Range(1, 16).Select(i => i is 1 or 14 ? Slot(i, "GeneralAction", 4) : Slot(i, "Empty", 0)).ToList();
        var standard = HotbarShaper.ShapeBar("standard", 1, new HotbarRecord(0, true, slots), false, Names);
        Assert.Equal((12, 1, 1), (standard.SlotCount, standard.Used, standard.Slots.Count));
        Assert.True(standard.SharedAcrossJobs);

        var cross = HotbarShaper.ShapeBar("cross", 1, new HotbarRecord(10, false, slots), true, Names);
        Assert.Equal((16, 2, 16), (cross.SlotCount, cross.Used, cross.Slots.Count));
    }
}

public class DutyUnlockShaperTests
{
    private static readonly DutyUnlockRecord[] Records =
    [
        new(4, "Sastasha", 2, "Dungeons", 15, 0, "instanceContent", 4, true, true),
        new(5, "The Tam-Tara Deepcroft", 2, "Dungeons", 16, 0, "instanceContent", 5, true, false),
        new(30, "The Aurum Vale", 2, "Dungeons", 47, 0, "instanceContent", 30, false, false),
        new(56, "The Bowl of Embers", 4, "Trials", 20, 0, "instanceContent", 56, true, true),
        new(900, "Some Field Operation", 26, "Field Operations", 70, 300, "other", 0, null, null),
    ];

    [Fact]
    public void CountsOnlyDutiesTheClientHasFlagsFor()
    {
        var result = DutyUnlockShaper.Build(Records, null, 0, 0, false, false, null, 50, 0);
        Assert.Equal(5, result.Total);
        Assert.Equal((4, 3, 2), (result.Known, result.Unlocked, result.Completed));
        Assert.Equal(["Dungeons", "Trials"], result.ByContentType.Select(c => c.ContentType));
        Assert.Equal(new DutyUnlockCount("Dungeons", 3, 2, 1), result.ByContentType[0]);
        var field = result.Duties.Single(d => d.DutyId == 900);
        Assert.Null(field.Unlocked);
        Assert.Equal(300, field.ItemLevel);
        Assert.Null(result.Duties[0].ItemLevel);
    }

    [Fact]
    public void FiltersByTypeLevelNameAndState()
    {
        Assert.Equal([56u], DutyUnlockShaper.Build(Records, "trial", 0, 0, false, false, null, 50, 0).Duties.Select(d => d.DutyId));
        Assert.Equal([56u], DutyUnlockShaper.Build(Records, "4", 0, 0, false, false, null, 50, 0).Duties.Select(d => d.DutyId));
        Assert.Equal([5u, 56u], DutyUnlockShaper.Build(Records, null, 16, 20, false, false, null, 50, 0).Duties.Select(d => d.DutyId));
        Assert.Equal([30u], DutyUnlockShaper.Build(Records, null, 0, 0, true, false, null, 50, 0).Duties.Select(d => d.DutyId));
        Assert.Equal([5u], DutyUnlockShaper.Build(Records, null, 0, 0, false, true, null, 50, 0).Duties.Select(d => d.DutyId));
        Assert.Equal([30u], DutyUnlockShaper.Build(Records, null, 0, 0, false, false, "aurum", 50, 0).Duties.Select(d => d.DutyId));
    }

    [Fact]
    public void KeepsScopeCountsWhenTheListIsNarrowed()
    {
        var locked = DutyUnlockShaper.Build(Records, "Dungeons", 0, 0, true, false, null, 50, 0);
        Assert.Equal(1, locked.Total);
        Assert.Equal((3, 2, 1), (locked.Known, locked.Unlocked, locked.Completed));
    }

    [Fact]
    public void PagesSortedByLevel()
    {
        var page = DutyUnlockShaper.Build(Records, null, 0, 0, false, false, null, 2, 1);
        Assert.Equal([5u, 56u], page.Duties.Select(d => d.DutyId));
        Assert.True(page.Truncated);
        Assert.False(DutyUnlockShaper.Build(Records, null, 0, 0, false, false, null, 2, 3).Truncated);
    }

    [Fact]
    public void RejectsAnInvertedLevelRange()
    {
        var ex = Assert.Throws<McpToolException>(() => DutyUnlockShaper.Build(Records, null, 60, 50, false, false, null, 50, 0));
        Assert.Equal(McpErrorCodes.InvalidArguments, ex.Code);
    }
}

public class SocialGroupsShaperTests
{
    private static SocialGroupsRecord Record(bool proxy = true, ulong id = 0, string? name = null, string? tag = null) =>
        new(proxy, id, name, tag, 30, "Maelstrom", ["Hunts", null, "  ", "Crafting"], null);

    [Fact]
    public void ReportsALoadedFreeCompany()
    {
        var fc = SocialGroupsShaper.FreeCompany(Record(id: 42, name: " Example Company ", tag: "EXMPL"));
        Assert.Equal(new FreeCompanyDto(true, true, "Example Company", "EXMPL", 30, "Maelstrom", null), fc);
    }

    [Fact]
    public void ATagWithoutDetailsMeansNotLoadedRatherThanNoCompany()
    {
        var fc = SocialGroupsShaper.FreeCompany(Record(tag: "EXMPL"));
        Assert.False(fc.Loaded);
        Assert.True(fc.Member);
        Assert.Equal("EXMPL", fc.Tag);
        Assert.Null(fc.Name);
        Assert.NotNull(fc.Note);
    }

    [Fact]
    public void NoTagMeansNoCompany()
    {
        var fc = SocialGroupsShaper.FreeCompany(Record());
        Assert.Equal((true, false), (fc.Loaded, fc.Member));
        Assert.Null(fc.Note);
        Assert.False(SocialGroupsShaper.FreeCompany(Record(proxy: false)).Loaded);
    }

    [Fact]
    public void KeepsLinkshellSlotNumbersAndSkipsEmptySlots()
    {
        var result = SocialGroupsShaper.Build(Record());
        Assert.True(result.Linkshells.Loaded);
        Assert.Equal([new GroupNameDto(1, "Hunts"), new GroupNameDto(4, "Crafting")], result.Linkshells.Groups);
        Assert.Equal(2, result.Linkshells.Count);
        Assert.False(result.CrossWorldLinkshells.Loaded);
        Assert.NotNull(result.CrossWorldLinkshells.Note);
        Assert.Equal(3, result.NotExposed.Count);
    }

    [Fact]
    public void CapsNamesAndExplainsAnEmptyList()
    {
        var names = SocialGroupsShaper.Names([new string('n', 200)], "unavailable");
        Assert.Equal(SocialGroupsShaper.MaxNameLength, names.Groups[0].Name.Length);
        var none = SocialGroupsShaper.Names([null, ""], "unavailable");
        Assert.True(none.Loaded);
        Assert.Equal(0, none.Count);
        Assert.NotNull(none.Note);
    }
}

public class EnmityShaperTests
{
    private static string? NameOf(uint id) => id switch
    {
        10 => "Example Tank",
        11 => "Example Healer",
        500 => "Striking Dummy",
        _ => null,
    };

    [Fact]
    public void ListsEnemiesInDisplayOrder()
    {
        EnmityRecord[] haters = [new(500, 100, "Striking Dummy"), new(0, 50, "gone"), new(0xE0000000, 50, "invalid"), new(501, 140, " ")];
        var result = EnmityShaper.Build(haters, 0, [], 500, 10, NameOf);
        Assert.Equal(2, result.EnemyCount);
        Assert.Equal(new EnemyListEntry(1, "Striking Dummy", 500, 100, true), result.Enemies[0]);
        Assert.Equal(new EnemyListEntry(2, null, 501, 100, null), result.Enemies[1]);
        Assert.Null(result.TargetEntityId);
        Assert.Empty(result.TargetEnmity);
    }

    [Fact]
    public void RanksTheTargetsEnmityTable()
    {
        EnmityRecord[] hate = [new(11, 250, null), new(10, 1000, null), new(0, 5, null)];
        var result = EnmityShaper.Build([], 500, hate, 500, 11, NameOf);
        Assert.Equal(500u, result.TargetEntityId);
        Assert.Equal("Striking Dummy", result.TargetName);
        Assert.Equal(new TargetEnmityEntry(1, "Example Tank", 10, 1000, 100, null), result.TargetEnmity[0]);
        Assert.Equal(new TargetEnmityEntry(2, "Example Healer", 11, 250, 25, true), result.TargetEnmity[1]);
    }

    [Fact]
    public void CapsBothLists()
    {
        var many = Enumerable.Range(1, 60).Select(i => new EnmityRecord((uint)i, i, null)).ToList();
        var result = EnmityShaper.Build(many, 500, many, 0, 0, NameOf);
        Assert.Equal(EnmityShaper.MaxEntries, result.Enemies.Count);
        Assert.Equal(EnmityShaper.MaxEntries, result.TargetEnmity.Count);
        Assert.Equal(60, result.TargetEnmity[0].Enmity);
    }
}

public class CharacterSheetShaperTests
{
    private static readonly PlayerProvider.PlayerDto Player = new(
        "Example Adventurer", 1, "1", new WorldDto(1, "ExampleWorld", "ExampleDC"), new WorldDto(2, "OtherWorld", "ExampleDC"), true,
        new JobDto(24, "WHM", "White Mage", "healer"), 100, 90, true, new PoolDto(100, 200, 50), new PoolDto(10000, 10000, 100), null, null, 0,
        new Vec3Dto(0, 0, 0), null, new RotationDto(0, 0, "N"), new IdNameDto(1, "Zone"), null, null,
        new PlayerProvider.GrandCompanyDto(1, "Maelstrom", 9, "Captain"), " ", new PlayerProvider.TitleDto(1, "The Example", false),
        null, null, false, null, false, false, false, 0, []);

    private static EquippedSlot Piece(string slot, int ilvl, double? condition, params string[] materia) =>
        new(slot, 100, slot + " item", ilvl, 100, null, condition, 0, materia.Length > 0 ? materia : null, null, null, null);

    [Fact]
    public void CondensesIdentityAndJob()
    {
        var identity = CharacterSheetShaper.Identity(Player);
        Assert.Equal(("ExampleWorld", "ExampleDC", "OtherWorld"), (identity.HomeWorld, identity.DataCenter, identity.CurrentWorld));
        Assert.Equal(("Maelstrom", "Captain", "The Example"), (identity.GrandCompany, identity.GrandCompanyRank, identity.Title));
        Assert.Null(identity.FreeCompanyTag);
        Assert.Null(CharacterSheetShaper.Identity(Player with { IsWorldVisiting = false }).CurrentWorld);

        var job = CharacterSheetShaper.Job(Player);
        Assert.Equal(("WHM", 100, 90, 200u, 10000u), (job.Abbreviation, job.Level, job.SyncedLevel, job.MaxHp, job.MaxMp));
        Assert.Null(CharacterSheetShaper.Job(Player with { IsLevelSynced = false }).SyncedLevel);
    }

    [Fact]
    public void ShowsTheStatsThatMatterForTheJob()
    {
        var attributes = new AttributesProvider.AttributesResult(24, "White Mage", new(0, 0, 0, 0, 0, 0),
            [new(5, "Mind", 4000), new(6, "Piety", 0), new(70, "Craftsmanship", 3000), new(11, "CP", 500), new(3, "Vitality", 3500)], "");
        Assert.Equal(["Mind", "Vitality"], CharacterSheetShaper.Stats(attributes, "healer").Select(s => s.Name));
        Assert.Equal(["Craftsmanship", "CP"], CharacterSheetShaper.Stats(attributes, "crafter").Select(s => s.Name));
        Assert.Equal(4, CharacterSheetShaper.Stats(attributes, null).Count);
    }

    [Fact]
    public void SummarisesGear()
    {
        var equipment = new EquipmentResult("White Mage", 712, "12 gear slots including off hand, floored", "Soul of the White Mage",
            [Piece("MainHand", 735, 80, "Savage Aim Materia XII (+54 Critical Hit)", "Quickarm Materia XI (+18 Spell Speed)"), Piece("Head", 690, 42.5), Piece("SoulCrystal", 1, null)],
            ["OffHand"]);
        var gear = CharacterSheetShaper.Gear(equipment);
        Assert.Equal(712, gear.AverageItemLevel);
        Assert.Equal((690, "Head", 42.5, 2), (gear.LowestItemLevel, gear.LowestItemLevelSlot, gear.LowestConditionPercent, gear.MateriaMelded));
        Assert.Equal(["MainHand", "Head"], gear.Pieces.Select(p => p.Slot));
        Assert.Equal(2, gear.Pieces[0].Materia!.Count);
        Assert.Equal(["OffHand"], gear.EmptySlots);

        var naked = CharacterSheetShaper.Gear(new EquipmentResult(null, 0, "", null, [], []));
        Assert.Null(naked.LowestItemLevel);
        Assert.Null(naked.LowestConditionPercent);
        Assert.Null(naked.EmptySlots);
    }

    [Fact]
    public void KeepsTheFirstNonZeroCurrencies()
    {
        var currencies = new CurrenciesResult(123456, new GrandCompanySeals(1, "Maelstrom", 20, 5000, 90000),
            [new("common", 21072, "Venture", 0), new("common", 29, "MGP", 1000), new("tomestone", 47, "Weekly tomestone", 300, 2000, 300, 450), new("hunt", 27, "Allied Seal", 12)]);
        var sheet = CharacterSheetShaper.Currencies(currencies, 2);
        Assert.Equal((123456L, "5000/90000 (Maelstrom)", 3), (sheet.Gil, sheet.GrandCompanySeals, sheet.NonZero));
        Assert.Equal(["MGP", "Weekly tomestone"], sheet.Top.Select(c => c.Name));
        Assert.Equal((300L, 450L), (sheet.Top[1].WeeklyAcquired, sheet.Top[1].WeeklyLimit));
        Assert.Empty(CharacterSheetShaper.Currencies(currencies, -3).Top);
    }

    [Fact]
    public void SummarisesRetainersAndKeepsTheNotLoadedNote()
    {
        var loaded = new RetainersResult(true, null, 2, null,
        [
            new("1", "Example-one", true, "Botanist", 100, 5000, 100, 20, null, "Limsa", new VentureInfo(1, "Quick Exploration", null, true, 0)),
            new("2", "Example-two", true, "Miner", 90, 250, 10, 0, null, null, null),
            new("3", "Lapsed", false, null, 0, 999, 0, 0, null, null, null),
        ]);
        var sheet = CharacterSheetShaper.Retainers(loaded);
        Assert.Equal((true, 2, 5250L, 1), (sheet.Loaded, sheet.Count, sheet.TotalGil, sheet.VenturesComplete));
        Assert.Equal("Quick Exploration", sheet.Retainers[0].Venture);

        var notLoaded = CharacterSheetShaper.Retainers(new RetainersResult(false, "use a summoning bell", 2, null, []));
        Assert.False(notLoaded.Loaded);
        Assert.Equal("use a summoning bell", notLoaded.Note);
    }

    [Fact]
    public void OmitsSectionsThatCouldNotBeRead()
    {
        var sheet = CharacterSheetShaper.Build(Player, null, null, null, null, 12, ["gear: not loaded yet"]);
        Assert.NotNull(sheet.Identity);
        Assert.Null(sheet.Gear);
        Assert.Null(sheet.Retainers);
        Assert.Equal(["gear: not loaded yet"], sheet.Unavailable);
        Assert.Null(CharacterSheetShaper.Build(Player, null, null, null, null, 12, []).Unavailable);
    }
}

public class ZoneLiveShaperTests
{
    private static readonly LocationProvider.LocationDto Location = new(
        new LocationProvider.TerritoryDto(137, "Eastern La Noscea", "s1f4", "La Noscea", "La Noscea", 1, "Overworld", false, true),
        new IdNameDto(18, "Eastern La Noscea"), "Bloodshore", "Costa del Sol", 2, null, new Vec3Dto(1, 2, 3), new MapCoordsDto(30.1, 30.5),
        new IdNameDto(2, "Fair Skies"), null, true, true, true,
        new LocationProvider.AetheryteNearbyDto(11, "Costa del Sol", false, true, new MapCoordsDto(30, 30), 0.52), null);

    private static FateProvider.FateDto Fate(uint id, string state, double distance, bool inside = false) =>
        new(id, $"Fate {id}", null, 30, 35, state, 40, 600, 900, null, id == 1, 0, new Vec3Dto(0, 0, 0), new MapCoordsDto(20, 20), distance, 30, inside, false, false);

    private static AetheryteProvider.AetheryteDto Aetheryte(uint id, string name, bool here, bool housing = false, bool home = false) =>
        new(id, 0, name, here ? 137u : 1u, "Zone", "Region", 100, false, false, home, here, housing, false, false, null, null, true);

    [Fact]
    public void FoldsTheThreeSnapshotsIntoOneZoneSummary()
    {
        var fates = new FateProvider.FatesDto(3, 3, false, 1, [Fate(1, "Running", 50, true), Fate(2, "Ended", 60), Fate(3, "Preparing", 90)]);
        var aetherytes = new AetheryteProvider.AetherytesDto(4, 0, 4, false,
            [Aetheryte(11, "Costa del Sol", true, home: true), Aetheryte(12, "Wineport", true), Aetheryte(8, "Limsa Lominsa", false), Aetheryte(99, "Estate Hall", true, housing: true)]);

        var zone = ZoneLiveShaper.Build(Location, fates, aetherytes, 1, []);
        Assert.Equal((137u, "Eastern La Noscea", "Overworld"), (zone.TerritoryId, zone.Zone, zone.IntendedUse));
        Assert.Equal((2u, true), (zone.Instance, zone.IsInstancedZone));
        Assert.Equal("Fair Skies", zone.Weather?.Name);
        Assert.True(zone.InSanctuary);
        Assert.False(zone.InDuty);
        Assert.False(zone.InHousingDistrict);
        Assert.Equal((2, 1u), (zone.Fates!.Active, zone.Fates.CurrentFateId));
        var nearest = Assert.Single(zone.Fates.Nearest);
        Assert.Equal((1u, true, true), (nearest.Id, nearest.HasBonus, nearest.PlayerIsIn));
        Assert.Equal(2, zone.AttunedAetherytesHere);
        Assert.Equal([11u, 12u], zone.Aetherytes.Select(a => a.AetheryteId));
        Assert.True(zone.Aetherytes[0].IsHomePoint);
        Assert.Null(zone.Aetherytes[1].IsHomePoint);
        Assert.Equal("Costa del Sol (0.5 map units away)", zone.NearestAetheryte);
        Assert.Null(zone.Unavailable);
    }

    [Fact]
    public void ReportsDutyHousingAndMissingParts()
    {
        var inside = Location with
        {
            Instance = 0,
            Duty = new IdNameDto(4, "Sastasha"),
            Housing = new LocationProvider.HousingDto("Mist", 3, 12, null, 1, true, false, 2, 11),
            NearestAetheryte = new LocationProvider.AetheryteNearbyDto(11, null, false, false, new MapCoordsDto(1, 1), 2),
        };
        var zone = ZoneLiveShaper.Build(inside, null, null, 5, ["fates: unavailable"]);
        Assert.Null(zone.Instance);
        Assert.False(zone.IsInstancedZone);
        Assert.True(zone.InDuty);
        Assert.Equal(new ZoneHousingDto("Mist", 3, 12, null, true), zone.Housing);
        Assert.True(zone.InHousingDistrict);
        Assert.Null(zone.Fates);
        Assert.Empty(zone.Aetherytes);
        Assert.Equal("Aetheryte #11 (2.0 map units away, not attuned)", zone.NearestAetheryte);
        Assert.Equal(["fates: unavailable"], zone.Unavailable);
    }

    [Fact]
    public void ClampsTheFateCount()
    {
        var fates = new FateProvider.FatesDto(40, 40, false, null, Enumerable.Range(1, 40).Select(i => Fate((uint)i, "Running", i)).ToList());
        Assert.Equal(25, ZoneLiveShaper.Fates(fates, 900).Nearest.Count);
        Assert.Empty(ZoneLiveShaper.Fates(fates, -1).Nearest);
    }
}

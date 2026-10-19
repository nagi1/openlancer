using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using LibreLancer.Data.Schema.GCS;
using LibreLancer.Data.Schema.Storyline;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The categories of the cutscene list (section 9 of the cutscene categories design, tests 1 to 12). All names are
/// synthetic. The INI files are small text files in a <see cref="TestWorkspace"/>.
/// </summary>
public sealed class CutsceneCategoriesTests
{
    private static readonly StoryMissionFact Story07A = Mission("Story_07a", "MX07A");
    private static readonly StoryMissionFact Story07B = Mission("Story_07b", "MX07B");
    private static readonly StoryMissionFact Story08 = Mission("Story_08", "MX08");
    private static readonly StoryMissionFact[] Missions = [Story07A, Story07B, Story08];

    private static StoryMissionFact Mission(string nickname, string folder) =>
        new(nickname, $@"DATA\MISSIONS\{folder}\{folder.ToLowerInvariant()}.ini", $@"DATA\MISSIONS\{folder}");

    private static string Bases(string name) => $@"DATA\SCRIPTS\BASES\{name}.thn";

    private static string Story(string name) => $@"DATA\SCRIPTS\STORY\{name}.thn";

    /// <summary>The facts of the INI files of a workspace and of synthetic values.</summary>
    private static CutsceneFacts Facts(TestWorkspace ws, IReadOnlyList<StoryMissionFact>? missions = null,
        Dictionary<string, BaseFact>? bases = null, Dictionary<string, ConversationFact>? conversations = null,
        IEnumerable<string>? intro = null, IEnumerable<string>? unsaved = null) =>
        new(ThnReferences.Scan(FileSystem.FromPath(ws.Base)), missions ?? Missions,
            new Dictionary<string, BaseFact>(bases ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ConversationFact>(conversations ?? [], StringComparer.OrdinalIgnoreCase),
            (intro ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (unsaved ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            id => id == "moda" ? "Mod A" : id);

    private static CutsceneGroup GroupOf(CutsceneCategories categories, string path)
    {
        var place = categories.PlaceOf(path)!.Value;
        return categories.Categories.Single(c => c.Category == place.Category).Groups.Single(g => g.Id == place.GroupId);
    }

    private static CutsceneGroup Group(CutsceneCategories categories, CutsceneCategory category, string label) =>
        categories.Categories.Single(c => c.Category == category).Groups.Single(g => g.Label == label);

    private static string[] Names(CutsceneGroup group) => group.Rows.Select(r => r.Name).ToArray();

    /// <summary>
    /// Writes the universe, base and room files of the region tests. Each base has one room that plays the scripts of
    /// its line. The facts put five bases in the systems Li07, Br09, Hi05 and Zz01, with a name and a system name.
    /// </summary>
    private static Dictionary<string, BaseFact> WriteRooms(TestWorkspace ws, params (string Base, string[] Scripts)[] rooms)
    {
        var universe = new StringBuilder();
        foreach (var (baseName, scripts) in rooms)
        {
            universe.Append($"[Base]\nnickname = {baseName}\nfile = Universe\\Bases\\{baseName}.ini\n");
            ws.WriteBase($@"DATA\UNIVERSE\Bases\{baseName}.ini",
                $"[Room]\nnickname = Bar\nfile = Universe\\Bases\\Rooms\\{baseName}_bar.ini\n");
            ws.WriteBase($@"DATA\UNIVERSE\Bases\Rooms\{baseName}_bar.ini",
                "[Room_Info]\n" + string.Concat(scripts.Select(s => $"scene = all, ambient, Scripts\\Bases\\{s}.thn\n")));
        }

        ws.WriteBase(@"DATA\UNIVERSE\universe.ini", universe.ToString());
        return new Dictionary<string, BaseFact>(StringComparer.OrdinalIgnoreCase)
        {
            ["zz_li_a"] = new("Port Alder", "Li07", "Alder Reach"),
            ["zz_li_b"] = new("Port Birch", "Li07", "Alder Reach"),
            ["zz_br_a"] = new("Fort Cedar", "Br09", "Cedar Reach"),
            ["zz_hi_a"] = new("Dock Elm", "Hi05", "Elm Reach"),
            ["zz_zz_a"] = new("Pier Fir", "Zz01", "Fir Reach"),
        };
    }

    /// <summary>Test 1: one path for each rule of D602 gives its category. A path outside DATA is in Other, in the
    /// group "Outside DATA". The counts add up to the number of paths.</summary>
    [Fact]
    public void EachFolderRuleGivesItsCategory()
    {
        using var ws = new TestWorkspace();
        string[] paths =
        [
            @"DATA\MODS\moda\CUTSCENES\zz_mod.thn", Story("zz_story"), @"DATA\MISSIONS\MX08\zz_mission.thn",
            @"DATA\SCRIPTS\INTRO\zz_intro.thn", Bases("zz_room_a"), @"data\scripts\gcs\zz_talk.thn",
            @"DATA\SCRIPTS\EXTRAS\zz_fidget.thn", @"DATA\SCRIPTS\VENDORS\zz_vendor.thn",
            @"DATA\RANDOMMISSIONS\zz_random.thn", @"DATA\MISSIONS\zz_loose.thn", @"OTHER\zz_outside.thn",
        ];

        var categories = CutsceneCategories.Build(paths, Facts(ws));

        Assert.Equal([
            CutsceneCategory.Mods, CutsceneCategory.MainStory, CutsceneCategory.MainStory, CutsceneCategory.Intro,
            CutsceneCategory.Bases, CutsceneCategory.BarConversations, CutsceneCategory.Characters,
            CutsceneCategory.Characters, CutsceneCategory.RandomMissions, CutsceneCategory.Other, CutsceneCategory.Other,
        ], paths.Select(p => categories.PlaceOf(p)!.Value.Category));
        Assert.Equal(["Intro scenes", "Main story", "Bases and rooms", "Bar conversations", "Characters",
                "Random missions", "Mod cutscenes", "Other"],
            categories.Categories.Select(c => CutsceneCategories.Label(c.Category)));
        Assert.Equal("Outside DATA", GroupOf(categories, @"OTHER\zz_outside.thn").Label);
        Assert.Equal("MISSIONS", GroupOf(categories, @"DATA\MISSIONS\zz_loose.thn").Label);
        Assert.Equal(["Generic fidgets", "Vendor fidgets"],
            categories.Categories.Single(c => c.Category == CutsceneCategory.Characters).Groups.Select(g => g.Label));
        Assert.Equal(paths.Length, categories.Count);
        Assert.Equal(paths.Length, categories.Categories.Sum(c => c.Count));
        Assert.Equal(CutsceneCategory.Bases, categories.PlaceOf(@"data/Scripts/Bases/ZZ_ROOM_A.thn")!.Value.Category);
        Assert.Null(categories.PlaceOf(Bases("zz_not_in_list")));
    }

    /// <summary>Test 2: the mission of a Main story script is the first story mission whose folder has an INI file that
    /// names it. Else it is the mission of its folder (decision D603).</summary>
    [Fact]
    public void TheMissionOfAScriptComesFromTheIniFilesOfTheMissionFolders()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\MISSIONS\MX08\zz_rtc8.ini",
            "[CharacterEncounter]\naction = Scripts\\Story\\zz_s_eight.thn\n" +
            "[CharacterEncounter]\naction = Scripts\\Story\\zz_s_both.thn\n");
        ws.WriteBase(@"DATA\MISSIONS\MX07A\zz_rtc7.ini", "[CharacterEncounter]\naction = Scripts\\Story\\zz_s_both.thn\n");
        ws.WriteBase(@"DATA\MISSIONS\MX08\mx08.ini", "[Trigger]\nAct_CallThorn = missions\\\\mx07b\\\\zz_b_eight.thn, obj\n");
        ws.WriteBase(@"DATA\UNIVERSE\zz_room.ini", "[Room_Info]\nscene = all, ambient, Missions\\MX07B\\zz_b_room.thn\n");
        string[] paths =
        [
            Story("zz_s_eight"), Story("zz_s_both"), @"DATA\MISSIONS\MX07B\zz_b_eight.thn",
            @"DATA\MISSIONS\MX07B\zz_b_room.thn", @"DATA\MISSIONS\MX07B\zz_b_unused.thn", Story("zz_s_unused"),
        ];

        var categories = CutsceneCategories.Build(paths, Facts(ws));

        Assert.Equal(["Mission 8", "Mission 7", "Mission 8", "Mission 7", "Mission 7", "Not used by a mission"],
            paths.Select(p => GroupOf(categories, p).Label));
        Assert.Equal(CutsceneCategories.UnusedDetail, categories.RowOf(@"DATA\MISSIONS\MX07B\zz_b_unused.thn")!.Detail);
        Assert.False(categories.RowOf(@"DATA\MISSIONS\MX07B\zz_b_unused.thn")!.Used);
        Assert.Equal(CutsceneCategories.UnusedDetail, categories.RowOf(Story("zz_s_unused"))!.Detail);
        Assert.Equal(CutsceneCategories.SpaceDetail, categories.RowOf(@"DATA\MISSIONS\MX07B\zz_b_eight.thn")!.Detail);
        Assert.True(categories.RowOf(@"DATA\MISSIONS\MX07B\zz_b_room.thn")!.Used);
        Assert.Equal("No INI file of a story mission folder names these scripts.",
            Group(categories, CutsceneCategory.MainStory, "Not used by a mission").Tooltip);

        // "First" is the story order, not the scan order. The scan reads MX07A before MX08, but with Story_08 first in
        // the storyline, the script that both folders name goes to Mission 8.
        var reordered = CutsceneCategories.Build(paths, Facts(ws, [Story08, Story07A, Story07B]));
        Assert.Equal("Mission 8", GroupOf(reordered, Story("zz_s_both")).Label);
        Assert.Equal(["Mission 8", "Mission 7", "Not used by a mission"],
            reordered.Categories.Single(c => c.Category == CutsceneCategory.MainStory).Groups.Select(g => g.Label));
    }

    /// <summary>Test 3: two parts with one number make one group. A nickname without a number keeps its nickname. The
    /// first group has the detail "game opening" (decision D605).</summary>
    [Fact]
    public void PartsWithOneNumberShareAGroupAndTheFirstGroupIsTheGameOpening()
    {
        using var ws = new TestWorkspace();
        var finale = Mission("Finale", "MXF");
        string[] paths =
        [
            @"DATA\MISSIONS\MX07A\zz_a.thn", @"DATA\MISSIONS\MX07B\zz_b.thn", @"DATA\MISSIONS\MX08\zz_c.thn",
            @"DATA\MISSIONS\MXF\zz_d.thn",
        ];

        var categories = CutsceneCategories.Build(paths, Facts(ws, [Story07A, Story07B, Story08, finale]));

        var groups = categories.Categories.Single(c => c.Category == CutsceneCategory.MainStory).Groups;
        Assert.Equal(["Mission 7", "Mission 8", "Finale"], groups.Select(g => g.Label));
        Assert.Equal([CutsceneCategories.OpeningDetail, "", ""], groups.Select(g => g.Detail));
        Assert.Equal(groups[0].Id, categories.OpeningGroup);
        Assert.Equal(["zz_a", "zz_b"], Names(groups[0]));
        Assert.Contains("Story_07a: DATA\\MISSIONS\\MX07A\\mx07a.ini", groups[0].Tooltip, StringComparison.Ordinal);
        Assert.Contains("Story_07b: DATA\\MISSIONS\\MX07B\\mx07b.ini", groups[0].Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Test 4: the rows of a story mission follow the play order (decision D604). First come the scripts of the mission
    /// file and of the RTC files that it adds, by the section of the line. Then comes a script that another file of the
    /// folder names. Then come the unused scripts, by name. The rows of part a come before the rows of part b.
    /// </summary>
    [Fact]
    public void TheRowsOfAMissionFollowThePlayOrder()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\MISSIONS\MX07A\mx07a.ini",
            "[Trigger]\nnickname = t1\nAct_CallThorn = missions\\\\mx07a\\\\zz_a.thn, obj\n" +
            "[Trigger]\nnickname = t2\nAct_AddRTC = missions\\\\mx07a\\\\zz_rtc1.ini\n" +
            "[Trigger]\nnickname = t3\nAct_AddAmbient = missions\\\\mx07a\\\\zz_d.thn, zz_room, zz_base\n");
        ws.WriteBase(@"DATA\MISSIONS\MX07A\zz_rtc1.ini",
            "[CharacterEncounter]\naction = Missions\\MX07A\\zz_c.thn\n" +
            "[CharacterEncounter]\naction = Missions\\MX07A\\zz_b.thn\n");
        ws.WriteBase(@"DATA\MISSIONS\MX07A\zz_other.ini", "[CharacterEncounter]\naction = Missions\\MX07A\\zz_e.thn\n");
        string[] names = ["zz_g_unused", "zz_e", "zz_b", "zz_f_unused", "zz_d", "zz_c", "zz_a"];
        var paths = names.Select(n => $@"DATA\MISSIONS\MX07A\{n}.thn").Append(@"DATA\MISSIONS\MX07B\zz_0_part_b.thn");

        var categories = CutsceneCategories.Build(paths, Facts(ws));

        Assert.Equal(["zz_a", "zz_c", "zz_b", "zz_d", "zz_e", "zz_f_unused", "zz_g_unused", "zz_0_part_b"],
            Names(Group(categories, CutsceneCategory.MainStory, "Mission 7")));
        Assert.Equal("zz_base", categories.RowOf(@"DATA\MISSIONS\MX07A\zz_d.thn")!.Detail);
    }

    /// <summary>
    /// Test 6: the group of a room script is the region of the bases that play it (decisions D606 and D607). A tie goes
    /// to the region whose prefix starts the file name, else to the first region of the table. In the tie of Li07 and
    /// Br09, the table order (Liberty) differs from the name order and from the scan order of the room files
    /// (Bretonia).
    /// </summary>
    [Fact]
    public void TheGroupOfARoomScriptIsTheRegionOfItsBases()
    {
        using var ws = new TestWorkspace();
        var bases = WriteRooms(ws,
            ("zz_li_a", ["zz_room_one", "zz_room_two", "zz_room_three", "pl_set_b"]),
            ("zz_li_b", ["zz_room_two"]),
            ("zz_br_a", ["zz_room_two", "hi_set_a", "pl_set_a", "zz_room_three", "pl_set_b"]),
            ("zz_hi_a", ["hi_set_a", "pl_set_a", "zz_room_three"]),
            ("zz_zz_a", ["zz_room_far"]),
            ("zz_gone", ["zz_room_gone"]));
        string[] names = ["zz_room_one", "zz_room_two", "hi_set_a", "pl_set_a", "pl_set_b", "zz_room_three",
            "zz_room_none", "zz_room_far", "zz_room_gone"];

        var categories = CutsceneCategories.Build(names.Select(Bases), Facts(ws, bases: bases));

        Assert.Equal(["Liberty", "Liberty", "Hispania", "Bretonia", "Liberty", "Three or more regions",
            "Not used by a room", "ZZ systems", "Not used by a room"], names.Select(n => GroupOf(categories, Bases(n)).Label));
        Assert.Equal(["Liberty", "Bretonia", "Hispania", "ZZ systems", "Three or more regions", "Not used by a room"],
            categories.Categories.Single(c => c.Category == CutsceneCategory.Bases).Groups.Select(g => g.Label));
        Assert.Equal("Systems: Fir Reach", Group(categories, CutsceneCategory.Bases, "ZZ systems").Tooltip);
        Assert.Equal("Systems: Alder Reach", Group(categories, CutsceneCategory.Bases, "Liberty").Tooltip);
        Assert.Equal("Port Alder", categories.RowOf(Bases("zz_room_one"))!.Detail);
        // The first base is the base of the first room file in scan order.
        Assert.Equal("Fort Cedar +2", categories.RowOf(Bases("zz_room_two"))!.Detail);
        Assert.Equal(CutsceneCategories.UnusedDetail, categories.RowOf(Bases("zz_room_none"))!.Detail);
    }

    /// <summary>Test 7: the bar conversations have the groups of genericscripts.ini by gender and posture, in order. The
    /// detail is the first segment, with "+N" for more segments (decision D608).</summary>
    [Fact]
    public void TheBarConversationsHaveTheGroupsOfTheConversationList()
    {
        using var ws = new TestWorkspace();
        string[] names = ["zz_talk_fi", "zz_talk_fs", "zz_talk_mi", "zz_talk_ms", "zz_talk_none"];
        ws.WriteBase(@"DATA\SCRIPTS\GCS\zz_list.ini",
            "[GenericScripts]\nset_segment = seg_a\n" +
            string.Concat(names.Select(n => $"script = Scripts\\GCS\\{n}.thn\n")));
        string Gcs(string name) => $@"DATA\SCRIPTS\GCS\{name}.thn";
        var conversations = new Dictionary<string, ConversationFact>
        {
            [Gcs("zz_talk_ms")] = new(FLGender.male, Posture.Stand, ["seg_a"]),
            [Gcs("zz_talk_mi")] = new(FLGender.male, Posture.SitLow, ["seg_b"]),
            [Gcs("zz_talk_fs")] = new(FLGender.female, Posture.Stand, ["seg_c", "seg_d"]),
            [Gcs("zz_talk_fi")] = new(FLGender.female, Posture.SitLow, ["seg_e"]),
        };

        var categories = CutsceneCategories.Build(names.Select(Gcs), Facts(ws, conversations: conversations));

        var groups = categories.Categories.Single(c => c.Category == CutsceneCategory.BarConversations).Groups;
        Assert.Equal(["Male, standing", "Male, sitting", "Female, standing", "Female, sitting",
            "Not in the conversation list"], groups.Select(g => g.Label));
        Assert.Equal(["zz_talk_none"], Names(groups[^1]));
        Assert.Equal("seg_a", categories.RowOf(Gcs("zz_talk_ms"))!.Detail);
        Assert.Equal("seg_c +1", categories.RowOf(Gcs("zz_talk_fs"))!.Detail);
        Assert.Equal("genericscripts.ini does not list these scripts.", groups[^1].Tooltip);
    }

    /// <summary>Test 8: a script of an intro scene outside the INTRO folder is an intro scene. An INTRO script that no
    /// room plays is there too, with "not used".</summary>
    [Fact]
    public void TheScriptsOfTheIntroScenesAreIntroScenes()
    {
        using var ws = new TestWorkspace();
        var bases = WriteRooms(ws, ("zz_li_a", ["zz_intro_room"]));
        string[] paths = [Bases("zz_intro_room"), @"DATA\SCRIPTS\INTRO\zz_intro_b.thn"];

        var categories = CutsceneCategories.Build(paths, Facts(ws, bases: bases, intro: [Bases("zz_intro_room")]));

        var intro = Assert.Single(categories.Categories);
        Assert.Equal(CutsceneCategory.Intro, intro.Category);
        var group = Assert.Single(intro.Groups);
        Assert.Equal("", group.Label);
        Assert.Equal(["zz_intro_b", "zz_intro_room"], Names(group));
        Assert.Equal(CutsceneCategories.UnusedDetail, categories.RowOf(@"DATA\SCRIPTS\INTRO\zz_intro_b.thn")!.Detail);
        Assert.Equal("", categories.RowOf(Bases("zz_intro_room"))!.Detail);
        Assert.Equal("main menu", CutsceneCategories.Detail(CutsceneCategory.Intro));
    }

    /// <summary>Test 9: a new cutscene of a mod is in Mod cutscenes, in the group with the name of the mod. A new
    /// unsaved cutscene shows the same way, with the detail "new" (decision D615).</summary>
    [Fact]
    public void TheNewCutscenesOfAModAreInTheGroupOfTheMod()
    {
        using var ws = new TestWorkspace();
        const string saved = @"DATA\MODS\moda\CUTSCENES\x.thn";
        const string unsaved = @"DATA\MODS\moda\CUTSCENES\zz_new.thn";

        var categories = CutsceneCategories.Build([saved], Facts(ws, unsaved: [unsaved]));

        var group = GroupOf(categories, saved);
        Assert.Equal("Mod A", group.Label);
        Assert.Same(group, GroupOf(categories, unsaved));
        Assert.Equal(["x", "zz_new"], Names(group));
        Assert.Equal(CutsceneCategories.NewDetail, categories.RowOf(unsaved)!.Detail);
        Assert.Equal(CutsceneCategories.UnusedDetail, categories.RowOf(saved)!.Detail);
        Assert.Equal(2, categories.Count);
    }

    /// <summary>The categories of the filter test. Bases and rooms has three Liberty room scripts, and the base of one
    /// of them has a name that no row shows. Intro scenes and Main story have one script each.</summary>
    private static CutsceneCategories FilterCategories(TestWorkspace ws)
    {
        var bases = WriteRooms(ws, ("zz_li_a", ["zz_bar_one", "zz_room_one"]), ("zz_li_b", ["zz_room_two"]));
        return CutsceneCategories.Build([Bases("zz_bar_one"), Bases("zz_room_one"), Bases("zz_room_two"),
            @"DATA\SCRIPTS\INTRO\zz_intro.thn", @"DATA\MISSIONS\MX07A\zz_m7.thn"], Facts(ws, bases: bases));
    }

    private static string[] RowNames(HierarchyModel model) => model.Rows.Skip(1).Select(r => r.Node.Name).ToArray();

    /// <summary>
    /// Test 10: the filter of the list searches all categories. It shows only the matching rows below open headers, with
    /// "matches / total" counts. It also matches the words that only the search text has: a base name, a system name and
    /// the label of a group. Reveal opens the category and the group of a row.
    /// </summary>
    [Fact]
    public void TheFilterSearchesTheRowsAndTheWordsOfTheirGroups()
    {
        using var ws = new TestWorkspace();
        var categories = FilterCategories(ws);
        var tree = CutscenePicker.Tree(categories, null);

        tree.SetSearch("bar");
        Assert.Equal(["Bases and rooms", "Liberty", "zz_bar_one"], RowNames(tree));
        Assert.Equal(["1 / 3", "1 / 3", ""], tree.Rows.Skip(1).Select(r => r.Node.CountText));
        Assert.Equal(CutscenePicker.ScriptId(Bases("zz_bar_one")), CutscenePicker.FirstScript(tree));

        tree.SetSearch("birch");
        Assert.Equal(["Bases and rooms", "Liberty", "zz_room_two"], RowNames(tree));
        tree.SetSearch("REACH zz_room");
        Assert.Equal(["Bases and rooms", "Liberty", "zz_room_one", "zz_room_two"], RowNames(tree));

        tree.SetSearch("liberty");
        Assert.Equal(["Bases and rooms", "Liberty", "zz_bar_one", "zz_room_one", "zz_room_two"], RowNames(tree));
        tree.SetSearch("opening");
        Assert.Equal(["Main story", "Mission 7", "zz_m7"], RowNames(tree));
        // The header of the intro scenes shows "main menu", so "menu" finds them.
        tree.SetSearch("menu");
        Assert.Equal(["Intro scenes", "zz_intro"], RowNames(tree));

        tree.SetSearch("zz nomatch");
        Assert.Empty(RowNames(tree));
        Assert.Equal(0, tree.Root.Matches);
        Assert.Null(CutscenePicker.FirstScript(tree));

        tree.SetSearch("");
        Assert.DoesNotContain("zz_room_two", RowNames(tree));
        Assert.True(tree.Reveal(Bases("zz_room_two")));
        Assert.Equal(["Intro scenes", "zz_intro", "Main story", "Mission 7", "zz_m7", "Bases and rooms", "Liberty",
            "zz_bar_one", "zz_room_one", "zz_room_two"], RowNames(tree));
        Assert.Equal(["3", "3"], tree.Rows.Where(r => r.Node.Name is "Bases and rooms" or "Liberty")
            .Select(r => r.Node.CountText));
    }

    /// <summary>The keys of the list: Up and Down move the highlight over the visible rows, headers too, and skip the
    /// root. Without a visible highlight, Down goes to the first row and Up to the last row.</summary>
    [Fact]
    public void UpAndDownMoveTheHighlightOverTheVisibleRows()
    {
        using var ws = new TestWorkspace();
        var tree = CutscenePicker.Tree(FilterCategories(ws), null);
        var rows = tree.Rows;
        var first = rows[1].Node.Id;

        Assert.Equal(first, CutscenePicker.Step(rows, null, 1));
        Assert.Equal(rows[^1].Node.Id, CutscenePicker.Step(rows, null, -1));
        Assert.Equal(rows[2].Node.Id, CutscenePicker.Step(rows, first, 1));
        Assert.Equal(first, CutscenePicker.Step(rows, first, -1));
        Assert.Equal(rows[^1].Node.Id, CutscenePicker.Step(rows, rows[^1].Node.Id, 1));
        Assert.Equal(first, CutscenePicker.Step(rows, "s:not visible", 1));
    }

    /// <summary>Test 11: the first tree opens Intro scenes, Main story and the group of the game opening only (decision
    /// D612). A new tree with the same identity keeps the open groups.</summary>
    [Fact]
    public void TheFirstTreeOpensTheIntroAndTheGameOpeningAndANewTreeKeepsTheOpenGroups()
    {
        using var ws = new TestWorkspace();
        var categories = FilterCategories(ws);
        var tree = CutscenePicker.Tree(categories, null);

        Assert.Equal(["Intro scenes", "zz_intro", "Main story", "Mission 7", "zz_m7", "Bases and rooms"], RowNames(tree));

        var intro = tree.Root.Children.Single(c => c.Id == CutscenePicker.CategoryId(CutsceneCategory.Intro));
        var basesNode = tree.Root.Children.Single(c => c.Id == CutscenePicker.CategoryId(CutsceneCategory.Bases));
        tree.SetOpen(intro, false);
        tree.SetOpen(basesNode, true);
        var again = CutscenePicker.Tree(FilterCategories(ws), tree);

        Assert.Equal(["Intro scenes", "Main story", "Mission 7", "zz_m7", "Bases and rooms", "Liberty"], RowNames(again));
    }

    /// <summary>Test 12: a choice of the group menu of the Cutscenes tab. A group keeps only its scripts, and a category
    /// keeps all its groups. A new unsaved cutscene shows under Mod cutscenes.</summary>
    [Fact]
    public void TheGroupMenuKeepsTheScriptsOfTheChosenCategoryOrGroup()
    {
        using var ws = new TestWorkspace();
        var bases = WriteRooms(ws, ("zz_li_a", ["zz_room_one"]), ("zz_br_a", ["zz_room_two"]));
        const string unsaved = @"DATA\MODS\moda\CUTSCENES\zz_new.thn";
        string[] paths = [Bases("zz_room_one"), Bases("zz_room_two"), Bases("zz_room_none"), Story("zz_story")];
        var categories = CutsceneCategories.Build(paths, Facts(ws, bases: bases, unsaved: [unsaved]));
        var all = paths.Append(unsaved).ToArray();

        string[] Kept(CutsceneChoice choice) => all.Where(p => choice.Keeps(categories.PlaceOf(p))).ToArray();

        var liberty = GroupOf(categories, Bases("zz_room_one")).Id;
        Assert.Equal([Bases("zz_room_one")], Kept(new CutsceneChoice(CutsceneCategory.Bases, liberty)));
        Assert.Equal([Bases("zz_room_one"), Bases("zz_room_two"), Bases("zz_room_none")],
            Kept(new CutsceneChoice(CutsceneCategory.Bases, null)));
        Assert.Equal([unsaved], Kept(new CutsceneChoice(CutsceneCategory.Mods, null)));
        Assert.False(new CutsceneChoice(CutsceneCategory.Bases, null).Keeps(null));

        // The closed menu shows the label of the choice. Without the new cutscene, the group of the mod goes away, so
        // the choice has no label, and the menu goes back to all cutscenes.
        var mod = new CutsceneChoice(CutsceneCategory.Mods, GroupOf(categories, unsaved).Id);
        Assert.Equal("Mod A", categories.LabelOf(mod));
        Assert.Equal("Liberty", categories.LabelOf(new CutsceneChoice(CutsceneCategory.Bases, liberty)));
        Assert.Equal("Bases and rooms", categories.LabelOf(new CutsceneChoice(CutsceneCategory.Bases, null)));
        Assert.Null(categories.LabelOf(new CutsceneChoice(CutsceneCategory.Bases, "region:ZZ")));
        var after = CutsceneCategories.Build(paths, Facts(ws, bases: bases));
        Assert.Null(after.LabelOf(mod));
        Assert.Null(after.LabelOf(new CutsceneChoice(CutsceneCategory.Mods, null)));
    }

    /// <summary>
    /// The cache of the workspace (section 4.5 and decision D615). For the same data and edit version, it does not read
    /// the new unsaved cutscenes. For another version with the same set, it keeps the categories. Another set or other
    /// data builds again.
    /// </summary>
    [Fact]
    public void TheCacheBuildsAgainOnlyForOtherDataOrAnotherSetOfNewCutscenes()
    {
        using var ws = new TestWorkspace();
        var facts = Facts(ws);
        var cache = new CutsceneWorkspace.CategoriesCache();
        var data = new object();
        string[] unsaved = [];
        var (reads, builds) = (0, 0);

        CutsceneCategories Get(object of, int version) => cache.Get(of, version,
            () =>
            {
                reads++;
                return unsaved;
            },
            paths =>
            {
                builds++;
                return CutsceneCategories.Build([Bases("zz_room_a")], facts with { Unsaved = paths.ToHashSet() });
            });

        var first = Get(data, 1);
        Assert.Same(first, Get(data, 1));
        Assert.Equal((1, 1), (reads, builds));

        Assert.Same(first, Get(data, 2));
        Assert.Equal((2, 1), (reads, builds));

        unsaved = ["data/mods/moda/cutscenes/zz_new.thn"];
        var withNew = Get(data, 3);
        Assert.NotSame(first, withNew);
        Assert.Equal(CutsceneCategories.NewDetail, withNew.RowOf(@"DATA\MODS\moda\CUTSCENES\zz_new.thn")!.Detail);
        Assert.Same(withNew, Get(data, 4));
        Assert.Equal(2, builds);

        Assert.NotSame(withNew, Get(new object(), 4));
        Assert.Equal(3, builds);

        cache.Reset();
        Get(data, 4);
        Assert.Equal(4, builds);
    }

    /// <summary>
    /// The adapter of the loaded game data (section 4.2). It reads the storyline, the bases and their systems, the
    /// conversation list, the intro scenes and the new unsaved cutscenes, and it puts each path in normal form. A base
    /// without a system is not in the facts. A name without text is the nickname.
    /// </summary>
    [Fact]
    public void TheFactsComeFromTheLoadedGameData()
    {
        using var ws = new TestWorkspace();
        // A freelancer.ini without the resource DLLs, so the game data object loads no string resources.
        ws.WriteBase(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n");
        var items = new GameItemDb(FileSystem.FromPath(ws.Base));
        items.Ini.Storyline = new StorylineIni
        {
            Missions = [new StoryMission { Nickname = "Story_07a", File = @"missions\\mx07a\\mx07a.ini" }],
        };
        items.Ini.GenericScripts = new GenericScriptsIni();
        items.Ini.GenericScripts.Scripts[new GenericScriptKey("seg_a", Posture.Stand, FLGender.male)] =
            [@"Scripts\GCS\zz_talk.thn"];
        items.Ini.GenericScripts.Scripts[new GenericScriptKey("seg_b", Posture.SitLow, FLGender.female)] =
            ["scripts/gcs/ZZ_TALK.thn"];
        Add(items.Systems, new StarSystem { Nickname = "Zz01", IdsName = 2, SourceFile = "" });
        Add(items.Systems, new StarSystem { Nickname = "Zz02", SourceFile = "" });
        Add(items.Bases, new Base { Nickname = "zz_base_a", System = "Zz01", IdsName = 1 });
        Add(items.Bases, new Base { Nickname = "zz_base_b", System = "Zz02" });
        Add(items.Bases, new Base { Nickname = "zz_base_c" });
        items.IntroScenes.Add(new IntroScene
        {
            Scripts =
            [
                new ResolvedThn
                {
                    ReadCallback = items.VFS.ReadAllBytes, VFS = items.VFS, DataPath = "data/scripts/intro/zz_intro.thn",
                    SourcePath = null,
                },
            ],
        });
        string[] texts = ["", "Port Alder", "Alder Reach"];

        var facts = CutsceneFacts.From(items, ThnReferences.Scan(items.VFS), ids => texts[ids], id => id,
            ["data/mods/moda/cutscenes/zz_new.thn"]);

        Assert.Equal(new StoryMissionFact("Story_07a", @"DATA\missions\mx07a\mx07a.ini", @"DATA\missions\mx07a"),
            Assert.Single(facts.Missions));
        Assert.Equal(new BaseFact("Port Alder", "Zz01", "Alder Reach"), facts.Bases["ZZ_BASE_A"]);
        Assert.Equal(new BaseFact("zz_base_b", "Zz02", "Zz02"), facts.Bases["zz_base_b"]);
        Assert.False(facts.Bases.ContainsKey("zz_base_c"));
        var talk = Assert.Single(facts.Conversations).Value;
        Assert.True(facts.Conversations.ContainsKey(@"DATA\SCRIPTS\GCS\ZZ_talk.thn"));
        Assert.Equal((FLGender.male, Posture.Stand), (talk.Gender, talk.Posture));
        Assert.Equal(["seg_a", "seg_b"], talk.Segments);
        Assert.True(facts.Intro.Contains(@"DATA\SCRIPTS\INTRO\zz_intro.thn"));
        Assert.True(facts.Unsaved.Contains(@"DATA\MODS\moda\CUTSCENES\zz_new.thn"));
    }

    private static void Add<T>(GameItemCollection<T> collection, T item) where T : IdentifiableItem
    {
        item.CRC = FLHash.CreateID(item.Nickname);
        collection.Add(item);
    }

    /// <summary>The text of each category for its header and its info icon.</summary>
    [Fact]
    public void EachCategoryHasALabelAndATooltip()
    {
        foreach (var category in Enum.GetValues<CutsceneCategory>())
        {
            Assert.NotEmpty(CutsceneCategories.Label(category));
            Assert.EndsWith(".", CutsceneCategories.Tooltip(category), StringComparison.Ordinal);
            Assert.DoesNotContain(";", CutsceneCategories.Tooltip(category), StringComparison.Ordinal);
        }

        Assert.Equal("LI", CutsceneCategories.RegionOf("Li07"));
        Assert.Equal("ZZ", CutsceneCategories.RegionOf("Zz03b"));
        Assert.Equal("NODIGIT", CutsceneCategories.RegionOf("nodigit"));
    }
}

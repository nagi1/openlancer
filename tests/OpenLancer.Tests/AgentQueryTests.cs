using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The query tools of section 7.1 of the MCP1 design on synthetic profiles (section 13.1, items 1, 8, 10, 11 and 12):
/// each result fits the output schema of its tool, each reference of a result resolves, and each refusal has its code.
/// </summary>
public sealed class AgentQueryTests
{
    private static (TestWorkspace Ws, TestAgentHost Host) Scene()
    {
        var ws = SceneWorkspace.Create();
        return (ws, new TestAgentHost(ws.Session("act")));
    }

    [Fact]
    public void AToolThatNeedsAProfileGivesNotAvailableWithoutOne()
    {
        Assert.Equal(("not_available", "NoProfile"), Code(Error(new TestAgentHost(null), "list_systems")));
    }

    [Fact]
    public void ListSystemsPagesAndFilters()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var all = Ok(host, "list_systems");
        var first = Ok(host, "list_systems", """{ "limit": 1 }""");
        var second = Ok(host, "list_systems", """{ "limit": 1, "cursor": "1" }""");
        var filtered = Ok(host, "list_systems", """{ "text": "li02" }""");

        Assert.Equal(["system:Li01", "system:Li02"], Refs(all));
        Assert.Equal(ListWorkspace.Li01Path, (string?)all["rows"]![0]!["file"], ignoreCase: true);
        Assert.Equal("base", (string?)all["rows"]![0]!["origin"]!["origin"]);
        Assert.Equal(2, (int)first["total"]!);
        Assert.Equal("1", (string?)first["next_cursor"]);
        Assert.Equal(["system:Li02"], Refs(second));
        Assert.Null(second["next_cursor"]);
        Assert.Equal(["system:Li02"], Refs(filtered));
    }

    [Fact]
    public void AListAboveTheLimitOrABadCursorIsRefused()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var limit = Error(host, "list_systems", """{ "limit": 201 }""");
        var smaller = Error(new TestAgentHost(host.Session) { TestLimits = new AgentLimits { MaxListRows = 1 } },
            "list_systems", """{ "limit": 2 }""");

        Assert.Equal(("limit", "ListRows"), Code(limit));
        Assert.Equal("One call can give at most 200 rows. Use a smaller limit and the cursor of the result.", limit.Message);
        Assert.Equal(("limit", "ListRows"), Code(smaller));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "list_systems", """{ "cursor": "x" }""")));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "list_systems", """{ "cursor": "9" }""")));
    }

    [Fact]
    public void GetSystemGivesCountsBasesConnectionsAndFields()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var system = Ok(host, "get_system", """{ "system": "li01" }""");
        var missing = Error(host, "get_system", """{ "system": "Li0x" }""");

        Assert.Equal("system:Li01", (string?)system["ref"]);
        Assert.Equal(ListWorkspace.Li01Path, (string?)system["file"], ignoreCase: true);
        Assert.Equal(4, (int)system["counts"]!["zone"]!);
        Assert.Equal(3, (int)system["counts"]!["light"]!);
        Assert.Equal(1, (int)system["object_kinds"]!["gate"]!);
        Assert.Equal(2, (int)system["object_kinds"]!["lane_ring"]!);
        Assert.Equal(["base:Li01_01_Base"], system["bases"]!.AsArray().Select(b => (string)b!));
        var gate = system["connections"]!.AsArray().Single(c => (string?)c!["kind"] == "gate")!;
        Assert.Equal(("obj:Li01_to_Li02", "Li02", "Li02_to_Li01"),
            ((string?)gate["ref"], (string?)gate["to_system"], (string?)gate["to_object"]));
        Assert.Equal(["nebula:Zone_cloud"], system["nebulae"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["field:Zone_cloud"], system["asteroid_fields"]!.AsArray().Select(n => (string)n!));
        Assert.Contains(system["fields"]!.AsArray(), f => (string?)f!["key"] == "file");
        Assert.Equal(("not_found", ItemRefs.NotFound), Code(missing));
        Assert.Contains("Li01", missing.Message);
    }

    [Fact]
    public void ListItemsFiltersByKindObjectKindTextAndPlace()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var zones = Ok(host, "list_items", """{ "system": "Li01", "kinds": ["zone"] }""");
        var gates = Ok(host, "list_items", """{ "system": "Li01", "object_kinds": ["gate"] }""");
        var text = Ok(host, "list_items", """{ "system": "Li01", "text": "child" }""");
        var near = Ok(host, "list_items", """{ "system": "Li01", "kinds": ["zone"], "near": { "point": [100, 0, 0], "radius": 10 } }""");
        var station = Ok(host, "list_items", """{ "system": "Li01", "text": "Li01_01", "kinds": ["object"] }""")["rows"]![0]!;

        Assert.Equal(["zone:Zone_ring", "zone:Zone_cloud", "zone:Zone_hole", "zone:Zone_Tradelane_a"], Refs(zones));
        Assert.Equal(["obj:Li01_to_Li02"], Refs(gates));
        Assert.Equal(["obj:Li01_child", "obj:Li01_grandchild"], Refs(text));
        Assert.Equal(["zone:Zone_hole"], Refs(near));
        Assert.Equal("station", (string?)station["object_kind"]);
        Assert.Equal("station1", (string?)station["archetype"]);
        Assert.Contains("base_object", station["components"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(("invalid_argument", "BadArgument"),
            Code(Error(host, "list_items", """{ "system": "Li01", "kinds": ["planet"] }""")));
    }

    [Fact]
    public void EachReferenceOfAListResolvesWithGetItem()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var rows = Ok(host, "list_items", """{ "system": "Li01", "limit": 200 }""");

        foreach (var reference in Refs(rows))
        {
            var item = Ok(host, "get_item", $$"""{ "ref": {{Json(reference!)}} }""");
            Assert.Equal(reference, (string?)item["section"]!["ref"]);
        }
        Assert.Contains("light:Li01/light1", Refs(rows));
    }

    [Fact]
    public void GetItemGivesFieldsComponentsChildrenUsersAndRawText()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var item = Ok(host, "get_item", """{ "ref": "obj:Li01_01", "include_raw": true, "include_used_by": true }""")["section"]!;
        var plain = Ok(host, "get_item", """{ "ref": "obj:Li01_01" }""")["section"]!;

        Assert.Equal("Object", (string?)item["type"]);
        var archetype = item["fields"]!.AsArray().Single(f => (string?)f!["key"] == "archetype")!;
        Assert.Equal("""[["station1"]]""", archetype["lines"]!.ToJsonString());
        Assert.Equal("Solar archetype", (string?)archetype["label"]);
        Assert.False((bool)archetype["locked"]!);
        Assert.True((bool)item["fields"]!.AsArray().Single(f => (string?)f!["key"] == "nickname")!["locked"]!);
        Assert.Contains(item["components"]!.AsArray(), c => (string?)c!["kind"] == "dockable_station");
        Assert.Equal(["obj:Li01_child"], item["children"]!.AsArray().Select(c => (string)c!));
        Assert.StartsWith("[Object]\nnickname = Li01_01", (string?)item["raw"]);
        Assert.Contains(item["used_by"]!.AsArray(), r => (string?)r!["ref"] == "obj:Li01_child" && (string?)r["key"] == "parent");
        Assert.NotNull(item["mentions"]);
        Assert.Null(plain["raw"]);
        Assert.Null(plain["used_by"]);
    }

    [Fact]
    public void GetItemMarksTheLinesOfAnUnsavedEdit()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        var station = ListWorkspace.Li01("Li01_01");
        host.Session!.Execute(OpenLancer.Data.Editing.EditCommand.SetLinesFromText(station, "loadout", ["lo_other"]));

        var item = Ok(host, "get_item", """{ "ref": "obj:Li01_01" }""")["section"]!;
        var row = Ok(host, "list_items", """{ "system": "Li01", "text": "Li01_01", "kinds": ["object"] }""")["rows"]![0]!;

        Assert.True((bool)item["fields"]!.AsArray().Single(f => (string?)f!["key"] == "loadout")!["unsaved"]!);
        Assert.False((bool)item["fields"]!.AsArray().Single(f => (string?)f!["key"] == "archetype")!["unsaved"]!);
        Assert.Equal("active_mod", (string?)row["origin"]!["origin"]);
        Assert.True((bool)row["origin"]!["unsaved"]!);
    }

    [Fact]
    public void FindReferencesTakesAReferenceOrAName()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var byRef = Ok(host, "find_references", """{ "ref": "obj:Li01_01" }""");
        var byName = Ok(host, "find_references", """{ "name": "li_n_grp" }""");

        Assert.Contains(byRef["lines"]!.AsArray(), l => (string?)l!["ref"] == "obj:Li01_child" && (string?)l["list"] == "object_of_system");
        Assert.Contains(byName["lines"]!.AsArray(), l => (string?)l!["ref"] == "obj:Li01_01" && (string?)l["key"] == "reputation");
        Assert.Equal(("invalid_argument", ItemRefs.BadReference),
            Code(Error(host, "find_references", """{ "ref": "obj:Li01_01", "name": "x" }""")));
        Assert.Equal(("invalid_argument", ItemRefs.BadReference), Code(Error(host, "find_references")));
    }

    [Fact]
    public void ListValuesFindsTheDetailAndNeedsTheSystemOfAListOfOneSystem()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var stations = Ok(host, "list_values", """{ "list": "solar_archetype", "text": "DOCKING_RING" }""");
        var objects = Ok(host, "list_values", """{ "list": "object_of_system", "system": "Li02" }""");
        var unknown = Error(host, "list_values", """{ "list": "spaceships" }""");

        Assert.Equal(["dock_ring"], stations["rows"]!.AsArray().Select(r => (string)r!["value"]!));
        Assert.Equal($"sec:{ListWorkspace.SolarPath}#Solar/dock_ring", (string?)stations["rows"]![0]!["ref"]);
        Assert.Equal(["Li02_to_Li01", "Li02_01"], objects["rows"]!.AsArray().Select(r => (string)r!["value"]!));
        Assert.Equal(("invalid_argument", "UnknownList"), Code(unknown));
        Assert.Contains("solar_archetype", unknown.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "list_values", """{ "list": "object_of_system" }""")));
    }

    [Fact]
    public void SearchAssetsAndListPropGroups()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var solars = Ok(host, "search_assets", """{ "kind": "solar_archetype", "text": "planet" }""");
        var groups = Ok(host, "list_prop_groups");

        Assert.Contains("asset:solar_archetype/planet1", Refs(solars));
        var asset = Ok(host, "get_item", """{ "ref": "asset:solar_archetype/planet1" }""");
        Assert.Equal("planet1", (string?)asset["asset"]!["nickname"]);
        Assert.Empty(groups["groups"]!.AsArray());
        Assert.Equal(("invalid_argument", "UnknownCategory"), Code(Error(host, "search_assets", """{ "category": "none" }""")));
        Assert.Equal(("not_found", ItemRefs.NotFound), Code(Error(host, "get_item", """{ "ref": "group:none" }""")));
    }

    [Fact]
    public void TheMissionToolsReadTheQueriesOfTheMissionLayer()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        var script = Json("msn:" + MissionEdits.Script);
        var trigger = Json($"trg:{MissionEdits.Script}#ol_wave");

        var missions = Ok(host, "list_missions");
        var summary = Ok(host, "get_mission", $$"""{ "script": {{script}} }""");
        var triggers = Ok(host, "get_mission", $$"""{ "script": {{script}}, "level": "triggers" }""");
        var wave = Ok(host, "get_trigger", $$"""{ "ref": {{trigger}} }""");
        var raw = Ok(host, "get_trigger", $$"""{ "ref": {{trigger}}, "include_lines": true }""");
        var timer = Ok(host, "describe_mission_name", """{ "key": "cnd_timer" }""");
        var item = Ok(host, "get_item", $$"""{ "ref": {{trigger}} }""");

        Assert.Contains("msn:" + MissionEdits.Script, missions["scripts"]!.AsArray().Select(s => (string?)s!["ref"]));
        Assert.Null(summary["triggers"]);
        Assert.Equal(["ol_start", "ol_wave", "ol_end"], triggers["triggers"]!.AsArray().Select(t => (string)t!["nickname"]!));
        Assert.Equal("ol_wave", (string?)wave["nickname"]);
        Assert.All(wave["lines"]!.AsArray(), l => Assert.Empty(l!["texts"]!.AsArray()));
        Assert.Contains(raw["lines"]!.AsArray(), l => l!["texts"]!.AsArray().Count > 0);
        Assert.Equal("Cnd_Timer", (string?)timer["name"]);
        Assert.Equal("trigger", (string?)item["kind"]);
        Assert.Equal(("not_found", "NameNotFound"), Code(Error(host, "describe_mission_name", """{ "key": "Cnd_Timr" }""")));
        Assert.Contains("Cnd_Timer", Error(host, "describe_mission_name", """{ "key": "Cnd_Timr" }""").Message);
    }

    [Fact]
    public void TheCutsceneToolsGiveTheScriptAndItsElements()
    {
        using var ws = ThnSessions.Workspace();
        var host = new TestAgentHost(ws.Session("act"));
        var path = ThnSessions.ScriptPath;

        var list = Ok(host, "list_cutscenes", """{ "text": "x.thn" }""");
        var cutscene = Ok(host, "get_cutscene", $$"""{ "ref": {{Json("thn:" + path)}} }""");
        var camera = Ok(host, "get_item", $$"""{ "ref": {{Json($"thn:{path}#entity/Cam_A")}} }""")["cutscene_element"]!;
        var psys = Ok(host, "get_item", $$"""{ "ref": {{Json($"thn:{path}#event/1")}} }""")["cutscene_element"]!;

        Assert.Equal("thn:" + path, (string?)list["rows"]![0]!["ref"]);
        Assert.Equal(10, (float)list["rows"]![0]!["duration"]!);
        Assert.Equal("base", (string?)list["rows"]![0]!["kind"]);
        Assert.Equal(6, cutscene["entities"]!.AsArray().Count);
        Assert.Equal(6, cutscene["events"]!.AsArray().Count);
        Assert.Equal(("CAMERA", "Cam_A"), ((string?)camera["type"], (string?)camera["name"]));
        Assert.Equal(("START_PSYS", 5f, 2f), ((string?)psys["type"], (float)psys["time"]!, (float)psys["length"]!));
        Assert.Equal(["Fx"], psys["targets"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(("invalid_argument", ItemRefs.BadReference),
            Code(Error(host, "get_item", $$"""{ "ref": {{Json("thn:" + path)}} }""")));
        Assert.Equal(("not_found", ItemRefs.NotFound),
            Code(Error(host, "get_item", $$"""{ "ref": {{Json($"thn:{path}#event/9")}} }""")));
    }

    [Fact]
    public void GetRawIniGivesTheTextOfAFileOrASectionWithMarks()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var file = Ok(host, "get_raw_ini", $$"""{ "file": {{Json(@"data/universe/systems/li01/li01.ini")}} }""");
        var section = Ok(host, "get_raw_ini", """{ "ref": "obj:Li01_01" }""");

        Assert.Equal(ListWorkspace.Li01Path, (string?)file["file"]);
        Assert.Null(file["ref"]);
        Assert.StartsWith("[Object]\nnickname = Li01_01\n", (string?)file["text"]);
        Assert.Equal("obj:Li01_01", (string?)section["ref"]);
        Assert.StartsWith("[Object]", (string?)section["text"]);
        Assert.DoesNotContain("[zone]", (string?)section["text"]);
        Assert.Equal(("not_found", ItemRefs.NotFound),
            Code(Error(host, "get_raw_ini", $$"""{ "file": {{Json(@"DATA\none.ini")}} }""")));
    }

    [Theory]
    [InlineData(@"C:\x.ini")]
    [InlineData("C:x.ini")]
    [InlineData(@"\x.ini")]
    [InlineData("/x.ini")]
    [InlineData(@"\\server\share\x.ini")]
    [InlineData(@"DATA\x.ini:stream")]
    [InlineData(@"..\x.ini")]
    [InlineData(@"DATA\..\..\x.ini")]
    public void APathThatIsNotAnInstallPathIsRefusedByEachToolThatTakesAPath(string path)
    {
        var (ws, host) = Scene();
        using var _ = ws;

        Assert.Equal(("invalid_argument", InstallPaths.NotInstallPath),
            Code(Error(host, "get_raw_ini", $$"""{ "file": {{Json(path)}} }""")));
        Assert.Equal(("invalid_argument", InstallPaths.NotInstallPath),
            Code(Error(host, "get_item", $$"""{ "ref": {{Json($"sec:{path}#Object/x")}} }""")));
        Assert.Equal(("invalid_argument", InstallPaths.NotInstallPath),
            Code(Error(host, "get_cutscene", $$"""{ "ref": {{Json($"thn:{path}")}} }""")));
    }

    [Fact]
    public void CheckGivesTheProblemsOfEachScope()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session)
        {
            Target = t => ToolResult.Of(CheckQueries.Of("target", [], "target " + t)),
        };
        var script = Json("msn:" + MissionEdits.Script);

        var all = Ok(host, "check");
        var mission = Ok(host, "check", $$"""{ "scope": "mission", "ref": {{script}} }""");
        var target = Ok(host, "check", """{ "target": "vanilla-1.1" }""");

        Assert.Equal("all", (string?)all["scope"]);
        Assert.False((bool)all["save_blocked"]!);
        Assert.Equal("mission", (string?)mission["scope"]);
        Assert.Equal("target vanilla-1.1", (string?)target["note"]);
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "check", """{ "scope": "mission" }""")));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "check", $$"""{ "ref": {{script}} }""")));
        Assert.Equal(("invalid_argument", "BadArgument"),
            Code(Error(host, "check", """{ "target": "vanilla-1.1", "scope": "item" }""")));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "check", """{ "target": "openlancer" }""")));
    }

    [Fact]
    public void CheckOfAnItemGivesTheProblemsOfItsSection()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        host.Session!.Execute(OpenLancer.Data.Editing.EditCommand.SetLinesFromText(ListWorkspace.Li01("Li01_01"), "loadout",
            ["lo_missing"]));

        var item = Ok(host, "check", """{ "scope": "item", "ref": "obj:Li01_01" }""");

        Assert.Contains(item["problems"]!.AsArray(), p => ((string?)p!["message"])!.Contains("lo_missing"));
        Assert.Equal("obj:Li01_01", (string?)item["problems"]![0]!["ref"]);
    }

    [Fact]
    public void TheConsoleIsOnlyInTheEditorHost()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        var editor = new TestAgentHost(host.Session, AgentHostKind.Editor);
        editor.ConsoleEntries.Add(new AgentConsoleEntry(1, new System.DateTime(2026, 1, 1), "info", "Editor", "Opened.", null,
            OpenLancer.Data.OriginKind.OpenLancer));
        editor.ConsoleEntries.Add(new AgentConsoleEntry(2, new System.DateTime(2026, 1, 1), "warning", "Agent", "x.", "obj:Li01_01",
            OpenLancer.Data.OriginKind.OpenLancer));

        var entries = Ok(editor, "get_console", """{ "since": 1 }""");

        Assert.Equal(("not_available", "EditorOnly"), Code(Error(host, "get_console")));
        Assert.Equal([2L], entries["entries"]!.AsArray().Select(e => (long)e!["sequence"]!));
        Assert.Equal("openlancer", (string?)entries["entries"]![0]!["origin"]);
    }

    [Fact]
    public void AHiddenGroupGivesNotAvailableAndTheStudioGroupStays()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        host.Hidden.Add(ToolGroup.Scene);

        var hidden = Error(host, "list_systems");

        Assert.Equal(("not_available", "GroupHidden"), Code(hidden));
        Assert.Equal("The user hid the tool group 'scene'. Ask the user to show it in the agent settings.", hidden.Message);
        Assert.Equal(["studio", "missions", "cutscenes"], Ok(host, "studio_status")["tool_groups"]!.AsArray().Select(g => (string)g!));
    }

    [Fact]
    public void AnArgumentThatDoesNotFitTheSchemaIsRefusedWithItsName()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var unknown = Error(host, "get_system", """{ "system": "Li01", "sytem": "x" }""");
        var missing = Error(host, "get_system");
        var type = Error(host, "list_systems", """{ "limit": "ten" }""");
        var nested = Error(host, "list_items", """{ "system": "Li01", "near": { "point": [1, 2] , "radius": 1 } }""");

        Assert.All(new[] { unknown, missing, type, nested }, e => Assert.Equal(("invalid_argument", "BadArgument"), Code(e)));
        Assert.Equal("The tool get_system has no argument 'sytem'. Its arguments are: system.", unknown.Message);
        Assert.Equal("The argument 'system' is necessary. Give it, then call the tool again.", missing.Message);
        Assert.Equal("The argument 'limit' needs a whole number from 1.", type.Message);
        Assert.Equal("The argument 'near.point' needs three numbers.", nested.Message);
    }

    private static string[] Refs(JsonObject page) =>
        (page["rows"] ?? page["groups"])!.AsArray().Select(r => (string)r!["ref"]!).ToArray();

    private static (string?, string?) Code((string? Code, string? Detail, string? Message) error) => (error.Code, error.Detail);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The THN patch format (MOD_FORMAT.md, "Cutscenes"): make, write, read and apply, with one test for each merge rule of
/// <see cref="ThnPatch"/>. Each patch is Lua text that the engine VM evaluates, as the merge reads a patch file.
/// </summary>
public sealed class ThnPatchTests
{
    /// <summary>Two entities repeat a name in another letter case, and two events repeat a key.</summary>
    private static readonly string BaseText = """
        duration = 10
        entities = {
            { entity_name = "Cam_A", type = CAMERA, cameraprops = { fovh = 10, hvaspect = 1.5 }, spatialprops = { pos = { 0, 0, 0 } } },
            { entity_name = "Cam_B", type = CAMERA, cameraprops = { fovh = 30 } },
            { entity_name = "Monitor", type = MONITOR },
            { entity_name = "Fx", type = PSYS, psysprops = {} },
            { entity_name = "Mark", type = MARKER },
            { entity_name = "MARK", type = MARKER, userprops = { note = "second" } }
        }
        events = {
            { 0, SET_CAMERA, { "Monitor", "Cam_A" } },
            { 5, SET_CAMERA, { "Monitor", "Cam_B" } },
            { 5, START_PSYS, { "Fx" }, { duration = 2 } },
            { 5, START_PSYS, { "Fx" }, { duration = 3 } }
        }
        """.ReplaceLineEndings("\n");

    private const string FxKey = "{ 5, START_PSYS, { \"Fx\" } }";

    /// <summary>One source of ids for the models of a test, so that a new element never gets the id of another.</summary>
    private readonly Func<ThnElementId> ids = ThnElementId.Counter();

    private ThnScriptModel Model(string text) => ThnScriptModel.From(Evaluate(text), ids);

    private ThnScriptModel Base() => Model(BaseText);

    private static ThnPatch Read(string text) => ThnPatch.Read(Encoding.ASCII.GetBytes(text), "test.thn", Evaluator);

    private ThnPatchResult Apply(string patch, ThnScriptModel? model = null) =>
        Read(patch).Apply(model ?? Base(), Evaluator.Symbols, ids);

    /// <summary>The base text with each pair of texts replaced.</summary>
    private static string Changed(params (string Old, string New)[] changes)
    {
        var text = BaseText;
        foreach (var (old, value) in changes)
        {
            Assert.Contains(old, text);
            text = text.Replace(old, value);
        }
        return text;
    }

    /// <summary>Checks the script exactly, also the key order. Equal trees write the same text, so the texts show the
    /// difference.</summary>
    private static void AssertScript(string expected, ThnScriptModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(ThnWriter.Write(Evaluate(expected)), ThnWriter.Write(actual.ToGlobals()));
    }

    private static ThnPatchResult Applied(ThnPatchResult result)
    {
        Assert.True(result.Errors.Count == 0, string.Join("\n", result.Errors));
        return result;
    }

    // Sets.

    [Fact]
    public void ASetChangesAValueInItsPlace()
    {
        var result = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20, ["spatialprops.pos"] = { 1, 2, 3 } } } }
            """));

        AssertScript(Changed(("fovh = 10", "fovh = 20"), ("pos = { 0, 0, 0 }", "pos = { 1, 2, 3 }")), result.Model);
        Assert.Empty(result.Warnings);
    }

    /// <summary>An empty table is a record, so a path goes into it. A missing record on the path is made at the end of
    /// its record.</summary>
    [Fact]
    public void ASetGoesIntoAnEmptyRecordAndMakesAMissingRecord()
    {
        var result = Applied(Apply("""
            ol_thn_patch = 1
            entities = {
                { ol_match = "Fx", ol_set = { ["psysprops.sparam"] = 0.5 } },
                { ol_match = "Cam_B", ol_set = { ["userprops.category"] = "x" } }
            }
            """));

        AssertScript(Changed(("psysprops = {}", "psysprops = { sparam = 0.5 }"),
            ("cameraprops = { fovh = 30 } }", "cameraprops = { fovh = 30 }, userprops = { category = \"x\" } }")), result.Model);
    }

    /// <summary>SET_CAMERA has no props table in 3,270 retail events. A props path makes it.</summary>
    [Fact]
    public void APropsPathMakesThePropsOfAnEventWithoutProps()
    {
        var result = Applied(Apply("""
            ol_thn_patch = 1
            events = { { ol_match = { 0, SET_CAMERA, { "Monitor", "Cam_A" } }, ol_set = { ["props.duration"] = 1 } } }
            """));

        AssertScript(Changed(("{ 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } }", "{ 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" }, { duration = 1 } }")),
            result.Model);
    }

    /// <summary>Rule 3: a set into a value that is not a record is an error. Any error stops the whole patch.</summary>
    [Fact]
    public void ASetIntoAValueThatIsNotARecordIsAnErrorAndThePatchDoesNotApply()
    {
        var result = Apply("""
            ol_thn_patch = 1
            entities = {
                { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20 } },
                { ol_match = "Cam_B", ol_set = { ["cameraprops.fovh.x"] = 1 } },
                { ol_match = "Fx", ol_set = { ["psysprops.sparam"] = 1 } }
            }
            events = { { ol_match = { 0, SET_CAMERA, { "Monitor", "Cam_A" } }, ol_set = { ["props.offset.x"] = 1, ["props.offset"] = 2 } } }
            """, Model(Changed(("{ 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } }", "{ 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" }, { offset = { 1, 2 } } }"))));

        Assert.Null(result.Model);
        Assert.Equal([
            "entities entry 2: 'cameraprops.fovh.x' goes through 'cameraprops.fovh', which is not a record.",
            "events entry 1: 'props.offset.x' goes through 'props.offset', which is not a record.",
        ], result.Errors);
    }

    // Element changes.

    /// <summary>Rule 3: a remove of a missing key is a warning, and the patch applies.</summary>
    [Fact]
    public void ARemoveDropsAKeyAndAMissingKeyGivesAWarning()
    {
        var result = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_remove = { "cameraprops.hvaspect", "cameraprops.nearplane", "userprops.x" } } }
            events = { { ol_match = { 5, START_PSYS, { "Fx" } }, ol_nth = 2, ol_count = 2, ol_remove = { "props" } } }
            """));

        AssertScript(Changed(("fovh = 10, hvaspect = 1.5", "fovh = 10"),
            ("{ 5, START_PSYS, { \"Fx\" }, { duration = 3 } }", "{ 5, START_PSYS, { \"Fx\" } }")), result.Model);
        Assert.Equal([
            "entity \"Cam_A\": 'cameraprops.nearplane' has no value to remove.",
            "entity \"Cam_A\": 'userprops.x' has no value to remove.",
        ], result.Warnings);
    }

    [Fact]
    public void ADeleteRemovesTheElementAndKeepsTheOthersInOrder()
    {
        var result = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Monitor", ol_delete = Y } }
            events = { { ol_match = { 0, SET_CAMERA, { "Monitor", "Cam_A" } }, ol_delete = Y } }
            """));

        AssertScript(Changed(("{ entity_name = \"Monitor\", type = MONITOR },", ""), ("{ 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } },", "")),
            result.Model);
    }

    /// <summary>Rule 1: new elements go at the end of their list, in patch order, not in time order. The stable sort of
    /// the engine then runs a new event after the events of the same time (ThnScript.cs:150-152).</summary>
    [Fact]
    public void NewElementsGoAtTheEndInPatchOrder()
    {
        var model = Base();
        var result = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_new = { entity_name = "Cam_C", type = CAMERA } } }
            events = {
                { ol_new = { 5, START_PSYS, { "Fx" }, { duration = 9 } } },
                { ol_new = { 1, SET_CAMERA, { "Monitor", "Cam_C" } } }
            }
            """, model));

        AssertScript(Changed(
            ("userprops = { note = \"second\" } }\n}", "userprops = { note = \"second\" } },\n{ entity_name = \"Cam_C\", type = CAMERA }\n}"),
            ("{ 5, START_PSYS, { \"Fx\" }, { duration = 3 } }\n}",
                "{ 5, START_PSYS, { \"Fx\" }, { duration = 3 } },\n{ 5, START_PSYS, { \"Fx\" }, { duration = 9 } },\n{ 1, SET_CAMERA, { \"Monitor\", \"Cam_C\" } }\n}")),
            result.Model);
        // The kept elements keep their ids and origins. A new element gets a new id and the origin -1.
        Assert.Equal(model.Events.Select(e => (e.Id, e.Origin)), result.Model!.Events.Take(4).Select(e => (e.Id, e.Origin)));
        Assert.All(result.Model.Events.Skip(4), e => Assert.Equal(-1, e.Origin));
        var oldIds = model.Entities.Concat(model.Events).Select(e => e.Id).ToHashSet();
        var newIds = result.Model.Entities.Skip(6).Concat(result.Model.Events.Skip(4)).Select(e => e.Id).ToList();
        Assert.Equal(3, newIds.Distinct().Count());
        Assert.DoesNotContain(newIds, oldIds.Contains);
    }

    /// <summary>Rule 5: a rename sets entity_name. The next patch in load order uses the new name.</summary>
    [Fact]
    public void ARenameGivesTheKeyOfTheNextPatch()
    {
        var renamed = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { entity_name = "Cam_C" } } }
            """)).Model!;

        var next = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_C", ol_set = { ["cameraprops.fovh"] = 20 } } }
            """, renamed));
        var old = Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20 } } }
            """, renamed);

        AssertScript(Changed(("\"Cam_A\", type", "\"Cam_C\", type"), ("fovh = 10", "fovh = 20")), next.Model);
        Assert.Equal(["entities entry 1: no entity has the key \"Cam_A\"."], old.Errors);
    }

    /// <summary>A patch cannot insert an event before another event. To run a moved event first, the patch deletes the
    /// event before it and adds it again at the end. The order of the two events of the key changes.</summary>
    [Fact]
    public void AReorderIsADeleteAndANewEvent()
    {
        var lower = Base();
        var moved = lower.Events[2];
        var working = lower with { Events = lower.Events.Remove(moved).Add(moved with { Id = ids(), Origin = -1 }) };

        var patch = ThnPatch.Make(lower, working, Evaluator.Symbols);

        Assert.Equal("""
            ol_thn_patch = 1
            events = {
            	{ ol_match = { 5, START_PSYS, { "Fx" } }, ol_nth = 1, ol_count = 2, ol_delete = Y },
            	{ ol_new = { 5, START_PSYS, { "Fx" }, { duration = 2 } } }
            }

            """.ReplaceLineEndings("\n"), patch.Write());
        AssertScript(Changed(("{ 5, START_PSYS, { \"Fx\" }, { duration = 2 } },\n    { 5, START_PSYS, { \"Fx\" }, { duration = 3 } }",
                "{ 5, START_PSYS, { \"Fx\" }, { duration = 3 } },\n    { 5, START_PSYS, { \"Fx\" }, { duration = 2 } }")),
            Applied(Read(patch.Write()).Apply(lower, Evaluator.Symbols, ids)).Model);
    }

    // Keys.

    /// <summary>Rule 1: all keys resolve against the script before the patch, so a delete does not move a later
    /// match.</summary>
    [Fact]
    public void ADeleteAndAChangeResolveAgainstTheScriptBeforeThePatch()
    {
        var result = Applied(Apply($$"""
            ol_thn_patch = 1
            entities = {
                { ol_match = "Mark", ol_nth = 1, ol_count = 2, ol_delete = Y },
                { ol_match = "mark", ol_nth = 2, ol_count = 2, ol_set = { ["userprops.note"] = "kept" } }
            }
            events = {
                { ol_match = {{FxKey}}, ol_nth = 1, ol_count = 2, ol_delete = Y },
                { ol_match = {{FxKey}}, ol_nth = 2, ol_count = 2, ol_set = { ["props.duration"] = 4 } }
            }
            """));

        AssertScript(Changed(("{ entity_name = \"Mark\", type = MARKER },", ""), ("note = \"second\"", "note = \"kept\""),
            ("{ 5, START_PSYS, { \"Fx\" }, { duration = 2 } },", ""), ("duration = 3", "duration = 4")), result.Model);
    }

    [Fact]
    public void EntityKeysIgnoreLetterCase()
    {
        var result = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "cam_a", ol_set = { ["cameraprops.fovh"] = 20 } } }
            """));

        AssertScript(Changed(("fovh = 10", "fovh = 20")), result.Model);
    }

    /// <summary>The type of an event key compares by its number: a patch with the number finds an event with the
    /// name.</summary>
    [Fact]
    public void AnEventTypeMatchesByItsNumber()
    {
        var result = Applied(Apply($$"""
            ol_thn_patch = 1
            events = { { ol_match = { 5, {{Evaluator.Symbols["SET_CAMERA"].Value}}, { "Monitor", "Cam_B" } }, ol_set = { time = 6 } } }
            """));

        AssertScript(Changed(("{ 5, SET_CAMERA, { \"Monitor\", \"Cam_B\" } }", "{ 6, SET_CAMERA, { \"Monitor\", \"Cam_B\" } }")),
            result.Model);
    }

    /// <summary>Rule 2: a key without an element, or whose number of elements differs from ol_count, is an error. No
    /// entry of the patch applies.</summary>
    [Theory]
    [InlineData("{ ol_match = " + FxKey + ", ol_set = { time = 1 } }",
        "events entry 2: 2 events have the key { 5, START_PSYS, { \"Fx\" } }. Give ol_nth and ol_count to name one of them.")]
    [InlineData("{ ol_match = " + FxKey + ", ol_nth = 1, ol_count = 3, ol_set = { time = 1 } }",
        "events entry 2: 2 events have the key { 5, START_PSYS, { \"Fx\" } }, but the entry expects 3 (ol_count).")]
    [InlineData("{ ol_match = { 5, START_PSYS, { \"Fx2\" } }, ol_delete = Y }",
        "events entry 2: no event has the key { 5, START_PSYS, { \"Fx2\" } }.")]
    [InlineData("{ ol_match = { 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } }, ol_delete = Y }",
        "events entry 2 names the same event as events entry 1.")]
    public void AKeyThatDoesNotNameOneElementIsAnErrorAndNoEntryApplies(string entry, string error)
    {
        var result = Apply($$"""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20 } } }
            events = { { ol_match = { 0, SET_CAMERA, { "Monitor", "Cam_A" } }, ol_set = { time = 1 } }, {{entry}} }
            """);

        Assert.Null(result.Model);
        Assert.Equal([error], result.Errors);
        Assert.Empty(result.Changes);
    }

    // Files.

    /// <summary>Rule 6: an unknown ol_ key is an error. A key or a global that a patch does not have is an error
    /// too.</summary>
    [Theory]
    [InlineData("entities = { { ol_match = \"Cam_A\", ol_sett = { x = 1 } } }", "entities entry 1: unknown key 'ol_sett'.")]
    [InlineData("ol_extra = 1", "Unknown key 'ol_extra'.")]
    [InlineData("entities = { { ol_match = \"Cam_A\", fovh = 20 } }",
        "entities entry 1: 'fovh' is not a key of an entry. An entry has ol_match, ol_nth, ol_count, ol_set, ol_remove, ol_delete, ol_new.")]
    [InlineData("cameras = {}", "The global 'cameras' is not part of a THN patch. A patch has ol_thn_patch, duration, entities and events.")]
    [InlineData("entities = { { ol_match = \"Cam_A\", ol_delete = Y, ol_set = { x = 1 } } }",
        "entities entry 1: ol_delete cannot have ol_set or ol_remove.")]
    [InlineData("entities = { { ol_match = \"Cam_A\", ol_delete = N } }", "entities entry 1: write ol_delete = Y to delete the entity.")]
    [InlineData("entities = { { ol_new = { entity_name = \"X\" }, ol_match = \"X\" } }", "entities entry 1: ol_new cannot have other keys.")]
    [InlineData("events = { { ol_new = { entity_name = \"X\" } } }",
        "events entry 1: ol_new must be the list { time, TYPE, { targets }, { props } } of the new event.")]
    [InlineData("entities = { { ol_match = \"Mark\", ol_nth = 1, ol_set = { x = 1 } } }",
        "entities entry 1: ol_nth and ol_count go together. Each is a whole number from 1, and ol_nth is at most ol_count.")]
    [InlineData("entities = { { ol_match = \"Mark\", ol_nth = 3, ol_count = 2, ol_set = { x = 1 } } }",
        "entities entry 1: ol_nth and ol_count go together. Each is a whole number from 1, and ol_nth is at most ol_count.")]
    [InlineData("entities = { { ol_set = { x = 1 } } }", "entities entry 1 needs ol_match or ol_new.")]
    [InlineData("events = { { ol_match = \"Cam_A\", ol_set = { time = 1 } } }",
        "events entry 1: ol_match must be the key { time, type, { targets } } of the event.")]
    [InlineData("events = { { ol_match = { 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } }, ol_set = { [\"targets.x\"] = 1 } } }",
        "events entry 1: 'targets.x' is not a path of an event, because the targets of an event is a value, not a record.")]
    [InlineData("events = { { ol_match = { 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } }, ol_set = { duration = 1 } } }",
        "events entry 1: 'duration' is not a path of an event. An event path starts with time, type, targets, props.")]
    [InlineData("events = { { ol_match = { 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } }, ol_remove = { \"time\" } } }",
        "events entry 1: An event always has its time, type and targets, so 'time' cannot be removed.")]
    [InlineData("entities = { { ol_match = \"Cam_A\", ol_set = { [\"cameraprops..fovh\"] = 1 } } }",
        "entities entry 1: 'cameraprops..fovh' is not a path. A path is one or more keys joined with '.', and no key is empty.")]
    [InlineData("duration = Y", "'duration' must be a number.")]
    public void AnEntryOrAGlobalThatAPatchDoesNotHaveIsAnError(string text, string problem)
    {
        var e = Assert.Throws<ThnPatchException>(() => Read("ol_thn_patch = 1\n" + text));

        Assert.Equal([problem], e.Problems);
    }

    [Theory]
    [InlineData("entities = {}", "The first statement of a THN patch must be 'ol_thn_patch = 1'.")]
    [InlineData("entities = {}\nol_thn_patch = 1", "The first statement of a THN patch must be 'ol_thn_patch = 1'.")]
    [InlineData("ol_thn_patch = 2", "This OpenLancer reads THN patch format 1. The patch has ol_thn_patch = 2.")]
    public void APatchStartsWithItsVersion(string text, string problem)
    {
        var e = Assert.Throws<ThnPatchException>(() => Read(text));

        Assert.Equal([problem], e.Problems);
    }

    /// <summary>A patch is ASCII Lua text. Bytecode and other characters are refused before the VM runs.</summary>
    [Fact]
    public void APatchMustBeAsciiText()
    {
        var bytecode = Compile("ol_thn_patch = 1");
        var latin = Encoding.Latin1.GetBytes("ol_thn_patch = 1\n-- caf\u00e9");

        foreach (var bytes in new[] { bytecode, latin })
        {
            var e = Assert.Throws<ThnPatchException>(() => ThnPatch.Read(bytes, "test.thn", Evaluator));
            Assert.Equal(["A THN patch must be ASCII text."], e.Problems);
        }
        var broken = Assert.Throws<ThnPatchException>(() => Read("ol_thn_patch = 1\nentities = {"));
        Assert.StartsWith("test.thn: The script does not compile", Assert.Single(broken.Problems));
    }

    /// <summary>The comment block at the top stays with the patch (decision D63). The other comments and the layout of
    /// the text do not.</summary>
    [Fact]
    public void TheHeaderIsTheCommentBlockAtTheTop()
    {
        var patch = Read("-- My mod.\r\n\r\n  -- A second line.\r\n\r\nol_thn_patch = 1 -- the version\r\n-- not in the header\r\n" +
                         "entities = { { ol_match = \"Cam_A\", ol_set = { [\"cameraprops.fovh\"] = 20 } } }\r\n");

        Assert.Equal("-- My mod.\n\n  -- A second line.", patch.Header);
        Assert.Equal("""
            -- My mod.

              -- A second line.
            ol_thn_patch = 1
            entities = {
            	{ ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20 } }
            }

            """.ReplaceLineEndings("\n"), patch.Write());
        Assert.Equal("", Read("ol_thn_patch = 1").Header);
        Assert.Throws<ArgumentException>(() => new ThnPatch("not a comment", null, [], []));
    }

    // Make.

    /// <summary>The patch holds only the changed paths, with the keys of the lower script. This is the patch of the
    /// design (section 6.2) on a small script.</summary>
    [Fact]
    public void MakeWritesOnlyTheChanges()
    {
        var lower = Base();
        var working = lower with
        {
            Entities = lower.Entities.SetItem(0, lower.Entities[0] with
            {
                Table = ThnElementPaths.Set(ThnElementKind.Entity, lower.Entities[0].Table, new ThnPath("cameraprops.fovh"), N(20)),
            }),
            Events = lower.Events.SetItem(1, lower.Events[1] with
            {
                Table = ThnElementPaths.Set(ThnElementKind.Event, lower.Events[1].Table, new ThnPath("time"), N(6)),
            }),
        };

        var patch = ThnPatch.Make(lower, working, Evaluator.Symbols, "-- A wider shot, and a later cut.");

        Assert.Equal("""
            -- A wider shot, and a later cut.
            ol_thn_patch = 1
            entities = {
            	{ ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20 } }
            }
            events = {
            	{ ol_match = { 5, SET_CAMERA, { "Monitor", "Cam_B" } }, ol_set = { time = 6 } }
            }

            """.ReplaceLineEndings("\n"), patch.Write());
        Assert.True(ThnPatch.Make(lower, lower, Evaluator.Symbols).IsEmpty);
        Assert.Equal("ol_thn_patch = 1\nduration = 12\n", ThnPatch.Make(lower, lower with { Duration = 12 }, Evaluator.Symbols).Write());
    }

    /// <summary>A key that moves to the end of its record gets a remove and a set. The removes run first, so the merge
    /// gives the key order of the working script.</summary>
    [Fact]
    public void MakeMovesAKeyToTheEndWithARemoveAndASet()
    {
        var lower = Base();
        var working = Model(Changed(("cameraprops = { fovh = 10, hvaspect = 1.5 }", "cameraprops = { hvaspect = 1.5, fovh = 12 }"),
            ("{ entity_name = \"Cam_B\", type = CAMERA,", "{ type = CAMERA, entity_name = \"Cam_B\",")));
        working = lower with
        {
            Entities = [.. lower.Entities.Select((e, i) => e with { Table = working.Entities[i].Table })],
        };

        var patch = ThnPatch.Make(lower, working, Evaluator.Symbols);
        var merged = Applied(Read(patch.Write()).Apply(lower, Evaluator.Symbols, ids)).Model;

        Assert.Equal("""
            ol_thn_patch = 1
            entities = {
            	{ ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 12 }, ol_remove = { "cameraprops.fovh" } },
            	{ ol_match = "Cam_B", ol_set = { entity_name = "Cam_B", cameraprops = { fovh = 30 } }, ol_remove = { "entity_name", "cameraprops" } }
            }

            """.ReplaceLineEndings("\n"), patch.Write());
        Assert.Equal(working.ToGlobals(), merged!.ToGlobals());
    }

    [Fact]
    public void MakeRefusesAnOrderThatAPatchCannotGive()
    {
        var lower = Base();
        var added = new ThnElement(ids(), -1, Record(F("entity_name", T("New"))));

        Assert.Throws<ArgumentException>(() => ThnPatch.Make(lower, lower with { Entities = lower.Entities.Insert(0, added) },
            Evaluator.Symbols));
        Assert.Throws<ArgumentException>(() => ThnPatch.Make(lower,
            lower with { Entities = lower.Entities.RemoveAt(0).Insert(1, lower.Entities[0]) }, Evaluator.Symbols));
        Assert.Throws<ArgumentException>(() => ThnPatch.Make(lower,
            lower with { OtherGlobals = Record(F("extra", N(1))) }, Evaluator.Symbols));
    }

    /// <summary>A second patch, made on the result of the first, gives the second working script.</summary>
    [Fact]
    public void TwoStackedPatchesGiveTheLastWorkingScript()
    {
        var lower = Base();
        var first = Model(Changed(("fovh = 10", "fovh = 20"), ("{ entity_name = \"Monitor\", type = MONITOR },", "")));
        first = first with
        {
            Entities = [.. first.Entities.Select((e, i) => e with { Id = lower.Entities[i < 2 ? i : i + 1].Id, Origin = i < 2 ? i : i + 1 })],
            Events = lower.Events,
        };
        var patch1 = ThnPatch.Make(lower, first, Evaluator.Symbols);
        var merged1 = Applied(Read(patch1.Write()).Apply(lower, Evaluator.Symbols, ids)).Model!;
        AssertScript(Changed(("fovh = 10", "fovh = 20"), ("{ entity_name = \"Monitor\", type = MONITOR },", "")), merged1);

        var lower2 = ThnScriptModel.From(merged1.ToGlobals(), ids);
        var second = lower2 with
        {
            Duration = 11,
            Events = lower2.Events.RemoveAt(3).Add(new ThnElement(ids(), -1, List(N(7), S("START_PSYS"), List(T("Fx"))))),
        };
        var patch2 = ThnPatch.Make(lower2, second, Evaluator.Symbols);

        var stacked = Applied(Read(patch2.Write()).Apply(Applied(Read(patch1.Write()).Apply(lower, Evaluator.Symbols, ids)).Model!,
            Evaluator.Symbols, ids)).Model!;
        Assert.Equal(second.ToGlobals(), stacked.ToGlobals());
    }

    // The ledger: rules 4 and 7.

    /// <summary>Rule 4: two mods that change the same path, or a path in the value of the other, give a warning that
    /// names both mods. The later mod wins.</summary>
    [Fact]
    public void TwoModsThatChangeTheSamePathGiveAWarningThatNamesBoth()
    {
        var ledger = new ThnLedger();
        var a = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20, ["spatialprops.pos"] = { 1, 1, 1 } } } }
            """));
        Assert.Empty(ledger.Record("x.thn", "a", a));

        var b = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 25, ["cameraprops.hvaspect"] = 2, spatialprops = {} } } }
            """, a.Model));

        Assert.Equal([
            "entity \"Cam_A\": 'cameraprops.fovh' was also changed by mod 'a'. Mod 'b' loads later and wins.",
            "entity \"Cam_A\": 'spatialprops' overlaps 'spatialprops.pos', which mod 'a' also changed. Mod 'b' loads later and wins.",
        ], ledger.Record("x.thn", "b", b));
        // A whole file discards the earlier changes.
        ledger.RecordFile("x.thn");
        Assert.Empty(ledger.Record("x.thn", "c", b));
    }

    /// <summary>Rule 4 for the duration of the script: two mods that set it give a warning that names both mods. The
    /// later mod wins.</summary>
    [Fact]
    public void TwoModsThatSetTheDurationGiveAWarningThatNamesBoth()
    {
        var ledger = new ThnLedger();
        var a = Applied(Apply("ol_thn_patch = 1\nduration = 11"));
        Assert.True(a.SetsDuration);
        Assert.Empty(ledger.Record("x.thn", "a", a));
        // A patch without a duration does not set it.
        var b = Applied(Apply("""
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_B", ol_set = { ["cameraprops.fovh"] = 31 } } }
            """, a.Model));
        Assert.False(b.SetsDuration);
        Assert.Empty(ledger.Record("x.thn", "b", b));

        var c = Applied(Apply("ol_thn_patch = 1\nduration = 12", b.Model));

        Assert.Equal(["'duration' was also changed by mod 'a'. Mod 'c' loads later and wins."], ledger.Record("x.thn", "c", c));
        Assert.Equal(12, c.Model!.Duration);
        // A whole file discards the earlier changes.
        ledger.RecordFile("x.thn");
        Assert.Empty(ledger.Record("x.thn", "d", c));
    }

    /// <summary>
    /// Rule 7 (changed groups): mod 1 deletes the first of two events with one key and adds a new event with that key.
    /// The count stays 2. Mod 2, made against the lower script, sets a path of the first event. Its place now names the
    /// other event. The merge applies the patch and gives one warning that names both mods.
    /// </summary>
    [Fact]
    public void AChangedGroupGivesOneWarningThatNamesBothMods()
    {
        var ledger = new ThnLedger();
        var mod1 = Applied(Apply($$"""
            ol_thn_patch = 1
            events = {
                { ol_match = {{FxKey}}, ol_nth = 1, ol_count = 2, ol_delete = Y },
                { ol_new = { 5, START_PSYS, { "Fx" }, { duration = 8 } } }
            }
            """));
        Assert.Empty(ledger.Record("x.thn", "mod1", mod1));

        var mod2 = Applied(Apply($$"""
            ol_thn_patch = 1
            events = { { ol_match = {{FxKey}}, ol_nth = 1, ol_count = 2, ol_set = { ["props.duration"] = 4 } } }
            """, mod1.Model));

        Assert.Equal([
            "Mod 'mod1' changed the events with the key { 5, START_PSYS, { \"Fx\" } }. The events entry 1 of mod 'mod2' uses " +
            "place 1 of 2. Check the result.",
        ], ledger.Record("x.thn", "mod2", mod2));
        // The place now names the event with duration 3, not the deleted event with duration 2.
        AssertScript(Changed(("{ 5, START_PSYS, { \"Fx\" }, { duration = 2 } },", ""),
            ("{ 5, START_PSYS, { \"Fx\" }, { duration = 3 } }", "{ 5, START_PSYS, { \"Fx\" }, { duration = 4 } },\n{ 5, START_PSYS, { \"Fx\" }, { duration = 8 } }")),
            mod2.Model);
    }

    /// <summary>A change of the props of an event does not change the elements of its key, and an entry without
    /// ol_nth does not depend on a place. Neither gives a changed-group warning.</summary>
    [Fact]
    public void ChangesThatKeepTheElementsOfAKeyGiveNoGroupWarning()
    {
        var ledger = new ThnLedger();
        var mod1 = Applied(Apply($$"""
            ol_thn_patch = 1
            events = {
                { ol_match = {{FxKey}}, ol_nth = 1, ol_count = 2, ol_set = { ["props.duration"] = 5 } },
                { ol_match = { 5, SET_CAMERA, { "Monitor", "Cam_B" } }, ol_delete = Y },
                { ol_new = { 5, SET_CAMERA, { "Monitor", "Cam_B" } } }
            }
            """));
        Assert.Empty(ledger.Record("x.thn", "mod1", mod1));

        var mod2 = Applied(Apply($$"""
            ol_thn_patch = 1
            events = {
                { ol_match = {{FxKey}}, ol_nth = 2, ol_count = 2, ol_set = { ["props.x"] = 1 } },
                { ol_match = { 5, SET_CAMERA, { "Monitor", "Cam_B" } }, ol_set = { ["props.x"] = 1 } }
            }
            """, mod1.Model));

        Assert.Empty(ledger.Record("x.thn", "mod2", mod2));
    }

    /// <summary>A retime moves an event out of the elements of its old key and into the elements of its new key. Both
    /// keys record the change.</summary>
    [Fact]
    public void ARetimeChangesTheGroupsOfTheOldAndTheNewKey()
    {
        var result = Applied(Apply($$"""
            ol_thn_patch = 1
            events = { { ol_match = {{FxKey}}, ol_nth = 2, ol_count = 2, ol_set = { time = 6 } } }
            """));

        var old = ThnKeys.Signature(ThnElementKind.Event, Evaluate("k = " + FxKey)["k"], Evaluator.Symbols);
        var moved = ThnKeys.Signature(ThnElementKind.Event, Evaluate("k = { 6, START_PSYS, { \"Fx\" } }")["k"], Evaluator.Symbols);
        Assert.Equal([new ThnGroupChange(ThnElementKind.Event, old!), new ThnGroupChange(ThnElementKind.Event, moved!)],
            result.Groups);
    }
}

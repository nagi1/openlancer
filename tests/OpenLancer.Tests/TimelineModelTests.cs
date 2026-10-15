using System.Collections.Generic;
using System.Linq;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The Timeline model without ImGui (test 13.1 item 10 of the M8 design): the groups and rows, the monitor lanes with
/// the main monitor, the bars and diamonds, the extents, the hit tests, the events after the end with the gap text, the
/// grey events of a set script and of a fidget, and the camera mark.
/// </summary>
public sealed class TimelineModelTests
{
    internal const string Path = @"DATA\SCRIPTS\STORY\test.thn";

    /// <summary>
    /// A script in the form of s002xe: two monitors, cameras that only the ignored monitor cuts to, and one of them is
    /// the target of a START_SPATIAL_PROP_ANIM. The main monitor is the first by Priority, not the first in the file.
    /// </summary>
    internal const string Script = """
        duration = 30
        entities = {
            { entity_name = "Cam_Monitor_min_spec", type = MONITOR, userprops = { Priority = "2" } },
            { entity_name = "Cam_Monitor", type = MONITOR, userprops = { priority = "1" } },
            { entity_name = "Cam_A", type = CAMERA, cameraprops = { fovh = 10 } },
            { entity_name = "Cam_B", type = CAMERA },
            { entity_name = "Cam_C", type = CAMERA },
            { entity_name = "Cam_D", type = CAMERA },
            { entity_name = "Char_trent", type = DEFORMABLE },
            { entity_name = "Mk_end", type = MARKER },
            { entity_name = "Fx", type = 13 }
        }
        events = {
            { 0, SET_CAMERA, { "Cam_Monitor", "Cam_A" } },
            { 0, SET_CAMERA, { "Cam_Monitor_min_spec", "Cam_B" } },
            { 5, SET_CAMERA, { "cam_monitor_MIN_SPEC", "Cam_C" } },
            { 10, 2, { "Cam_Monitor", "Cam_D" } },
            { 12, SET_CAMERA, { "Cam_Monitor_min_spec", "Cam_D" } },
            { 13.812, START_SPATIAL_PROP_ANIM, { "Cam_A", "Cam_B" }, { duration = 6.406, param_curve = { CLSID = "FreeFormPCurve", points = { { 0, 0, 0, 0 }, { 0.5, 0.5, 0, 0 }, { 1, 1, 0, 0 } } } } },
            { 2, START_MOTION, { "char_TRENT" }, { animation = "Sc_X", duration = 4 } },
            { 3, ATTACH_ENTITY, { "Char_trent", "Mk_end" } },
            { 30.002, START_PSYS, { "Fx" }, { duration = 1 } },
            { 31, START_SOUND, { "Set_sound" } }
        }
        """;

    internal static TimelineModel Model(string script = Script, ScriptPlay play = ScriptPlay.Normal) =>
        TimelineModel.Build(Path, ThnScriptModel.From(ThnScripts.Evaluate(script), ThnElementId.Counter()),
            ThnScripts.Evaluator.Symbols, play);

    private static TimelineRow Row(TimelineModel model, string name) =>
        model.Groups.SelectMany(g => g.Rows).Single(r => r.Name == name);

    private static TimelineEvent Event(TimelineModel model, string type) => model.Events.Single(e => e.Type == type);

    /// <summary>Compares times that the model and the test add in float steps.</summary>
    private static void AssertTimes(float[] expected, IReadOnlyList<float> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i], 0.0001f);
    }

    private static TimelineLine Line(TimelineModel model, string name) =>
        model.Lines(new HashSet<TimelineGroupKind>(), false).Single(l => l.Row?.Name == name || l.Lane?.Name == name);

    [Fact]
    public void TheRowsAreInTheGroupsOfTheirTypesInTheOrderOfTheFile()
    {
        var model = Model();

        Assert.Equal([TimelineGroupKind.Cameras, TimelineGroupKind.Characters, TimelineGroupKind.Effects,
            TimelineGroupKind.Markers, TimelineGroupKind.Missing], model.Groups.Select(g => g.Id));
        // The monitors are lanes, not rows.
        Assert.Equal(["Cam_A", "Cam_B", "Cam_C", "Cam_D"], model.Groups[0].Rows.Select(r => r.Name));
        Assert.Equal(["Cam_Monitor", "Cam_Monitor_min_spec"], model.Groups[0].Lanes.Select(l => l.Name));
        // A type as a number is a type: 13 is PSYS.
        Assert.Equal(ObjectKinds.Thn.Effect, Row(model, "Fx").Kind);
        // An event goes to the row of its first target, without letter case.
        Assert.Equal(["START_MOTION", "ATTACH_ENTITY"], Row(model, "Char_trent").Events.Select(e => e.Type));
        Assert.Empty(Row(model, "Mk_end").Events);
        // A first target that names no entity of the script has a row in the last group.
        var missing = Row(model, "Set_sound");
        Assert.Null(missing.Entity);
        Assert.Equal(["START_SOUND"], missing.Events.Select(e => e.Type));
    }

    [Fact]
    public void TheMainMonitorIsTheFirstByPriorityAndTheOtherLanesAreIgnored()
    {
        var model = Model();
        var (main, other) = (model.Lanes[0], model.Lanes[1]);

        Assert.True(main.Main);
        Assert.True(main.Applies);
        Assert.Equal("Cam_Monitor", main.Name);
        Assert.NotNull(main.Monitor);
        // The cuts of the main lane, with a type as a number, and the shots to the next cut and to the end.
        Assert.Equal([("Cam_A", 0f, 10f), ("Cam_D", 10f, 30f)],
            main.Cuts.Select(c => (c.Camera, c.Event.Time, c.End)));
        Assert.All(main.Cuts, c => Assert.Null(c.Event.Ignored));

        Assert.False(other.Main);
        Assert.False(other.Applies);
        Assert.Equal(["Cam_B", "Cam_C", "Cam_D"], other.Cuts.Select(c => c.Camera));
        Assert.All(other.Cuts, c => Assert.Equal(other.Note, c.Event.Ignored));
        Assert.Contains("SetCameraEvent.cs:24-28", other.Note);
        Assert.Same(main, model.MainLane);
    }

    [Fact]
    public void AnotherPriorityMakesTheOtherMonitorTheMain()
    {
        var model = Model(Script.Replace("Priority = \"2\"", "Priority = \"0\""));

        Assert.Equal("Cam_Monitor_min_spec", model.MainLane!.Name);
        Assert.Equal(3, model.MainLane.Cuts.Count);
    }

    /// <summary>The engine reads the userprops keys without letter case (ThnScript.cs:305-355).</summary>
    [Fact]
    public void TheKeyOfThePriorityHasNoLetterCase()
    {
        var model = Model(Script.Replace("priority = \"1\"", "PRIORITY = \"3\""));

        Assert.Equal("Cam_Monitor_min_spec", model.MainLane!.Name);
    }

    [Fact]
    public void MonitorsWithTheSamePriorityKeepTheOrderOfTheFile()
    {
        var model = Model(Script.Replace("Priority = \"2\"", "Priority = \"1\""));

        Assert.Equal(["Cam_Monitor_min_spec", "Cam_Monitor"], model.Lanes.Select(l => l.Name));
        Assert.True(model.Lanes[0].Main);
    }

    /// <summary>
    /// The camera mark: a camera that only an ignored monitor cuts to gets it, unless another event names it. Cam_B is
    /// the pose target of the spatial animation, as Cam_02_Intro_Trent_B is in s002xe, so it has no mark. Cam_D has a cut
    /// on the main monitor.
    /// </summary>
    [Fact]
    public void OnlyACameraThatNoOtherEventNamesGetsTheMarkOfTheIgnoredMonitor()
    {
        var model = Model();

        Assert.Equal(TimelineModel.IgnoredMonitorMark, Row(model, "Cam_C").Mark);
        Assert.Null(Row(model, "Cam_B").Mark);
        Assert.Null(Row(model, "Cam_D").Mark);
        Assert.Null(Row(model, "Cam_A").Mark);
        Assert.Equal(["Cam_C"], model.MarkedCameras.Select(r => r.Name));
    }

    [Fact]
    public void TheMarkCountsAnyTargetOfAnotherEventButNoCutOfAnIgnoredMonitor()
    {
        // The spatial animation names Cam_B as its first target now, and a second cut of the ignored monitor names it.
        var model = Model(Script.Replace("{ \"Cam_A\", \"Cam_B\" }", "{ \"Cam_B\", \"Cam_A\" }")
            .Replace("{ \"cam_monitor_MIN_SPEC\", \"Cam_C\" }", "{ \"cam_monitor_MIN_SPEC\", \"Cam_B\" }"));

        Assert.Null(Row(model, "Cam_B").Mark);
        Assert.Null(Row(model, "Cam_C").Mark);
        Assert.Empty(model.MarkedCameras);
    }

    [Fact]
    public void AnEventWithALengthIsABarAndTheOthersAreDiamonds()
    {
        var model = Model();
        var motion = Event(model, "START_MOTION");
        var attach = Event(model, "ATTACH_ENTITY");
        var spatial = Event(model, "START_SPATIAL_PROP_ANIM");

        Assert.True(motion.IsBar);
        Assert.Equal((2f, 6f), (motion.Time, motion.End));
        Assert.False(attach.IsBar);
        Assert.Equal(3f, attach.End);
        Assert.Equal("Mk_end", attach.SecondTarget);
        Assert.Empty(attach.Keys);
        // The points of param_curve are fractions of the length.
        AssertTimes([13.812f, 13.812f + 0.5f * 6.406f, 13.812f + 6.406f], spatial.Keys);
        Assert.Equal(Enumerable.Range(0, model.Events.Count), model.Events.Select(e => e.Index));
    }

    [Fact]
    public void ACurveWithAPeriodRepeatsItsKeysInTheBar()
    {
        var model = Model(Script.Replace("duration = 6.406, param_curve", "duration = 2.5, pcurve_period = 1000, param_curve"));

        // The end of one period and the start of the next have one key.
        AssertTimes([13.812f, 14.312f, 14.812f, 15.312f, 15.812f, 16.312f],
            Event(model, "START_SPATIAL_PROP_ANIM").Keys);
    }

    [Fact]
    public void TheEndIsTheDurationOrTheEndOfALaterEvent()
    {
        var model = Model();
        Assert.Equal(30f, model.Duration);
        Assert.Equal(31.002f, model.End, 0.0001f);

        var later = Model(Script.Replace("{ 30.002, START_PSYS, { \"Fx\" }, { duration = 1 } },", "")
            .Replace("{ 31, START_SOUND, { \"Set_sound\" } }", "{ 1, START_SOUND, { \"Set_sound\" } }"));
        Assert.Equal(30f, later.End);
    }

    /// <summary>
    /// An event after the end tells its gap and the frame rule. At 60 frames per second, the last frame of a 30 s
    /// script starts at 30 s and ends at 30.017 s, so an event 2 ms after the end runs and an event 1 s after it does
    /// not. Events before the end have no text.
    /// </summary>
    [Fact]
    public void AnEventAfterTheEndHasTheGapAndTheFrameRule()
    {
        var model = Model();

        Assert.Equal(
            "Starts 2 ms after the end. The game runs it only when the last frame passes this time, so it depends on " +
            "the frame rate. At a steady 60 frames per second, the last frame ends at 30.017 s, so the game runs it. " +
            "The preview stops at the end, so it does not run it.", Event(model, "START_PSYS").AfterEnd);
        Assert.StartsWith("Starts 1 s after the end.", Event(model, "START_SOUND").AfterEnd);
        Assert.Contains("so the game does not run it.", Event(model, "START_SOUND").AfterEnd);
        Assert.All(model.Events.Where(e => e.Time <= model.Duration), e => Assert.Null(e.AfterEnd));
        Assert.Contains(Event(model, "START_PSYS").AfterEnd!, TimelineModel.Describe(Event(model, "START_PSYS")));
    }

    [Theory]
    [InlineData(92.75f, 92.751f, "1 ms", true)]
    [InlineData(92.75f, 92.771f, "21 ms", false)]
    [InlineData(10f, 10.0000005f, "less than 1 ms", true)]
    [InlineData(10f, 12.5f, "2.5 s", false)]
    public void TheGapTextRoundsToWholeMilliseconds(float duration, float time, string gap, bool runs)
    {
        var text = TimelineModel.GapText(time, duration);

        Assert.StartsWith($"Starts {gap} after the end.", text);
        Assert.Contains(runs ? "so the game runs it." : "so the game does not run it.", text);
    }

    /// <summary>The events of a set script are grey, also the cuts of the main monitor: the Timeline greys a cut with
    /// its shot by the reason of the cut, not by the lane.</summary>
    [Fact]
    public void TheEventsOfASetScriptAreGrey()
    {
        var model = Model(play: ScriptPlay.Set);

        Assert.All(model.Events, e => Assert.Contains("Cutscene.cs:279-283", e.Ignored));
        Assert.True(model.MainLane!.Applies);
        Assert.All(model.MainLane.Cuts, c => Assert.Contains("Cutscene.cs:279-283", c.Event.Ignored));
    }

    [Fact]
    public void AFidgetRunsOnlyItsMotions()
    {
        var model = Model(play: ScriptPlay.Fidget);

        Assert.Null(Event(model, "START_MOTION").Ignored);
        Assert.All(model.Events.Where(e => e.Kind != EventTypes.StartMotion),
            e => Assert.Contains("Cutscene.cs:229-241", e.Ignored));
        Assert.All(model.MainLane!.Cuts, c => Assert.Contains("Cutscene.cs:229-241", c.Event.Ignored));
    }

    /// <summary>
    /// The plan of the preview tells how it plays the open script, and the Timeline greys the events that the game does
    /// not run in that play: the set script of the room, the fidget, or else a script with all its events. The path
    /// compares without letter case, as an install path does.
    /// </summary>
    [Fact]
    public void ThePlanGivesThePlayOfTheOpenScript()
    {
        const string ambient = @"DATA\SCRIPTS\BASES\ambi.thn";
        var none = new Dictionary<string, string>();
        var set = new PreviewPlan(Path.ToUpperInvariant(), [ambient], null, "Camera_0", null, null, none);
        var fidget = new PreviewPlan(null, [], null, null, null, null, none) { Fidget = Path };
        var cutscene = new PreviewPlan(@"DATA\SCRIPTS\BASES\set.thn", [ambient, Path], Path, null, null, null, none);

        Assert.Equal(ScriptPlay.Set, set.PlayOf(Path));
        Assert.Equal(ScriptPlay.Normal, set.PlayOf(ambient));
        Assert.Equal(ScriptPlay.Fidget, fidget.PlayOf(Path));
        Assert.Equal(ScriptPlay.Normal, cutscene.PlayOf(Path));
        Assert.Equal(10, Model(play: set.PlayOf(Path)).Events.Count(e => e.Ignored != null));
        Assert.Equal(9, Model(play: fidget.PlayOf(Path)).Events.Count(e => e.Ignored != null));
        Assert.Equal(3, Model(play: cutscene.PlayOf(Path)).Events.Count(e => e.Ignored != null));
    }

    /// <summary>
    /// Each event shows exactly once: a cut on its monitor lane, or an event on the row of its first target. An event
    /// on a monitor that is not a cut, for example a SET_CAMERA without a camera, gives the monitor a row next to its
    /// lane. An event without a target is on the row of no target.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("""
        { 4, START_SPATIAL_PROP_ANIM, { "cam_monitor" }, { duration = 1 } },
        { 7, SET_CAMERA, { "Cam_Monitor_min_spec" } },
        { 8, START_SOUND, {} },
        """)]
    public void EachEventIsOnOneLaneOrOneRow(string moreEvents)
    {
        var model = Model(Script.Replace("events = {", "events = {\n" + moreEvents));

        var shown = model.Lanes.SelectMany(l => l.Cuts).Select(c => c.Event.Index)
            .Concat(model.Groups.SelectMany(g => g.Rows).SelectMany(r => r.Events).Select(e => e.Index))
            .Order().ToList();

        Assert.Equal(Enumerable.Range(0, model.Events.Count), shown);
        Assert.Equal(5, model.Lanes.Sum(l => l.Cuts.Count));
        var monitors = model.Groups[0].Rows.Where(r => r.Kind == ObjectKinds.Thn.Monitor).ToList();
        if (moreEvents.Length == 0)
        {
            // A monitor with only cuts has no row.
            Assert.Empty(monitors);
            return;
        }

        Assert.Equal(["Cam_Monitor_min_spec", "Cam_Monitor"], monitors.Select(r => r.Name));
        Assert.Equal(["SET_CAMERA"], monitors[0].Events.Select(e => e.Type));
        Assert.Equal(["START_SPATIAL_PROP_ANIM"], monitors[1].Events.Select(e => e.Type));
        Assert.Equal(["START_SOUND"], Row(model, TimelineModel.NoTarget).Events.Select(e => e.Type));
    }

    [Fact]
    public void ANormalScriptHasGreyEventsOnlyOnIgnoredMonitors()
    {
        var model = Model();

        Assert.Equal(3, model.Events.Count(e => e.Ignored != null));
        Assert.All(model.Events.Where(e => e.Ignored != null), e => Assert.Equal(EventTypes.SetCamera, e.Kind));
    }

    /// <summary>
    /// A diamond takes the tolerance on each side and wins over a bar. A bar takes the tolerance at its ends. A lane
    /// gives the cut of a diamond, or else the cut of the shot under the time.
    /// </summary>
    [Fact]
    public void AHitTestFindsTheDiamondFirstThenTheBarAndOnALaneTheShot()
    {
        var model = Model();
        var trent = Line(model, "Char_trent");
        var main = Line(model, "Cam_Monitor");

        Assert.Equal("ATTACH_ENTITY", TimelineModel.HitTest(trent, 3.04, 0.05)?.Type);
        Assert.Equal("START_MOTION", TimelineModel.HitTest(trent, 4.5, 0.05)?.Type);
        Assert.Equal("START_MOTION", TimelineModel.HitTest(trent, 6.04, 0.05)?.Type);
        Assert.Equal("START_MOTION", TimelineModel.HitTest(trent, 1.96, 0.05)?.Type);
        Assert.Null(TimelineModel.HitTest(trent, 6.06, 0.05));
        Assert.Null(TimelineModel.HitTest(trent, 1.9, 0.05));

        Assert.Equal(10f, TimelineModel.HitTest(main, 9.97, 0.05)?.Time);
        Assert.Equal(0f, TimelineModel.HitTest(main, 5, 0.05)?.Time);
        Assert.Equal(10f, TimelineModel.HitTest(main, 29.9, 0.05)?.Time);
        Assert.Null(TimelineModel.HitTest(main, 30.5, 0.05));
        // A group header has no events.
        var header = model.Lines(new HashSet<TimelineGroupKind>(), false)[0];
        Assert.True(header.IsHeader);
        Assert.Null(TimelineModel.HitTest(header, 0, 1));
    }

    [Fact]
    public void ALaterEventOnTheSameRowWinsTheHitTest()
    {
        var model = Model(Script.Replace("{ 3, ATTACH_ENTITY, { \"Char_trent\", \"Mk_end\" } },",
            "{ 3, START_MOTION, { \"Char_trent\" }, { animation = \"Sc_Y\", duration = 1 } },"));

        var hit = TimelineModel.HitTest(Line(model, "Char_trent"), 3.5, 0.05);

        Assert.Equal(3f, hit?.Time);
    }

    [Fact]
    public void TheLinesFollowTheCollapsedGroupsAndTheEventFilter()
    {
        var model = Model();

        var all = model.Lines(new HashSet<TimelineGroupKind>(), false);
        var filtered = model.Lines(new HashSet<TimelineGroupKind>(), true);
        var collapsed = model.Lines(new HashSet<TimelineGroupKind> { TimelineGroupKind.Cameras }, true);

        Assert.Contains(all, l => l.Row?.Name == "Mk_end");
        // Without events, the marker and its group do not show. The lanes always show.
        Assert.DoesNotContain(filtered, l => l.Group.Id == TimelineGroupKind.Markers);
        Assert.Equal(2, filtered.Count(l => l.Lane != null));
        Assert.DoesNotContain(filtered, l => l.Row?.Name == "Cam_C");
        Assert.Contains(filtered, l => l.Row?.Name == "Cam_A");
        // A collapsed group shows only its header.
        Assert.Single(collapsed, l => l.Group.Id == TimelineGroupKind.Cameras);
        Assert.True(collapsed.Single(l => l.Group.Id == TimelineGroupKind.Cameras).IsHeader);
        Assert.Equal(15, all.Count);
        Assert.Equal(10, filtered.Count);
        Assert.Equal(7, collapsed.Count);
    }

    [Fact]
    public void AnEntityThatALaterEntityHidesHasTheMarkAndNoEvents()
    {
        var model = Model(Script.Replace("{ entity_name = \"Mk_end\", type = MARKER },",
            "{ entity_name = \"Mk_end\", type = MARKER }, { entity_name = \"char_trent\", type = DEFORMABLE },"));

        var rows = model.Groups.Single(g => g.Id == TimelineGroupKind.Characters).Rows;

        Assert.Equal(["Char_trent", "char_trent"], rows.Select(r => r.Name));
        Assert.Equal(TimelineModel.HiddenEntityMark, rows[0].Mark);
        Assert.Empty(rows[0].Events);
        Assert.Equal(2, rows[1].Events.Count);
        Assert.Equal(rows[1], model.RowOf("CHAR_TRENT")!.Value.Group.Rows[model.RowOf("CHAR_TRENT")!.Value.Index]);
    }

    [Fact]
    public void ACutOnANameThatNoEntityHasAppliesAndACutOnAnotherEntityDoesNot()
    {
        var model = Model(Script.Replace("{ 0, SET_CAMERA, { \"Cam_Monitor\", \"Cam_A\" } },",
            "{ 0, SET_CAMERA, { \"Cam_Monitor\", \"Cam_A\" } }, { 1, SET_CAMERA, { \"Set_monitor\", \"Cam_A\" } }, " +
            "{ 2, SET_CAMERA, { \"Mk_end\", \"Cam_A\" } },"));

        var unknown = model.Lanes.Single(l => l.Name == "Set_monitor");
        var marker = model.Lanes.Single(l => l.Name == "Mk_end");

        Assert.Null(unknown.Monitor);
        Assert.True(unknown.Applies);
        Assert.False(unknown.Main);
        Assert.Null(unknown.Cuts[0].Event.Ignored);
        Assert.False(marker.Applies);
        Assert.Contains("SetCameraEvent.cs:19-23", marker.Cuts[0].Event.Ignored);
    }

    [Fact]
    public void TheSummaryHasTheCountsOfTheRowsTheLanesAndTheCutsAndTheMarkedCameras()
    {
        var model = Model();

        Assert.Equal(
            "Timeline: 8 rows (4 with events) in 5 groups, 2 monitor lanes, 5 cuts (2 on the main monitor), 10 events " +
            "(2 after the end, 3 that the game does not run here). Cameras that only an ignored monitor uses: Cam_C.",
            model.Summary);
    }

    [Fact]
    public void TheKindsOfTheEntitiesComeFromTheirTypes()
    {
        var model = Model();
        var w = ThnScriptModel.From(ThnScripts.Evaluate(Script), ThnElementId.Counter());

        // The ids of a new model of the same text are the same, because the counter starts at 0.
        Assert.Equal(ObjectKinds.Thn.Monitor, model.KindOf(w.Entities[0].Id));
        Assert.Equal(ObjectKinds.Thn.Camera, model.KindOf(w.Entities[2].Id));
        Assert.Equal(ObjectKinds.Thn.Effect, model.KindOf(w.Entities[8].Id));
        Assert.Equal(model.Events[6], model.Event(w.Events[6].Id));
    }
}

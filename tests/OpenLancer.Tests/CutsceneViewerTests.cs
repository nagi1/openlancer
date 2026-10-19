using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer.Data.IO;
using LibreLancer.Render.Cameras;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Validation;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The parts of the read-only cutscene viewer that need no GPU: the selection of the center tabs, the scan and the
/// state of the Cutscenes tab, the sections of Used by, the line of an open, the view of the Timeline, the drags of the
/// playhead and of the loop range, the free camera, and the clip and the markers of the Free mode.
/// </summary>
public sealed class CutsceneViewerTests
{
    private static readonly ThnRef Element = new(TimelineModelTests.Path, ThnElementKind.Entity, new ThnElementId(3));
    private static readonly CutsceneRoot Root = new(TimelineModelTests.Path);
    private static readonly object SceneItem = new();

    /// <summary>
    /// The Scene and the Cutscene tab each get back their own selection. A selection that no tab owns belongs to the tab
    /// that showed when the user made it (section 4.2 of the M6a design).
    /// </summary>
    [Fact]
    public void EachCenterTabGetsBackItsOwnSelection()
    {
        // To the Cutscene tab: the scene item waits, and the kept element shows.
        var tabs = Tabs();
        tabs.Park(CenterMode.Cutscene, Element);
        Assert.Equal(Element, tabs.Follow(CenterMode.Cutscene, SceneItem, Valid));
        Assert.Equal(SceneItem, tabs.Kept(CenterMode.Scene));
        // Without a kept element, the cutscene shows.
        Assert.Equal(Root, Tabs().Follow(CenterMode.Cutscene, SceneItem, Valid));
        // A Timeline click while the Scene showed selected an element: it stays.
        tabs = Tabs();
        tabs.Park(CenterMode.Scene, SceneItem);
        Assert.Equal(Element, tabs.Follow(CenterMode.Cutscene, Element, Valid));
        Assert.Equal(SceneItem, tabs.Kept(CenterMode.Scene));

        // To the Scene: the element waits, and the scene item shows.
        Assert.Equal(SceneItem, tabs.Follow(CenterMode.Scene, Element, Valid));
        Assert.Equal(Element, tabs.Kept(CenterMode.Cutscene));
        // A Database click while the Cutscene tab showed selected a section: it stays with the Cutscene tab.
        var section = new object();
        tabs.Follow(CenterMode.Cutscene, Element, Valid);
        Assert.Equal(SceneItem, tabs.Follow(CenterMode.Scene, section, Valid));
        Assert.Equal(section, tabs.Kept(CenterMode.Cutscene));
        Assert.Equal(section, tabs.Follow(CenterMode.Cutscene, SceneItem, Valid));
    }

    /// <summary>The center tabs with the owners of the editor: the Cutscene tab owns the cutscene items.</summary>
    private static CenterSelections Tabs() =>
        new(item => CutsceneWorkspace.IsCutsceneItem(item) ? CenterMode.Cutscene : null);

    /// <summary>A kept cutscene item, or the root of the open cutscene, as the editor validates it.</summary>
    private static object? Valid(CenterMode mode, object? item) =>
        mode == CenterMode.Cutscene ? item ?? Root : item;

    [Theory]
    [InlineData(@"DATA\SCRIPTS\BASES\Li_01_Cityscape_ambi_day_01.thn", "Bases")]
    [InlineData(@"data/scripts/story/s002xe.thn", "Story")]
    [InlineData(@"DATA\Scripts\GCS\x.thn", "GCS")]
    [InlineData(@"DATA\Scripts\Intro\x.thn", "Intro")]
    [InlineData(@"DATA\MISSIONS\M01A\x.thn", "Missions")]
    [InlineData(@"DATA\RandomMissions\solar_pan.thn", "Missions")]
    [InlineData(@"DATA\MODS\my_mod\CUTSCENES\x.thn", "Mods")]
    [InlineData(@"DATA\Scripts\x.thn", "Other")]
    [InlineData(@"DATA\Scripts\Mods\x.thn", "Other")]
    [InlineData(@"EXE\x.thn", "Other")]
    public void TheFolderGroupComesFromTheInstallPath(string path, string folder) =>
        Assert.Equal(Enum.Parse<CutsceneFolder>(folder), CutsceneCatalog.FolderOf(path));

    [Fact]
    public void TheScanReadsTheCountsOfEachScriptAndTheProblemOfABrokenOne()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"DATA\SCRIPTS\STORY\a.thn"] = Encoding.ASCII.GetBytes(TimelineModelTests.Script),
            [@"DATA\SCRIPTS\STORY\b.thn"] = Encoding.ASCII.GetBytes("duration = ("),
            [@"DATA\SCRIPTS\STORY\c.thn"] = Encoding.ASCII.GetBytes("duration = 1\nentities = 3\nevents = {}"),
        };
        var catalog = new CutsceneCatalog(files.Keys);

        // A budget of 0 still reads one script in each call.
        Assert.True(catalog.Scan(p => files[p], ThnScripts.Evaluator, TimeSpan.Zero));
        Assert.Equal(1, catalog.Scanned);
        Assert.False(catalog.Done);
        while (catalog.Scan(p => files[p], ThnScripts.Evaluator, TimeSpan.Zero))
        {
        }

        Assert.True(catalog.Done);
        Assert.Equal(new CutsceneCounts(30, 9, 10, null), catalog.Entries[0].Counts);
        Assert.Contains("does not compile", catalog.Entries[1].Counts!.Problem);
        Assert.Contains("ThnScript.cs:122-141", catalog.Entries[2].Counts!.Problem);
        Assert.Equal("a", catalog.Entries[0].Name);
        Assert.Equal(CutsceneFolder.Story, catalog.Entries[0].Folder);
        Assert.False(catalog.Scan(p => files[p], ThnScripts.Evaluator, TimeSpan.Zero));
    }

    [Fact]
    public void TheStateNamesTheFileOfTheActiveModOrTheLowerMods()
    {
        Assert.Equal("Retail", CutsceneCatalog.StateText(new ThnFileSource(ThnFileKind.Lower, false)));
        Assert.Equal("Changed by a lower mod", CutsceneCatalog.StateText(new ThnFileSource(ThnFileKind.Lower, true)));
        Assert.Equal("Patched here", CutsceneCatalog.StateText(new ThnFileSource(ThnFileKind.PatchedHere, true)));
        Assert.Equal("Replaced here", CutsceneCatalog.StateText(new ThnFileSource(ThnFileKind.WholeFileHere, false)));
        Assert.Equal("New here", CutsceneCatalog.StateText(new ThnFileSource(ThnFileKind.NewHere, false)));
    }

    /// <summary>
    /// The Timeline opens on the first 600 s of a long script, and on the whole of a short one. A margin of 10 pixels
    /// is before time 0 and after the end, so that a diamond there shows whole.
    /// </summary>
    [Fact]
    public void TheViewFitsAtMost600SecondsBetweenTheMargins()
    {
        var view = new TimelineView();
        view.Fit(60_000, 1200);
        Assert.Equal(1180 / 600.0, view.PixelsPerSecond, 9);
        Assert.Equal(20, view.XOf(0, 10), 3);

        view.Fit(92.75, 1200);
        Assert.Equal(1180 / 92.75, view.PixelsPerSecond, 9);
        Assert.Equal(20, view.XOf(0, 10), 3);
        Assert.Equal(10 + 1200 - TimelineView.Margin, view.XOf(92.75, 10), 3);
        Assert.Equal(92.75, view.TimeAt(10 + 1200 - TimelineView.Margin, 10), 6);
    }

    [Fact]
    public void AZoomKeepsTheTimeUnderTheMouseAndAPanStaysInTheRange()
    {
        var view = new TimelineView();
        view.Fit(100, 1000);
        var time = view.TimeAt(510, 10);

        view.ZoomAt(510, 10, 4, 100, 1000);
        Assert.Equal(4 * 980 / 100.0, view.PixelsPerSecond, 9);
        Assert.Equal(time, view.TimeAt(510, 10), 9);

        // The pan stops with time 0 at the left margin, and with the end at the right margin.
        view.Pan(-100_000, 100, 1000);
        Assert.Equal(10 + TimelineView.Margin, view.XOf(0, 10), 3);
        view.Pan(100_000, 100, 1000);
        Assert.Equal(100, view.TimeAt(10 + 1000 - TimelineView.Margin, 10), 6);

        // The zoom stops at 2 pixels for each millisecond, and at 600 s in the view.
        view.ZoomAt(510, 10, 1e9, 100, 1000);
        Assert.Equal(TimelineView.MaxPixelsPerSecond, view.PixelsPerSecond);
        view.ZoomAt(510, 10, 1e-9, 100_000, 1000);
        Assert.Equal(980 / TimelineView.MaxSpan, view.PixelsPerSecond, 9);
    }

    [Fact]
    public void FrameShowsASpanWithAMargin()
    {
        var view = new TimelineView();
        view.Fit(100, 1000);

        view.Frame(20, 30, 100, 1000);

        // A tenth of the span on each side, 1 s, between the margins of the view.
        Assert.Equal(980 / 12.0, view.PixelsPerSecond, 9);
        Assert.Equal(10 + TimelineView.Margin, view.XOf(19, 10), 3);
    }

    /// <summary>A drag of the playhead back waits for the release, so that it builds the scene once.</summary>
    [Fact]
    public void ADragBackSeeksAtTheReleaseAndADragForwardSeeksAtOnce()
    {
        var fakes = new CutsceneFakes();
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => 10);
        var scrub = new ScrubGesture();
        transport.Seek(5);

        scrub.Drag(transport, 2);
        scrub.Drag(transport, 1);
        Assert.Equal(5, transport.Time);
        Assert.Equal(1, scrub.Shown(transport));

        scrub.Release(transport);
        Assert.Equal(1, transport.Time);
        Assert.Null(scrub.Held);

        scrub.Drag(transport, 3);
        Assert.Equal(3, transport.Time);
        Assert.Null(scrub.Held);
        scrub.Release(transport);
        Assert.Equal(3, transport.Time);
    }

    /// <summary>
    /// A drag with Shift on the ruler sets the loop range from the press to the mouse and turns the loop on. A press
    /// without Shift leaves the range to the scrub. A Shift+click without a drag clears the range.
    /// </summary>
    [Fact]
    public void AShiftDragSetsTheLoopRangeAndAShiftClickClearsIt()
    {
        var fakes = new CutsceneFakes();
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => 10);
        var drag = new LoopRangeGesture();

        drag.Press(6, range: true);
        drag.Drag(transport, 4);
        drag.Drag(transport, 2.5);
        Assert.True(drag.Active);
        drag.Release(transport);
        Assert.False(drag.Active);
        Assert.Equal((2.5, 6.0), transport.LoopRange);
        Assert.True(transport.Loop);

        drag.Press(3, range: false);
        Assert.False(drag.Active);
        drag.Drag(transport, 8);
        drag.Release(transport);
        Assert.Equal((2.5, 6.0), transport.LoopRange);

        drag.Press(5, range: true);
        drag.Release(transport);
        Assert.Null(transport.LoopRange);
        Assert.True(transport.Loop);
    }

    /// <summary>
    /// Used by shows the section at the place of the line, so that the right one of two sections with the same line
    /// opens. After an edit that deleted a section, the place is stale: it is past the end of the file, or another
    /// section is there. Then the first section that has the line opens.
    /// </summary>
    [Fact]
    public void UsedByShowsTheSectionAtThePlaceOfTheLineOrElseTheFirstSectionWithTheLine()
    {
        const string missions = @"DATA\MISSIONS\m.ini";
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\MISSIONS\x.thn", "-- script");
        ws.WriteBase(@"DATA\MISSIONS\y.thn", "-- script");
        ws.WriteBase(missions,
            "[Trigger]\nnickname = t1\nAct_CallThorn = Missions\\x.thn\n" +
            "[Trigger]\nnickname = t2\nAct_CallThorn = Missions\\x.thn\n" +
            "[Trigger]\nnickname = t3\nAct_CallThorn = Missions\\y.thn\n");
        ws.Mod("act", new());
        ws.WriteProfile("act");
        var session = ws.Session("act");
        var uses = ThnReferences.Scan(FileSystem.FromPath(ws.Base)).UsedBy(@"DATA\MISSIONS\x.thn");
        static SectionRef Trigger(string nickname) => new(missions, new SectionIdentity("Trigger", nickname));

        Assert.Equal([0, 1], uses.Select(u => u.SectionIndex));
        Assert.Equal(Trigger("t1"), CutsceneWorkspace.SectionOf(session, uses[0]));
        Assert.Equal(Trigger("t2"), CutsceneWorkspace.SectionOf(session, uses[1]));

        // t2 is at place 0 now, and t3 is at place 1.
        session.Execute(EditCommand.DeleteSection(Trigger("t1")));
        Assert.Equal(Trigger("t2"), CutsceneWorkspace.SectionOf(session, uses[0]));
        Assert.Equal(Trigger("t2"), CutsceneWorkspace.SectionOf(session, uses[1]));

        // Only t2 is left, so place 1 is past the end of the file.
        session.Execute(EditCommand.DeleteSection(Trigger("t3")));
        Assert.Equal(Trigger("t2"), CutsceneWorkspace.SectionOf(session, uses[1]));

        session.Execute(EditCommand.RemoveName(Trigger("t2"), "Act_CallThorn"));
        Assert.Null(CutsceneWorkspace.SectionOf(session, uses[0]));
        Assert.Null(CutsceneWorkspace.SectionOf(session, uses[1]));
    }

    /// <summary>
    /// A problem of the validation selects its element in the Console (section 9 of the M8 design). The check reads the
    /// file of the mod, and the session opens the cutscene from that file. So the place of the problem names the element
    /// of W, also after a delete moved the places, and also after the problem went through the JSON of oltool. The
    /// result comes some seconds after the save. When an edit in that time moves the element, the key of the place finds
    /// it. When the key names no element or more than one element, the cutscene is selected.
    /// </summary>
    [Fact]
    public void AProblemOfTheValidationSelectsTheElementOfItsPlaceInW()
    {
        const string path = ThnSessions.ScriptPath;
        using var ws = ThnSessions.Workspace();
        var writer = ws.Session("act");
        writer.Execute(ThnEdits.Delete(ThnSessions.Entity(writer, "Monitor")));
        writer.Execute(ThnEdits.Set(ThnSessions.Entity(writer, "Voice_other"), new ThnPath("audioprops.ain"), new ThnText("loud")));
        // The second of two events with one key.
        writer.Execute(ThnEdits.Set(ThnSessions.Event(writer, 2), new ThnPath("props.duration"), new ThnText("long")));
        // The save gate refuses these errors (decision D137 of the M8b design), so the test writes the patch of the
        // edits as a modder does outside the editor.
        var patch = writer.CutsceneDocument(path)!.ChangedFile()!.Text!;
        Assert.False(writer.Save().Saved);
        writer.Dispose();
        var patchFile = Path.Combine(ws.ModFolder("act"), ThnSessions.PatchFile);
        Directory.CreateDirectory(Path.GetDirectoryName(patchFile)!);
        File.WriteAllText(patchFile, patch);
        var session = ws.Session("act");
        var overlay = ws.Build(Mod.Load(ws.ModFolder("act")));

        var problems = CutsceneCheck.NewProblems(FileSystem.FromPath(ws.Base), overlay.CreateFileSystem(),
            overlay.CutscenePaths, null, null);
        var places = ValidationJson.Read(ValidationJson.Write(problems)).Select(p => p.Cutscene!).ToList();

        var (voicePlace, psysPlace) = (places[0], places[1]);
        Assert.Equal(2, places.Count);
        Assert.Equal(new ThnLocation(path, ThnElementKind.Entity, 3, "audioprops.ain", "\"Voice_other\""), voicePlace);
        Assert.Equal(new ThnLocation(path, ThnElementKind.Event, 2, "props.duration", "{ 5, START_PSYS, { \"Fx\" } }"), psysPlace);
        var voice = ThnSessions.Entity(session, "Voice_other");
        var root = new CutsceneRoot(path);
        Assert.Equal(voice, CutsceneWorkspace.SelectionAt(path, ThnSessions.W(session), voicePlace));
        Assert.Equal(ThnSessions.Event(session, 2), CutsceneWorkspace.SelectionAt(path, ThnSessions.W(session), psysPlace));
        Assert.Equal(root, CutsceneWorkspace.SelectionAt(path, ThnSessions.W(session), new ThnLocation(path)));
        Assert.Equal(root, CutsceneWorkspace.SelectionAt(path, null, voicePlace));

        // Edits after the save delete the first entity and the first event: entity place 3 is Fx now, and event place 2
        // is the START_MOTION. The key finds the entity. The key of the event names two events, so the cutscene is
        // selected.
        session.Execute(ThnEdits.Delete(ThnSessions.Entity(session, "Cam_A")));
        session.Execute(ThnEdits.Delete(ThnSessions.Event(session, 0)));
        Assert.Equal(voice, CutsceneWorkspace.SelectionAt(path, ThnSessions.W(session), voicePlace));
        Assert.Equal(root, CutsceneWorkspace.SelectionAt(path, ThnSessions.W(session), psysPlace));

        // The entity is gone.
        session.Execute(ThnEdits.Delete(voice));
        Assert.Equal(root, CutsceneWorkspace.SelectionAt(path, ThnSessions.W(session), voicePlace));
    }

    /// <summary>A click on a problem opens its cutscene in normal form, and does not open the open cutscene again, also
    /// when the place spells the path in another way.</summary>
    [Fact]
    public void AClickOnAProblemOpensItsCutsceneOnlyWhenItIsNotOpen()
    {
        var place = new ThnLocation(@"data/scripts/story/x.thn", ThnElementKind.Entity, 1);

        Assert.Null(CutsceneWorkspace.ToOpen(@"DATA\SCRIPTS\STORY\x.thn", place));
        Assert.Equal(@"data\scripts\story\x.thn", CutsceneWorkspace.ToOpen(@"DATA\SCRIPTS\STORY\y.thn", place));
        Assert.Equal(@"data\scripts\story\x.thn", CutsceneWorkspace.ToOpen(null, place));
        Assert.Null(CutsceneWorkspace.ToOpen(@"DATA\x.thn", new ThnLocation(@"DATA\x.thn")));
    }

    /// <summary>The line of an open, which the bench and the proofs read, has the same numbers in a culture with a
    /// decimal comma.</summary>
    [Fact]
    public void TheLineOfAnOpenDoesNotDependOnTheCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = comma;

            Assert.Equal(@"Opened cutscene DATA\x.thn (92.75 s). Context: Script only. Timeline: 1 row.",
                CutsceneWorkspace.OpenedLine(@"DATA\x.thn", 92.75, "Script only", "Timeline: 1 row."));
            Assert.Equal(@"Opened cutscene DATA\x.thn (0.5 s). Context: Script only.",
                CutsceneWorkspace.OpenedLine(@"DATA\x.thn", 0.5, "Script only", null));
            Assert.Equal(@"Opened cutscene DATA\x.thn (0.5 s). Context: Space: Zz01.",
                CutsceneWorkspace.OpenedLine(@"DATA\x.thn", 0.5, "Space: Zz01", null));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>A preview that stops and a frame that does not draw each give one Console line with the path, the script
    /// time and the message (decision D657). The time has three decimals in each culture.</summary>
    [Fact]
    public void TheLinesOfAStoppedPreviewNameThePathTheTimeAndTheMessage()
    {
        var culture = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = comma;
            var problem = new InvalidOperationException("The chain is too deep.");

            Assert.Equal(@"The preview of DATA\x.thn stopped at 0.017 s: The chain is too deep. The view keeps the last " +
                         "frame.", CutsceneWorkspace.StoppedLine(@"DATA\x.thn", 1.0 / 60, problem));
            Assert.Equal(@"The preview of DATA\x.thn stopped at 0.000 s: The chain is too deep. The view keeps the last " +
                         "frame.", CutsceneWorkspace.StoppedLine(@"DATA\x.thn", 0, problem));
            Assert.Equal(@"The preview of DATA\x.thn cannot draw the frame at 12.500 s: The chain is too deep.",
                CutsceneWorkspace.DrawFailedLine(@"DATA\x.thn", 12.5, problem));
            // The message has no parameter text of .NET.
            Assert.Equal(@"The preview of DATA\x.thn cannot draw the frame at 1.000 s: No model.",
                CutsceneWorkspace.DrawFailedLine(@"DATA\x.thn", 1, new ArgumentException("No model.", "model")));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>A line through the near plane draws only its part in front of the camera.</summary>
    [Fact]
    public void ALineBehindTheCameraIsCutAtTheNearPlane()
    {
        var projection = Projection();

        Assert.Null(CutsceneOverlay.Clip(projection, new Vector3(0, 0, 5), new Vector3(1, 0, 5)));
        var whole = CutsceneOverlay.Clip(projection, new Vector3(0, 0, -5), new Vector3(1, 0, -5));
        Assert.NotNull(whole);
        Assert.True(projection.Project(new Vector3(0, 0, -5), out var start, out _));
        Assert.Equal(start, whole.Value.A);

        // From behind the camera to the front: the cut point is on the near plane, on the line.
        var cut = CutsceneOverlay.Clip(projection, new Vector3(0, 0.01f, 5), new Vector3(0, 0.01f, -5));
        Assert.NotNull(cut);
        var near = projection.Near;
        Assert.True(projection.Project(new Vector3(0, 0.01f, -near), out var expected, out _));
        Assert.True(Vector2.Distance(expected, cut.Value.A) < 0.05f, $"Expected {expected}, but got {cut.Value.A}.");
        Assert.True(projection.Project(new Vector3(0, 0.01f, -5), out var end, out _));
        Assert.Equal(end, cut.Value.B);
    }

    /// <summary>The markers of the Free mode take cutscene elements: a selection that is an equal value does not fade,
    /// and the hit test gives the element.</summary>
    [Fact]
    public void TheMarkersOfTheFreeModeFindAnElementByItsValue()
    {
        var markers = new SceneMarkers();
        var kind = ObjectKinds.Thn.Marker with { FadeDistance = 10 };
        var far = new Vector3(0, 0, -9.5f);

        markers.Update(Projection(), new ThnRef(Element.Path, Element.Kind, Element.Id),
            [((object)Element, kind, kind.Abgr, far), (Root, kind, kind.Abgr, new Vector3(0, 0, -50))]);

        var marker = Assert.Single(markers.Items);
        Assert.Equal(1, marker.Alpha);
        Assert.Equal(Element, markers.HitTest(marker.Screen));
    }

    [Fact]
    public void ARoomCameraHasANearPlaneOf5CentimetersAndSetPoseKeepsTheRoll()
    {
        var camera = new SceneCamera(CameraScale.Room);
        Assert.Equal(5, camera.Speed);
        Assert.Equal(0.05f, camera.ZRange.X);

        var turn = Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 0.25f);
        camera.SetPose(new Vector3(1, 2, 3), turn);

        Assert.Equal(new Vector3(1, 2, 3), camera.Position);
        AssertSameTurn(turn, camera.Orientation);
    }

    /// <summary>
    /// The fly and the orbit turn the camera in each frame while a button is down. A frame without a mouse move keeps
    /// the roll of SetPose, so a press of the right button after Align view to camera does not move the view. A mouse
    /// move levels the view.
    /// </summary>
    [Fact]
    public void AFrameWithoutAMouseMoveKeepsTheRollAndAMouseTurnLevelsTheView()
    {
        var camera = new SceneCamera(CameraScale.Room);
        var turn = Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 0.25f);
        camera.SetPose(new Vector3(1, 2, 3), turn);

        camera.Turn(Vector2.Zero);
        AssertSameTurn(turn, camera.Orientation);

        camera.Turn(new Vector2(4, 0));
        Assert.Equal(0, Vector3.Transform(Vector3.UnitX, camera.Orientation).Y, 1e-5f);
        Assert.Equal(new Vector3(1, 2, 3), camera.Position);
    }

    [Theory]
    [InlineData(1.2f, -1)]
    [InlineData(-0.4f, 1)]
    public void SetPoseLooksStraightDownOrUpWithTheTurnOfTheUp(float yaw, int sign)
    {
        var camera = new SceneCamera(CameraScale.Room);
        var turn = Quaternion.CreateFromYawPitchRoll(yaw, sign * MathF.PI / 2, 0);

        camera.SetPose(Vector3.Zero, turn);

        AssertSameTurn(turn, camera.Orientation);
    }

    /// <summary>The lens of the Free mode with the default field of view and the ratio of the view is the projection of
    /// the Scene camera.</summary>
    [Fact]
    public void TheDefaultLensIsTheProjectionOfTheSceneCamera()
    {
        var lens = new LensCamera();
        var scene = new LookAtCamera { GameFOV = true, ZRange = new Vector2(0.05f, 1000) };

        lens.Update(1600, 900, Vector3.Zero, Quaternion.Identity, LensCamera.DefaultFovH, 1600 / 900f,
            new Vector2(0.05f, 1000));
        scene.Update(1600, 900, Vector3.Zero, -Vector3.UnitZ);

        Assert.Equal(scene.Projection, lens.Projection);
        Assert.Equal(scene.View, lens.View);
    }

    /// <summary>A space script in Shot mode uses the planes of the space camera of the game: 3 and 250,000.</summary>
    [Fact]
    public void TheSpaceShotLensHasThePlanesOfTheSpaceCamera()
    {
        var lens = new LensCamera();

        lens.Update(1600, 900, Vector3.Zero, Quaternion.Identity, LensCamera.DefaultFovH, 16 / 9f,
            LensCamera.SpaceShotRange);

        // A perspective projection keeps near * far / (near - far) in M43 and far / (near - far) in M33.
        var projection = lens.Projection;
        Assert.InRange(projection.M43 / projection.M33, 2.99f, 3.01f);
        Assert.InRange(projection.M43 / (projection.M33 + 1), 245_000f, 255_000f);
    }

    /// <summary>A wider field of view or a wider screen widens the lens, as the THN camera widens the
    /// shot.</summary>
    [Fact]
    public void TheLensFollowsTheFieldOfViewAndTheScreenRatio()
    {
        var narrow = new LensCamera();
        var wide = new LensCamera();
        var screen = new LensCamera();
        narrow.Update(1600, 900, Vector3.Zero, Quaternion.Identity, 10, 4 / 3f, new Vector2(1, 100));
        wide.Update(1600, 900, Vector3.Zero, Quaternion.Identity, 20, 4 / 3f, new Vector2(1, 100));
        screen.Update(1600, 900, Vector3.Zero, Quaternion.Identity, 10, 16 / 9f, new Vector2(1, 100));

        // M11 is 1 / tan(half the horizontal angle). On 4:3, the half angle is fovh.
        Assert.Equal(1 / MathF.Tan(10 * MathF.PI / 180), narrow.Projection.M11, 3);
        Assert.True(wide.Projection.M11 < narrow.Projection.M11);
        Assert.Equal(narrow.Projection.M11 * 0.75f, screen.Projection.M11, 3);
    }

    /// <summary>
    /// After a data reload, the workspace makes the choices of the context again (section 5.4 of the M8b design). The
    /// chosen context stays when it is still a choice, also when only its ship package differs. A room that the new
    /// choices do not have gives the first choice.
    /// </summary>
    [Fact]
    public void AfterADataReloadTheChosenContextStaysWhenItIsStillAChoice()
    {
        var room = new Runtime.Thn.PreviewContext(Runtime.Thn.PreviewContextKind.Room, "Li01_01_base", "Bar", null, "pkg");
        var choices = new List<Runtime.Thn.PreviewContext> { room, Runtime.Thn.PreviewContext.ScriptOnly };

        Assert.Same(Runtime.Thn.PreviewContext.ScriptOnly,
            CutsceneWorkspace.ContextAfterReload(choices, Runtime.Thn.PreviewContext.ScriptOnly));
        Assert.Same(room, CutsceneWorkspace.ContextAfterReload(choices, room with { Room = "BAR", ShipPackage = null }));
        Assert.Same(room, CutsceneWorkspace.ContextAfterReload(choices, room with { Room = "Cityscape" }));
        Assert.Same(Runtime.Thn.PreviewContext.ScriptOnly,
            CutsceneWorkspace.ContextAfterReload([Runtime.Thn.PreviewContext.ScriptOnly], room));
    }

    private static void AssertSameTurn(Quaternion expected, Quaternion actual)
    {
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ })
        {
            var distance = Vector3.Distance(Vector3.Transform(axis, expected), Vector3.Transform(axis, actual));
            Assert.True(distance < 1e-4f, $"The axis {axis} differs by {distance}.");
        }
    }

    /// <summary>A camera at the origin that looks along -Z.</summary>
    private static SceneProjection Projection()
    {
        var camera = new LookAtCamera { ZRange = new Vector2(0.05f, 1000) };
        camera.Update(800, 600, Vector3.Zero, -Vector3.UnitZ);
        return new SceneProjection(camera, Vector2.Zero, new Vector2(800, 600), 1);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Runtime.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The Space contexts of a script and their plans (test 5.2 item 4 of the cutscene fixes plan): one context for each
/// system, the order, the labels, the plan fields and the notes. The names are synthetic.
/// </summary>
public sealed class PreviewContextSpaceTests : IDisposable
{
    private const string Script = @"DATA\MISSIONS\MX01\a.thn";

    private static readonly SpaceFacts Facts = new(["Zz01", "Zz02"],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, SpaceObjectFact>(StringComparer.OrdinalIgnoreCase));

    private readonly TestWorkspace ws = new();

    public void Dispose() => ws.Dispose();

    private (IReadOnlyList<PreviewContext> Choices, ThnReferences References, GameItemDb Items) Choices(string ini)
    {
        ws.WriteBase(@"DATA\MISSIONS\MX01\mx01.ini", ini);
        // A freelancer.ini without the resource DLLs, so the game data object loads no string resources.
        ws.WriteBase(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n");
        ws.WriteBase(@"DATA\SCRIPTS\GenericScripts.ini",
            "[GenericScripts]\nset_segment = fidget\nscript = missions\\mx01\\a.thn\n");
        var view = FileSystem.FromPath(ws.Base);
        var references = ThnReferences.Scan(view);
        var items = new GameItemDb(view);
        var spaces = new SpaceScenes(view, references, Facts);
        return (PreviewContext.Choices(Script, references, items, spaces), references, items);
    }

    /// <summary>The Space contexts come after the fidget and before Script only, one for each system, in the order of
    /// the lines.</summary>
    [Fact]
    public void TheSpaceContextsComeAfterTheRoomsAndBeforeScriptOnly()
    {
        var (choices, _, _) = Choices(
            "[Trigger]\nnickname = t1\nsystem = Zz02\nAct_CallThorn = missions\\mx01\\a.thn\n" +
            "[Trigger]\nnickname = t2\nsystem = zz01\nAct_CallThorn = missions\\mx01\\a.thn, zz_ship\n" +
            "[Trigger]\nnickname = t3\nsystem = Zz02\nAct_CallThorn = missions\\mx01\\a.thn\n");

        Assert.Equal([PreviewContextKind.Fidget, PreviewContextKind.Space, PreviewContextKind.Space,
            PreviewContextKind.ScriptOnly], choices.Select(c => c.Kind));
        Assert.Equal(["Zz02", "Zz01"], choices.Where(c => c.Kind == PreviewContextKind.Space).Select(c => c.System));
        Assert.Equal("t1", choices[1].Use!.Trigger);
        Assert.Equal("Space: Zz02", choices[1].Label);
        Assert.Equal("Script only", choices[^1].Label);
    }

    /// <summary>Same compares the system without case, so a reload of the data keeps the chosen system.</summary>
    [Fact]
    public void SameComparesTheSystemAndAReloadKeepsIt()
    {
        var zz01 = new PreviewContext(PreviewContextKind.Space, null, null, null, null) { System = "Zz01" };
        var zz02 = zz01 with { System = "Zz02" };

        Assert.True(PreviewContext.Same(zz01, zz01 with { System = "ZZ01" }));
        Assert.False(PreviewContext.Same(zz01, zz02));
        Assert.False(PreviewContext.Same(zz01, zz01 with { System = null }));
        Assert.Same(zz02, CutsceneWorkspace.ContextAfterReload([zz01, zz02, PreviewContext.ScriptOnly],
            zz02 with { ShipPackage = "pkg" }));
    }

    /// <summary>The plan of a Space context has the system, the main object, its start and the note. It plays the
    /// script as a cutscene, so the letterbox shows.</summary>
    [Fact]
    public void TheSpacePlanHasTheSystemTheMainObjectAndTheNote()
    {
        var (choices, references, items) = Choices(
            "[MsnShip]\nnickname = zz_ship\nposition = 1, 2, 3\n" +
            "[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = missions\\mx01\\a.thn, zz_ship, 4, 5\n");
        var space = choices.Single(c => c.Kind == PreviewContextKind.Space);

        var plan = space.Plan(Script, items, references);

        Assert.Equal("Zz01", plan.System);
        Assert.Equal("zz_ship", plan.MainObject);
        Assert.Equal(new SpacePose(new Vector3(1, 2, 3), Quaternion.Identity), plan.MainObjectStart);
        Assert.Null(plan.ScriptPosition);
        Assert.Equal(Script, plan.Cutscene, StringComparer.OrdinalIgnoreCase);
        Assert.Equal([Script], plan.Scripts, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("The game plays this script in space with the mission objects. It makes none of the script " +
                     "objects. The preview shows the system Zz01, and the script objects as stand-ins.", plan.Note);
    }

    /// <summary>A script without a <c>main_object</c> marker gets no main object: nothing binds to it.</summary>
    [Fact]
    public void AScriptWithoutAMainObjectMarkerGetsNoMainObject()
    {
        var (choices, references, items) = Choices(
            "[MsnShip]\nnickname = zz_ship\nposition = 1, 2, 3\n" +
            "[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = missions\\mx01\\a.thn, zz_ship\n");
        var space = choices.Single(c => c.Kind == PreviewContextKind.Space);
        var plain = ThnScriptModel.From(Evaluate(
            "duration = 1\nentities = { { entity_name = \"Mk\", type = MARKER } }\nevents = {}"), ThnElementId.Counter());
        var marked = ThnScriptModel.From(Evaluate(
            "duration = 1\nentities = { { entity_name = \"Mk\", type = MARKER, userprops = { Main_Object = 1 } } }\n" +
            "events = {}"), ThnElementId.Counter());

        var without = space.Plan(Script, items, references, plain);
        var with = space.Plan(Script, items, references, marked);

        Assert.Equal("Zz01", without.System);
        Assert.Null(without.MainObject);
        Assert.Null(without.MainObjectStart);
        Assert.Equal("zz_ship", with.MainObject);
        Assert.NotNull(with.MainObjectStart);
    }

    /// <summary>The position of the form <c>script, x, y, z</c> goes into the plan, for its banner.</summary>
    [Fact]
    public void TheScriptPositionGoesIntoThePlan()
    {
        var (choices, references, items) = Choices(
            "[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = missions\\mx01\\a.thn, 4, 5, 6\n");

        var plan = choices.Single(c => c.Kind == PreviewContextKind.Space).Plan(Script, items, references);

        Assert.Equal(new Vector3(4, 5, 6), plan.ScriptPosition);
        Assert.Equal(SpaceUse.Player, plan.MainObject);
    }

    /// <summary>A script whose lines give no system gets one Space context without a system. Its plan plays the script
    /// alone, and the note tells how to fix the mission line.</summary>
    [Fact]
    public void AScriptWithoutASystemGetsOneSpaceContextWithoutASystem()
    {
        var (choices, references, items) = Choices(
            "[Trigger]\nnickname = t1\nAct_CallThorn = missions\\mx01\\a.thn\n" +
            "[Trigger]\nnickname = t2\nAct_CallThorn = missions\\mx01\\a.thn\n");

        var space = Assert.Single(choices, c => c.Kind == PreviewContextKind.Space);
        var plan = space.Plan(Script, items, references);

        Assert.Null(space.System);
        Assert.Equal("Space", space.Label);
        Assert.Null(plan.System);
        Assert.Equal("No rule finds the system of the mission line of the trigger t1. The preview shows the script " +
                     "without a system. Give the trigger a system key, or a condition that names a system or a base.",
            plan.Note);
    }

    /// <summary>The Space context of a system takes the first line of that system that gives a start. The order of the
    /// systems stays the order of their first lines.</summary>
    [Fact]
    public void ASystemTakesItsFirstLineThatGivesAStart()
    {
        var (choices, references, items) = Choices(
            "[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = missions\\mx01\\a.thn\n" +
            "[Trigger]\nnickname = t2\nsystem = Zz02\nAct_CallThorn = missions\\mx01\\a.thn\n" +
            "[Trigger]\nnickname = t3\nsystem = Zz01\nAct_SetInitialPlayerPos = 1, 2, 3\n" +
            "Act_CallThorn = missions\\mx01\\a.thn\n");

        var spaces = choices.Where(c => c.Kind == PreviewContextKind.Space).ToList();

        Assert.Equal(["Zz01", "Zz02"], spaces.Select(c => c.System));
        Assert.Equal(["t3", "t2"], spaces.Select(c => c.Use!.Trigger));
        Assert.Equal(new Vector3(1, 2, 3), spaces[0].Plan(Script, items, references).MainObjectStart!.Position);
    }

    /// <summary>The note of a line outside a trigger names the file and the problem. It has no advice for a
    /// trigger.</summary>
    [Fact]
    public void TheNoteOfALineOutsideATriggerNamesItsFile()
    {
        var (choices, references, items) = Choices("[Mission]\nAct_CallThorn = missions\\mx01\\a.thn\n");

        var space = Assert.Single(choices, c => c.Kind == PreviewContextKind.Space);
        var plan = space.Plan(Script, items, references);

        Assert.Equal("", space.Use!.Trigger);
        Assert.Contains("[Trigger]", space.Use.Problem);
        Assert.Equal($"No rule finds the system of the mission line in {space.Use.Line.File}. {space.Use.Problem} The " +
                     "preview shows the script without a system.", plan.Note);
    }

    /// <summary>The note of a system key that names no system of the data tells the reason.</summary>
    [Fact]
    public void TheNoteOfAnUnknownSystemKeyTellsTheReason()
    {
        var (choices, references, items) = Choices(
            "[Trigger]\nnickname = t1\nsystem = Qq99\nAct_CallThorn = missions\\mx01\\a.thn\n");

        var plan = Assert.Single(choices, c => c.Kind == PreviewContextKind.Space).Plan(Script, items, references);

        Assert.Equal("No rule finds the system of the mission line of the trigger t1. The system key of the trigger " +
                     "names Qq99, but the game data has no such system. So the game never runs the trigger. The " +
                     "preview shows the script without a system. Give the trigger a system key, or a condition that " +
                     "names a system or a base.", plan.Note);
    }
}

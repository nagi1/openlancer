using System;
using System.Collections.Generic;
using LibreLancer.Data.Schema;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// Decision D34: a save writes nothing while a light of an edited star system file names an attenuation curve that the
/// engine cannot load. The engine throws when it loads such a light, so a saved mod with it stops each later load of
/// the profile.
/// </summary>
public sealed class CurveCheckTests
{
    private const string Li01 = """
        [Object]
        nickname = Li01_depot
        pos = -38675, 0, -27705
        archetype = depot

        [LightSource]
        nickname = Light_curve
        pos = 1, 2, 3
        atten_curve = known_curve

        [LightSource]
        nickname = Light_point
        pos = 0, 0, 0
        """;

    private const string OtherPath = @"DATA\UNIVERSE\SYSTEMS\LI01\lights.ini";
    private const string St01Path = @"DATA\UNIVERSE\SYSTEMS\ST01\st01.ini";

    private static readonly SectionRef Point = SystemWorkspace.Li01Section("LightSource", "Light_point");

    /// <summary>The problems of the checks before a save, and whether the save can go on.</summary>
    private static (bool CanSave, List<Diagnostic> Problems) Check(EditSession session, GraphIni? graphs)
    {
        var problems = new List<Diagnostic>();
        return (EditController.CanSave(session, graphs, problems.Add), problems);
    }

    private static void AssertNoProblem(EditSession session, GraphIni? graphs)
    {
        var (canSave, problems) = Check(session, graphs);
        Assert.True(canSave);
        Assert.Empty(problems);
    }

    /// <summary>The engine finds a graph only when exactly one graph has the name, without letter case, and it needs
    /// the points of the graph.</summary>
    private static GraphIni Graphs() => new()
    {
        FloatGraphs =
        {
            new FloatGraph { Name = "known_curve", Points = { new(0, 1), new(1, 0.5f), new(2, 0) } },
            new FloatGraph { Name = "no_points" },
            new FloatGraph { Name = "twice", Points = { new(0, 1), new(1, 0) } },
            new FloatGraph { Name = "TWICE", Points = { new(0, 1), new(1, 0) } },
        },
    };

    /// <summary>The workspace with a file of St01 whose light names an unknown curve, and a file that is not a system
    /// file with such a light.</summary>
    private static TestWorkspace Create()
    {
        var ws = SystemWorkspace.Create(Li01);
        const string bad = "[LightSource]\nnickname = Light_bad\npos = 0, 0, 0\natten_curve = no_such_curve\n";
        ws.WriteBase(St01Path, bad);
        ws.WriteBase(OtherPath, bad);
        return ws;
    }

    [Fact]
    public void AnUnknownCurveOfAnEditedSystemFileRefusesTheSaveAndNamesTheLightTheCurveAndTheFile()
    {
        using var ws = Create();
        var session = ws.Session("act");
        AssertNoProblem(session, Graphs());

        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["no_such_curve"]));
        var (canSave, problems) = Check(session, Graphs());

        Assert.False(canSave);
        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal(DiagnosticSeverity.Error, p.Severity));
        var curve = problems[0];
        Assert.Equal(SystemWorkspace.Li01Path, curve.Source, ignoreCase: true);
        Assert.Equal((Point, "atten_curve"), (curve.Target, curve.Key));
        Assert.StartsWith("[LightSource] nickname = Light_point: atten_curve = no_such_curve. No graph has the name " +
                          "'no_such_curve'", curve.Message);
        Assert.StartsWith("The save wrote nothing: 1 light names an attenuation curve", problems[1].Message);

        // An undo leaves the file edited, and W of the light has no curve again.
        session.Undo();
        Assert.Contains(SystemWorkspace.Li01Path, session.EditedPaths, StringComparer.OrdinalIgnoreCase);
        AssertNoProblem(session, Graphs());
    }

    [Theory]
    [InlineData("no_points", "The graph 'no_points' has no points.")]
    [InlineData("twice", "No graph has the name 'twice', or more than one graph has it.")]
    public void ACurveThatTheEngineCannotLoadIsAnError(string name, string problem)
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", [name]));

        Assert.Contains(problem, Assert.Single(SceneChecks.Curves(session, Graphs())).Message);
    }

    [Fact]
    public void TheCheckReadsTheCurveAsTheEngineDoesWithoutLetterCase()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["KNOWN_CURVE"]));
        AssertNoProblem(session, Graphs());
        // The engine reads the first value of the last line that has a value.
        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["no_such_curve", "known_curve", ""]));
        AssertNoProblem(session, Graphs());
        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["known_curve", "no_such_curve"]));
        Assert.Single(SceneChecks.Curves(session, Graphs()));
        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["no_such_curve", ""]));
        Assert.Single(SceneChecks.Curves(session, Graphs()));
    }

    [Fact]
    public void OnlyTheEditedSystemFilesCount()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var other = new SectionRef(OtherPath, new SectionIdentity("LightSource", "Light_bad"));
        var st01 = new SectionRef(St01Path, new SectionIdentity("LightSource", "Light_bad"));

        // The engine reads no light of a file that is not a system file.
        session.Execute(EditCommand.SetLinesFromText(other, "pos", ["1, 1, 1"]));
        Assert.Contains(OtherPath, session.EditedPaths, StringComparer.OrdinalIgnoreCase);
        AssertNoProblem(session, Graphs());

        // A system file counts when the session edits it, also for a light that the edit did not change.
        session.Execute(EditCommand.SetLinesFromText(st01, "pos", ["1, 1, 1"]));
        var problem = Assert.Single(SceneChecks.Curves(session, Graphs()));
        Assert.Equal(St01Path, problem.Source, ignoreCase: true);
        Assert.Equal(st01, problem.Target);
    }

    /// <summary>Without game data the check cannot run. The save goes on, with a warning only when the session edited a
    /// system file, so a save of other files gets no warning.</summary>
    [Fact]
    public void WithoutGameDataTheSaveGoesOnWithAWarning()
    {
        using var ws = Create();
        var session = ws.Session("act");
        AssertNoProblem(session, null);
        session.Execute(EditCommand.SetLinesFromText(new SectionRef(OtherPath, new SectionIdentity("LightSource",
            "Light_bad")), "pos", ["1, 1, 1"]));
        AssertNoProblem(session, null);

        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["no_such_curve"]));

        var (canSave, problems) = Check(session, null);
        Assert.True(canSave);
        var warning = Assert.Single(problems);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("cannot check the attenuation curves", warning.Message);
    }
}

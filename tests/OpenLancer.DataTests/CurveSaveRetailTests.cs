using System;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of decision D34 as the curve rule of the data lists (decision D111): the save gate finds a light that
/// names an unknown attenuation curve, and finds none in the retail data. A mod with such a light makes the engine load
/// fail, so the rule is needed, and the validation of a save is the second line.
/// </summary>
public sealed class CurveSaveRetailTests(ITestOutputHelper output)
{
    private const string ManhattanLight = "Light_Li01_Manhattan";

    static CurveSaveRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void AnUnknownCurveIsAnErrorOfTheGateAndOfTheValidation()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();

        // The curve list holds the 6 FLOAT graphs of igraph.ini. The 5 COLOR graphs are not in it.
        var curves = session.Lists.List("curve")!;
        Assert.Equal(6, curves.Entries.Count);
        Assert.All(curves.Entries, e => Assert.True(e.Usable));

        var systems = session.Lists.List("system")!.Entries.Select(e => e.Value).ToList();
        var files = systems.Select(session.SystemPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lights = files.SelectMany(session.Sections).Where(s => s.Id.Type.Equals("LightSource",
            StringComparison.OrdinalIgnoreCase)).ToList();
        var withCurve = lights.Count(s => session.Get(s)!.Lines("atten_curve").Count > 0);
        output.WriteLine($"{systems.Count} systems, {files.Count} system files, {lights.Count} lights, " +
                         $"{withCurve} with atten_curve");
        Assert.Equal(53, files.Count);
        Assert.Equal(136, lights.Count);
        Assert.Equal(88, withCurve);
        Assert.All(lights, l => Assert.Empty(session.CheckLists(l)));
        Assert.Empty(session.SaveProblems());

        var li01 = session.SystemPath("Li01")!;
        var light = new SectionRef(li01, new SectionIdentity("LightSource", ManhattanLight));
        session.Execute(EditCommand.SetLinesFromText(light, "atten_curve", ["no_such_curve"]));
        var problem = Assert.Single(session.SaveProblems());
        output.WriteLine(problem.ToString());
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal(li01, problem.Source);
        Assert.Equal(light, problem.Target);
        Assert.Equal("atten_curve", problem.Key);
        Assert.Contains(ManhattanLight, problem.Message);
        Assert.Contains("'no_such_curve'", problem.Message);
        Assert.Contains("The game stops loading with this value.", problem.Message);

        // The gate stops the save, and the save writes nothing.
        var result = session.Save();
        Assert.False(result.Saved);
        Assert.Contains(problem, result.Problems);
        var patch = Path.Combine(workspace.ModFolder, "patches", "DATA", "Universe", "Systems", "Li01", "Li01.ini");
        Assert.False(File.Exists(patch));

        // The same patch written by hand: the validation, as oltool runs it, finds that the engine cannot load the data.
        session.Undo();
        Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
        File.WriteAllText(patch, $"[LightSource]\r\nnickname = {ManhattanLight}\r\natten_curve = no_such_curve\r\n");
        var profile = ModProfile.Load(workspace.ProfilePath);
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        var problems = ProfileValidator.NewProblems(FileSystem.FromPath(baseFolder), overlay.CreateFileSystem(),
            overlay.CutscenePaths);
        foreach (var line in problems)
            output.WriteLine(line.ToString());
        Assert.Contains(problems,
            p => p.Severity == DiagnosticSeverity.Error && p.Source == EngineDataLoader.LoadSource);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>
    /// The case of the M21 verify report (decision D82, as decided on 2026-10-18): the active mod removes the points of
    /// the retail curve of the first light of Li01 with a curve. The active mod changes no light, but each retail light
    /// that names the curve is an error of the gate, as for a removed curve. One undo heals them. The same edit as a
    /// saved patch makes the engine load fail, so the rule is needed.
    /// </summary>
    [Fact]
    public void ACurveWithoutPointsMakesEachRetailLightThatNamesItAnError()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var light = session.Sections(session.SystemPath("Li01")!).First(s =>
            s.Id.Type.Equals("LightSource", StringComparison.OrdinalIgnoreCase) &&
            session.Get(s)!.Lines("atten_curve").Count > 0);
        var name = session.Get(light)!.Lines("atten_curve")[0].Values[0].ToString()!;
        var curve = session.Lists.List("curve")!.Find(name)!;
        var lights = session.Lists.ReferencesTo("curve", name).Select(r => r.Section).ToHashSet();
        output.WriteLine($"{light.Id} names the curve {name} of {curve.Section!.Path}. {lights.Count} lights name it.");
        Assert.Contains(light, lights);

        session.Execute(EditCommand.RemoveName(curve.Section!, "point"));

        var problems = session.SaveProblems();
        foreach (var problem in problems)
            output.WriteLine(problem.ToString());
        Assert.Equal(lights, problems.Select(p => p.Target!).ToHashSet());
        Assert.All(problems, p =>
        {
            Assert.Equal((DiagnosticSeverity.Error, "atten_curve"), (p.Severity, p.Key));
            Assert.EndsWith($"Curve '{name}' is not usable: the [IGraph] '{name}' has no 'point' line. The game stops " +
                            $"loading with this value. Fix '{name}', or pick another value.", p.Message);
        });
        var result = session.Save();
        Assert.False(result.Saved);
        Assert.False(Directory.Exists(Path.Combine(workspace.ModFolder, "patches")));

        session.Undo();
        Assert.Empty(session.SaveProblems());

        // The same edit as a patch written by hand: the validation, as oltool runs it, finds that the engine cannot
        // load the data.
        var patch = Path.Combine(workspace.ModFolder, "patches", "DATA", "igraph.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
        File.WriteAllText(patch, $"[IGraph]\r\nnickname = {name}\r\nol.remove = point\r\n");
        var profile = ModProfile.Load(workspace.ProfilePath);
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        var validation = ProfileValidator.NewProblems(FileSystem.FromPath(baseFolder), overlay.CreateFileSystem(),
            overlay.CutscenePaths);
        foreach (var line in validation)
            output.WriteLine(line.ToString());
        Assert.Contains(validation,
            p => p.Severity == DiagnosticSeverity.Error && p.Source == EngineDataLoader.LoadSource);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }
}

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
        Assert.Contains("The engine stops with an error when it loads the game data.", problem.Message);

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
}

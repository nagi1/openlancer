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
using OpenLancer.Runtime.Scene;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of decision D34: the check before a save finds a light that names an unknown attenuation curve, and
/// finds none in the retail data. A save without the check writes the mod, and the validation of the save then reports
/// that the game data does not load. So the check is needed, and the validation is the second line.
/// </summary>
public sealed class CurveSaveRetailTests(ITestOutputHelper output)
{
    private const string ManhattanLight = "Light_Li01_Manhattan";

    static CurveSaveRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void AnUnknownCurveIsAnErrorOfTheCheckAndOfTheValidationOfTheSave()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var graphs = RetailGame.Instance.Data.Items.Ini.Graphs;

        // An edit and its undo make each retail system file an edited file, and W stays the retail data.
        var systems = RetailGame.Instance.Data.Items.Systems.Select(s => s.Nickname).ToList();
        foreach (var system in systems)
        {
            var path = session.SystemPath(system)!;
            var target = session.Sections(path).First(s => s.Id.Key != null && session.Get(s)!.ReadOnlyReason == null);
            session.Execute(EditCommand.SetLinesFromText(target, "comment", ["touch"]));
            session.Undo();
        }

        var edited = session.EditedPaths.Where(session.IsSystemFile).ToList();
        var lights = edited.SelectMany(session.Sections).Where(s => s.Id.Type.Equals("LightSource",
            StringComparison.OrdinalIgnoreCase)).ToList();
        var withCurve = lights.Count(s => session.Get(s)!.Lines("atten_curve").Count > 0);
        output.WriteLine($"{systems.Count} systems, {edited.Count} edited system files, {lights.Count} lights, " +
                         $"{withCurve} with atten_curve");
        Assert.Equal(53, edited.Count);
        Assert.Equal(136, lights.Count);
        Assert.Equal(88, withCurve);
        Assert.Empty(SceneChecks.Curves(session, graphs));

        var li01 = session.SystemPath("Li01")!;
        var light = new SectionRef(li01, new SectionIdentity("LightSource", ManhattanLight));
        session.Execute(EditCommand.SetLinesFromText(light, "atten_curve", ["no_such_curve"]));
        var problem = Assert.Single(SceneChecks.Curves(session, graphs));
        output.WriteLine(problem.ToString());
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal(li01, problem.Source);
        Assert.Equal(light, problem.Target);
        Assert.Contains(ManhattanLight, problem.Message);
        Assert.Contains("'no_such_curve'", problem.Message);

        // A save without the check writes the light into the patch of Li01.
        var result = session.Save();
        Assert.True(result.Saved);
        var patch = Assert.Single(result.Written);
        Assert.EndsWith(Path.Combine("patches", "DATA", "Universe", "Systems", "Li01", "Li01.ini"), patch);
        Assert.Equal($"[LightSource]\r\nnickname = {ManhattanLight}\r\natten_curve = no_such_curve\r\n",
            File.ReadAllText(patch));

        // The validation of the save, as oltool runs it, finds that the engine cannot load the data.
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

using System.Diagnostics;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The cutscene errors in the save gate on the retail data (decision D137, retail test 7 of the M8b design): an edit of
/// s002xe that crashes the game stops the save, and an undo lets it pass. The checks without game data find no error in
/// the 1,506 retail cutscenes, so a rename cannot turn a retail error into a false gate error.
/// </summary>
public sealed class ThnSaveGateRetailTests(ITestOutputHelper output)
{
    private const string S002xe = ThnPatchRetailTests.S002xe;

    static ThnSaveGateRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void AnEditThatCrashesTheGameStopsTheSaveOfS002xe()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8bproof");
        var session = workspace.Open();
        var symbols = ThornEvaluator.Instance.Symbols;
        var w = session.Cutscene(S002xe)!;
        var light = w.Entities.First(e => ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, symbols) == "LIGHT" &&
                                          !ThnFlags.IsReference(e.Table, symbols));
        var name = ThnScriptModel.NameOf(light.Table);

        session.Execute(ThnEdits.Remove(new ThnRef(S002xe, ThnElementKind.Entity, light.Id), new ThnPath("spatialprops.pos")));
        var result = session.Save();

        output.WriteLine(string.Join("\n", result.Problems));
        Assert.False(result.Saved);
        var error = Assert.Single(result.Problems);
        Assert.Equal($"The game crashes when it plays this cutscene. entity \"{name}\", spatialprops.pos: The value is " +
                     "not a list of three numbers. An entity with lightprops needs it.", error.Message);
        Assert.Equal((ThnElementKind.Entity, "spatialprops.pos"), (error.Cutscene!.Kind, error.Cutscene.Value));
        Assert.Empty(Directory.EnumerateFiles(workspace.ModFolder, "*.thn", SearchOption.AllDirectories));

        session.Undo();
        var passed = session.Save();
        Assert.True(passed.Saved, string.Join("\n", passed.Problems));
        Assert.Empty(passed.Problems);
    }

    /// <summary>The gate runs the checks without game data. They find no error in the retail cutscenes.</summary>
    [Fact]
    public void TheChecksWithoutGameDataFindNoErrorInTheRetailCutscenes()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var evaluator = ThornEvaluator.Instance;
        var clock = Stopwatch.StartNew();
        var (files, errors) = (0, new System.Collections.Generic.List<Diagnostic>());
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            files++;
            errors.AddRange(ThnChecks.Errors(file, evaluator.Evaluate(view.ReadAllBytes(file), file), evaluator));
        }

        output.WriteLine($"{files} files: {errors.Count} errors without game data in {clock.Elapsed.TotalSeconds:0.0} s.");
        foreach (var error in errors.Take(20))
            output.WriteLine(error.ToString());
        Assert.Equal(1506, files);
        Assert.Empty(errors);
    }
}

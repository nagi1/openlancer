using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The lens of the camera of a new cutscene (decision D134, retail test 3 of the M8b design): the camera values of
/// <see cref="ThnTemplates.NewScript"/> are the medians of the retail CAMERA entities in <c>SCRIPTS\BASES</c>.
/// </summary>
public sealed class ThnTemplatesRetailTests(ITestOutputHelper output)
{
    private static readonly string[] Keys = ["fovh", "hvaspect", "nearplane", "farplane"];

    [Fact]
    public void TheCameraOfANewCutsceneHasTheMedianLensOfTheRetailBaseCameras()
    {
        var fs = FileSystem.FromPath(GameDataPaths.RequireBase());
        var evaluator = ThornEvaluator.Instance;
        var values = Keys.ToDictionary(k => k, _ => new List<float>());
        var (files, cameras) = (0, 0);
        foreach (var file in VfsFiles.Find(fs, ".thn")
                     .Where(f => f.StartsWith(@"DATA\SCRIPTS\BASES\", StringComparison.OrdinalIgnoreCase)))
        {
            files++;
            var model = ThnScriptModel.From(evaluator.Evaluate(fs.ReadAllBytes(file), file), ThnElementId.Counter());
            foreach (var entity in model.Entities.Where(e =>
                         ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, evaluator.Symbols) == "CAMERA"))
            {
                cameras++;
                if (entity.Table["cameraprops"] is not ThnTable props)
                    continue;
                foreach (var key in Keys)
                {
                    if (props[key] is ThnNumber number)
                        values[key].Add(number.Value);
                }
            }
        }

        var medians = Keys.ToDictionary(k => k, k => Median(values[k]));
        output.WriteLine($"{files} files in SCRIPTS\\BASES, {cameras} CAMERA entities.");
        foreach (var key in Keys)
            output.WriteLine($"{key}: median {medians[key]:R} of {values[key].Count} values.");

        var camera = ThnScriptModel.From(ThnTemplates.NewScript(), ThnElementId.Counter()).EntityNamed("Camera_01")!;
        var lens = (ThnTable)camera.Table["cameraprops"]!;
        foreach (var key in Keys)
            Assert.Equal(medians[key], ((ThnNumber)lens[key]!).Value);
    }

    /// <summary>The median of the values: the value in the middle, or for an even count the lower of the two middle
    /// values, so that the median is a value that a retail camera has.</summary>
    private static float Median(List<float> values)
    {
        var sorted = values.Order().ToList();
        return sorted[(sorted.Count - 1) / 2];
    }
}

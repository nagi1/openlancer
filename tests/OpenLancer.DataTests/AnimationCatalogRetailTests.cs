using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using LibreLancer.Thn;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Inspector;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The clip list of the animation picker on the retail data (section 12.1 of the M8b design): the list has the names of
/// the animation table of the engine, the last file wins for a name in two files, each START_MOTION value on a character
/// is in the list, and each costume has a standing idle for the clip preview.
/// </summary>
public sealed class AnimationCatalogRetailTests(ITestOutputHelper output)
{
    /// <summary>The list has the names of the table that the engine plays from, without letter case. A facial clip that
    /// two files have comes from the later file, FacialJuni.anm.</summary>
    [Fact]
    public void TheListHasTheNamesOfTheEngineTable()
    {
        GameDataPaths.RequireBase();
        var data = RetailGame.Instance.Data;
        var clock = Stopwatch.StartNew();
        var clips = AnimationClips.Read(data);
        var read = clock.Elapsed;
        var table = data.GetCharacterAnimations().Scripts.Keys;

        Assert.Equal(table.Count, clips.Clips.Count);
        Assert.True(clips.Clips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(table));
        var flicker = clips.Find("Sc_ADHEAD_MOTION_FLCKR_EYES_000LV_XA_06");
        Assert.NotNull(flicker);
        Assert.Equal("FacialJuni.anm", Path.GetFileName(flicker.Archive.Replace('\\', '/')), ignoreCase: true);
        output.WriteLine($"{clips.Clips.Count} clips in {clips.Clips.Select(c => c.Archive).Distinct().Count()} files, " +
                         $"read in {read.TotalMilliseconds:0.0} ms.");
    }

    /// <summary>
    /// Each START_MOTION value of the retail cutscenes whose first target is a character (DEFORMABLE) is in the list. A
    /// value on a COMPOUND names a script of its model, so the row of such an event keeps its text field (risk R5).
    /// </summary>
    [Fact]
    public void EachRetailAnimationOfACharacterIsInTheList()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var clips = AnimationClips.Read(RetailGame.Instance.Data);
        var evaluator = ThornEvaluator.Instance;
        var path = new ThnPath(ThnAnimationPicker.AnimationPath);
        var (characters, others) = (0, 0);
        var missing = new List<string>();
        var otherFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            var model = ThnScriptModel.From(evaluator.Evaluate(view.ReadAllBytes(file), file), ThnElementId.Counter());
            foreach (var ev in model.Events)
            {
                if (ThnSchema.TypeOf(ThnElementKind.Event, ev.Table, evaluator.Symbols) != "START_MOTION" ||
                    ThnElementPaths.Get(ThnElementKind.Event, ev.Table, path) is not ThnText value)
                    continue;
                var target = ev.Table.List.Length > 2 && ev.Table.List[2] is ThnTable { List: [ThnText first, ..] }
                    ? model.EntityNamed(first.Value)
                    : null;
                var type = target == null ? null : ThnSchema.TypeOf(ThnElementKind.Entity, target.Table, evaluator.Symbols);
                if (!ThnAnimationPicker.Applies(EventTypes.StartMotion, path.Text, type))
                {
                    others++;
                    otherFiles.Add(file);
                    continue;
                }

                characters++;
                if (!clips.Contains(value.Value))
                    missing.Add($"{file}: {value.Value}");
            }
        }

        output.WriteLine($"START_MOTION values: {characters} on a DEFORMABLE, {others} on another target in " +
                         $"{otherFiles.Count} files.");
        Assert.Empty(missing);
        Assert.True(characters > 10_000, $"Only {characters} values on a DEFORMABLE.");
        Assert.True(others > 0);
    }
}

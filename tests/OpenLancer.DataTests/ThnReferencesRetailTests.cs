using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The cutscene references of the retail INI files (test 13.2 item 4 of the M8 design). The numbers are the numbers of
/// section 6.4, which the scratch program <c>m8py\thnrefs2.py</c> found with its own INI reader.
/// </summary>
public sealed class ThnReferencesRetailTests(ITestOutputHelper output)
{
    /// <summary>The references of each section and key in retail, from the table of section 6.4.</summary>
    private static readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["[Room_Info] scene"] = 666,
        ["[GenericScripts] script"] = 617,
        ["[MRoom] fixture"] = 559,
        ["[Room_Info] set_script"] = 443,
        ["[CharacterPlacement] start_script"] = 208,
        ["[GCS_Exclusions] script"] = 126,
        ["[Char] fidget"] = 83,
        ["[Trigger] Act_CallThorn"] = 70,
        ["[CharacterEncounter] action"] = 42,
        ["[CharacterEncounter] offer"] = 26,
        ["[CharacterEncounter] decision"] = 26,
        ["[CharacterEncounter] accept"] = 26,
        ["[CharacterEncounter] decline"] = 19,
        ["[CharacterEncounter] reject"] = 4,
        ["[PlayerShipPlacement] landing_script"] = 29,
        ["[PlayerShipPlacement] launching_script"] = 29,
        ["[Room_Info] goodscart_script"] = 19,
        ["[Trigger] Act_AddAmbient"] = 4,
        ["[Trigger] Act_RemoveAmbient"] = 4,
    };

    /// <summary>
    /// The scan finds 1,292 scripts, and each one exists. Each section and key has the count of the design. 75 values
    /// double the backslash, all of them trigger values. Without the path rule, 61 of the named scripts have no file
    /// with that name, and 214 scripts have no reference.
    /// </summary>
    [Fact]
    public void TheRetailReferencesName1292ScriptsThatAllExist()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var watch = Stopwatch.StartNew();
        var references = ThnReferences.Scan(view);
        var time = watch.Elapsed;

        Assert.Empty(references.Problems);
        Assert.Equal(1292, references.Paths.Count);
        Assert.DoesNotContain(references.Paths, p => !view.FileExists(p));

        var counts = references.All.CountBy(r => $"[{r.Section}] {r.Key}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, count) in Counts)
            Assert.True(counts.GetValueOrDefault(key) == count, $"{key}: {counts.GetValueOrDefault(key)}, not {count}.");
        Assert.Equal(Counts.Keys.Order(StringComparer.OrdinalIgnoreCase),
            counts.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3000, references.All.Count);

        var doubled = references.All.Where(r => r.Value.Contains(@"\\", StringComparison.Ordinal)).ToList();
        Assert.Equal(75, doubled.Count);
        var doubledKeys = doubled.CountBy(r => $"[{r.Section}] {r.Key}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(67, doubledKeys["[Trigger] Act_CallThorn"]);
        Assert.Equal(4, doubledKeys["[Trigger] Act_AddAmbient"]);
        Assert.Equal(4, doubledKeys["[Trigger] Act_RemoveAmbient"]);

        // Without the path rule, a value gives the path DATA\<value> as it is.
        var scripts = VfsFiles.Find(view, ".thn").ToHashSet(VfsPath.Comparer);
        var unruled = references.All.Select(r => @"DATA\" + r.Value).Distinct(VfsPath.Comparer).ToList();
        var seemMissing = unruled.Count(p => !scripts.Contains(p));
        Assert.Equal(61, seemMissing);
        Assert.Equal(1506, scripts.Count);
        Assert.Equal(214, scripts.Count(s => !references.Paths.Contains(s, VfsPath.Comparer)));

        output.WriteLine($"Scan: {time.TotalMilliseconds:F0} ms, {references.All.Count} references of " +
                         $"{references.Paths.Count} scripts, 0 missing. {doubled.Count} values with a doubled " +
                         "backslash. Without the path rule, " +
                         $"{seemMissing} scripts seem to be missing. {scripts.Count - references.Paths.Count} of " +
                         $"{scripts.Count} scripts have no reference.");
        foreach (var (key, count) in counts.OrderByDescending(c => c.Value))
            output.WriteLine($"  {key}: {count}");
    }

}

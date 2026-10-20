using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of <c>ol.replace</c> (section 6.9 item 10 of the M6b design, step 1.1): a forced <c>ol.replace</c> of
/// each section of a mission type whose line order counts, with no change, gives merged sections equal to the retail
/// sections. The keys, the values, the value types and the line order are the same. The proofs read the 14 vanilla
/// scripts, and the 14 HD scripts and the 2 HD open single player scripts with <c>OPENLANCER_HDE</c>. They only read
/// the data folders and hold no text of the scripts.
/// </summary>
public sealed class MissionReplaceRetailTests(ITestOutputHelper output)
{
    private static readonly Lazy<MissionRunResult> Hde = new(() =>
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireHde(), "m6breplace");
        return MissionRun.Execute(workspace.Open().MissionInputs(EngineCoverage.Instance));
    });

    [Fact]
    public void AForcedReplaceOfEachVanillaSectionGivesTheRetailSections()
    {
        var folder = GameDataPaths.RequireBase();
        var totals = RoundTrip(folder, Scripts(RetailMissions.Run()));

        // The sections of these types with a repeated nickname stay read-only (decision D20).
        Assert.Equal((14, 5083, 14), (totals.Scripts, totals.Replaced, totals.Repeated));
    }

    [Fact]
    public void AForcedReplaceOfEachHdSectionGivesTheRetailSections()
    {
        var folder = GameDataPaths.RequireHde();
        var totals = RoundTrip(folder, Scripts(Hde.Value));

        Assert.Equal((16, 4498, 12), (totals.Scripts, totals.Replaced, totals.Repeated));
    }

    /// <summary>
    /// The sections with a line before their key line were read-only before M6b, because a patch by names could not
    /// keep their order: 116 triggers and 198 order lists in vanilla, 100 triggers and 163 order lists in HD Edition.
    /// Now each of them is editable, except a section whose nickname repeats.
    /// </summary>
    [Fact]
    public void TheSectionsWithALineBeforeTheKeyLineAreEditable()
    {
        var vanilla = LineBeforeKey(GameDataPaths.RequireBase(), Scripts(RetailMissions.Run()));
        Assert.Equal((116, 198), (vanilla.GetValueOrDefault("Trigger"), vanilla.GetValueOrDefault("ObjList")));

        if (Environment.GetEnvironmentVariable(GameDataPaths.HdeVariable) is not { Length: > 0 })
            return;
        var hd = LineBeforeKey(GameDataPaths.RequireHde(), Scripts(Hde.Value));
        Assert.Equal((100, 163), (hd.GetValueOrDefault("Trigger"), hd.GetValueOrDefault("ObjList")));
    }

    private static IReadOnlyList<string> Scripts(MissionRunResult run) =>
        run.Scripts.Where(s => s.State != MissionScriptState.Missing).Select(s => s.Path).ToList();

    private sealed record Totals(int Scripts, int Replaced, int Repeated);

    private Totals RoundTrip(string folder, IReadOnlyList<string> scripts)
    {
        var replaced = 0;
        var repeated = 0;
        foreach (var path in scripts)
        {
            var b = IniDocument.Parse(path, File.ReadAllBytes(Path.Combine(folder, path)));
            var counts = new KeyCounts(b.Sections);
            var forced = new Dictionary<SectionIdentity, IReadOnlyCollection<string>>();
            var ids = SectionIdentity.Identify(b.Sections);
            for (var i = 0; i < b.Sections.Count; i++)
            {
                var section = b.Sections[i];
                if (!SectionKeys.IsReplaced(section.Name, path))
                    continue;
                if (PatchWriter.NotPatchable(section, kept: true, counts, counts, path) != null)
                {
                    repeated++;
                    continue;
                }
                // A pin of each name makes the writer write the section, also with no change.
                forced[ids[i]] = section.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            var patch = PatchWriter.Write(path, b, b.Sections, id => forced.GetValueOrDefault(id) ?? []);
            Assert.True(patch.Succeeded, $"{path}: {string.Join("\n", patch.Problems)}");
            var replacedHere = patch.Sections.Count;
            Assert.All(patch.Sections, s => Assert.Contains(IniPatcher.DirectiveReplace + " = true", s.Lines));

            var merged = b.WithSections(b.Sections);
            var diagnostics = new List<Diagnostic>();
            IniPatcher.Apply(merged, IniDocument.Parse(path, patch.ToBytes()).Sections,
                new PatchContext("proof", path, path, null, new PatchLedger(), diagnostics, keepPlaceholders: true));
            Assert.Empty(diagnostics);
            Assert.Equal(b.Sections.Count, merged.Sections.Count);
            for (var i = 0; i < b.Sections.Count; i++)
                AssertSame(path, b.Sections[i], merged.Sections[i], b.IsBinary);

            replaced += replacedHere;
            output.WriteLine($"{path}: {replacedHere} sections replaced, {(b.IsBinary ? "binary" : "text")}");
        }
        output.WriteLine($"{scripts.Count} scripts, {replaced} sections replaced, {repeated} sections with a repeated key");
        return new Totals(scripts.Count, replaced, repeated);
    }

    /// <summary>The same keys, values, value types and line order.</summary>
    private static void AssertSame(string path, Section expected, Section actual, bool binary)
    {
        var where = $"{path} [{expected.Name}]";
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Select(e => e.Name), actual.Select(e => e.Name));
        for (var e = 0; e < expected.Count; e++)
        {
            Assert.Equal(expected[e].Count, actual[e].Count);
            for (var v = 0; v < expected[e].Count; v++)
            {
                var a = expected[e][v];
                var c = actual[e][v];
                Assert.True(Kind(a) == Kind(c) && IniValueComparer.StrictEqual(a, c, binary),
                    $"{where} {expected[e].Name}: {a} ({a.GetType().Name}) came back as {c} ({c.GetType().Name}).");
            }
        }
    }

    /// <summary>The value type that a file stores: an integer, a float, a boolean or text.</summary>
    private static string Kind(ValueBase value) => value switch
    {
        Int32Value => "integer",
        SingleValue => "float",
        BooleanValue => "boolean",
        _ => "text",
    };

    /// <summary>The number of sections of each mission type whose line order counts with a line before the key line.
    /// The test asserts that each of them is editable now: the order rule does not refuse it, and a patch can find it
    /// unless its nickname repeats.</summary>
    private Dictionary<string, int> LineBeforeKey(string folder, IReadOnlyList<string> scripts)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in scripts)
        {
            var b = IniDocument.Parse(path, File.ReadAllBytes(Path.Combine(folder, path)));
            var counts = new KeyCounts(b.Sections);
            foreach (var section in b.Sections.Where(s => SectionKeys.IsReplaced(s.Name, path)))
            {
                if (section.Count == 0 || section[0].Name.Equals("nickname", StringComparison.OrdinalIgnoreCase))
                    continue;
                result[section.Name] = result.GetValueOrDefault(section.Name) + 1;
                Assert.Null(PatchWriter.OrderNotKept(section, path));
                // The same section in a file outside the missions keeps the old rule.
                if (SectionKeys.IsOrderSensitive(section.Name))
                    Assert.NotNull(PatchWriter.OrderNotKept(section, @"DATA\other.ini"));
                // A patch finds each such section, except a section whose nickname another section of its type has
                // (decision D20). That section stays read-only.
                if (PatchWriter.NotPatchable(section, kept: true, counts, counts, path) is { } reason)
                {
                    output.WriteLine($"{path} [{section.Name}]: {reason}");
                    Assert.True(SectionIdentity.KeyOf(section) is { } key && counts.Keys(section.Name, key) > 1,
                        $"{path} [{section.Name}]: {reason}");
                }
            }
        }
        output.WriteLine(string.Join(", ", result.Select(r => $"{r.Key} {r.Value}")));
        return result;
    }
}

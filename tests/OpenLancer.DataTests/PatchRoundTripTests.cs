using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the patch writer. Each proof reads every retail INI file and never writes the base folder.
/// </summary>
public sealed class PatchRoundTripTests(ITestOutputHelper output)
{
    // The counts of the extracted English retail v1.0 data. All files except 4 text files are binary.
    private const int RetailIniFiles = 1_255;
    private const int RetailBinaryFiles = 1_251;
    private const int RetailBinarySections = 70_250;
    private const int RetailBinaryEntries = 511_556;
    private const int RetailBinaryValues = 876_034;
    private const int RetailTextSections = 83;

    /// <summary>
    /// The sections that a patch can find by their key: one key entry with one value, which no other section of the
    /// type in the file has. Of these, this many are child sections. The key rules of milestone M25 added 163 of them:
    /// the 145 <c>[DynamicAsteroids]</c> sections and the 18 <c>[LootableZone]</c> sections with a <c>zone</c> line.
    /// </summary>
    private const int RetailUniqueKeySections = 31_537;
    private const int RetailUniqueKeyChildren = 1_643;

    /// <summary>Proof 1(d) writes back this many sections of a file at a time.</summary>
    private const int Chunk = 64;

    /// <summary>The binary files store this many booleans as the strings "true" and "false", in any letter case.</summary>
    private const int RetailBoolStrings = 12_699;

    /// <summary>
    /// The bool strings that are not lowercase: True 1,828, False 139, FALSE 38 and TRUE 17. Most are
    /// <c>mission_eligible</c> in zones and the flags of AI pilot blocks.
    /// </summary>
    private const int RetailCapitalBoolStrings = 2_022;

    /// <summary>
    /// Proof 1(c). Each retail line goes through the patch formatter, the engine's text parser and the merge's copy
    /// rule, as a patch line that replaces the same line. Every value must come back strictly equal: the file
    /// stores the same bytes. The copies of each binary file also go through the binary writer and reader. Without
    /// the binary type rules, exactly the bool strings fail.
    /// </summary>
    [Fact]
    public void EveryRetailValueComesBackStrictlyEqualThroughAPatchLine()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var watch = Stopwatch.StartNew();
        var diagnostics = new List<Diagnostic>();
        var context = new PatchContext("proof", "proof", "proof", null, new PatchLedger(), diagnostics);
        var binaryTotals = new Totals();
        var textTotals = new Totals();
        var failures = new List<string>();
        var failuresWithoutTypeRules = new List<ValueBase>();

        foreach (var (file, document) in RetailDocuments(baseFolder))
        {
            var binary = document.IsBinary;
            var totals = binary ? binaryTotals : textTotals;
            totals.Files++;
            var copies = new List<Section>();
            foreach (var section in document.Sections)
            {
                totals.Sections++;
                var copied = new Section(section.Name);
                foreach (var entry in section)
                {
                    totals.Entries++;
                    totals.Values += entry.Count;
                    var where = $"{file} [{section.Name}] {entry.Name}";
                    if (!PatchFormatter.TryFormatLine(entry, entry, binary, out var line, out var error))
                    {
                        failures.Add($"{where}: {error}");
                        continue;
                    }
                    var parsed = ReadLine(line);
                    var copy = IniPatcher.Copy(copied, parsed, context, binary, replaced: entry);
                    copied.Add(copy);
                    failures.AddRange(StrictDifferences(entry, copy, binary).Select(d => $"{where}: {d} from '{line}'."));
                    failuresWithoutTypeRules.AddRange(
                        entry.Where((value, i) => !IniValueComparer.StrictEqual(parsed[i], value, binary)));
                }
                copies.Add(copied);
            }
            if (binary)
                failures.AddRange(BinaryFileDifferences(file, document.Sections, copies));
        }

        output.WriteLine($"Binary: {binaryTotals}. Text: {textTotals}.");
        output.WriteLine($"Time: {watch.Elapsed.TotalSeconds:F1} s.");
        output.WriteLine($"Failures: {failures.Count}. Without the binary type rules: {failuresWithoutTypeRules.Count}.");
        foreach (var failure in failures.Take(20))
            output.WriteLine(failure);

        Assert.Equal(RetailIniFiles, binaryTotals.Files + textTotals.Files);
        Assert.Equal((RetailBinaryFiles, RetailBinarySections, RetailBinaryEntries, RetailBinaryValues),
            (binaryTotals.Files, binaryTotals.Sections, binaryTotals.Entries, binaryTotals.Values));
        Assert.Empty(failures);
        Assert.Empty(diagnostics);
        Assert.Equal(RetailBoolStrings, failuresWithoutTypeRules.Count);
        Assert.All(failuresWithoutTypeRules, v => Assert.True(v is StringValue && bool.TryParse(v.ToString(), out _), v.ToString()));
    }

    /// <summary>
    /// Each retail line goes through the formatter and the merge as a new line, with no replaced line. This is how
    /// a copy of a retail item works. Every value must come back strictly equal, except the bool strings that are
    /// not lowercase. The merge stores a new boolean as lowercase text, so the formatter refuses their lines. A copy
    /// writes them in lowercase: the engine then reads the same truth value.
    /// </summary>
    [Fact]
    public void EveryRetailValueExceptACapitalBoolStringComesBackThroughANewPatchLine()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var watch = Stopwatch.StartNew();
        var diagnostics = new List<Diagnostic>();
        var context = new PatchContext("proof", "proof", "proof", null, new PatchLedger(), diagnostics);
        var failures = new List<string>();
        var capitalBoolStrings = 0;

        foreach (var (file, document) in RetailDocuments(baseFolder))
        {
            var binary = document.IsBinary;
            foreach (var section in document.Sections)
            {
                foreach (var entry in section)
                {
                    var where = $"{file} [{section.Name}] {entry.Name}";
                    var capitals = entry.Count(ProofSections.IsCapitalBoolString);
                    if (PatchFormatter.TryFormatLine(entry, null, binary, out var line, out var error))
                    {
                        if (binary && capitals > 0)
                            failures.Add($"{where}: '{line}' is accepted, but the merge does not keep the letter case.");
                        var copy = IniPatcher.Copy(new Section(section.Name), ReadLine(line), context, binary, replaced: null);
                        failures.AddRange(StrictDifferences(entry, copy, binary).Select(d => $"{where}: {d} from '{line}'."));
                        continue;
                    }
                    if (!binary || capitals == 0)
                    {
                        failures.Add($"{where}: {error}");
                        continue;
                    }
                    capitalBoolStrings += capitals;
                    var lowercase = ProofSections.Lowercase(entry);
                    if (!PatchFormatter.TryFormatLine(lowercase, null, binary, out line, out error))
                    {
                        failures.Add($"{where}: the lowercase copy is refused: {error}");
                        continue;
                    }
                    var lowercaseCopy = IniPatcher.Copy(new Section(section.Name), ReadLine(line), context, binary, replaced: null);
                    failures.AddRange(StrictDifferences(lowercase, lowercaseCopy, binary).Select(d => $"{where}: {d} from '{line}'."));
                    if (!entry.Select(v => v.ToBoolean()).SequenceEqual(lowercaseCopy.Select(v => v.ToBoolean())))
                        failures.Add($"{where}: '{line}' changes a truth value.");
                }
            }
        }

        output.WriteLine($"Time: {watch.Elapsed.TotalSeconds:F1} s.");
        output.WriteLine($"Failures: {failures.Count}. Capital bool strings: {capitalBoolStrings}.");
        foreach (var failure in failures.Take(20))
            output.WriteLine(failure);

        Assert.Empty(failures);
        Assert.Empty(diagnostics);
        Assert.Equal(RetailCapitalBoolStrings, capitalBoolStrings);
    }

    /// <summary>
    /// Proof 1(b), a smoke test: for every retail file, a second parse of the same bytes gives an empty patch. The
    /// second parse shares no section and no value with the first, so every value goes through the comparer.
    /// </summary>
    [Fact]
    public void ASecondParseOfEveryRetailFileGivesAnEmptyPatch()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var watch = Stopwatch.StartNew();
        var failures = new List<string>();
        var sections = 0;

        foreach (var (file, document) in RetailDocuments(baseFolder))
        {
            var again = IniDocument.Parse(file, File.ReadAllBytes(Path.Combine(baseFolder, file)));
            sections += again.Sections.Count;
            var patch = PatchWriter.Write(file, document, again.Sections, _ => []);
            if (!patch.Succeeded || patch.Sections.Count > 0)
                failures.Add($"{file}: {string.Join("; ", patch.Problems)}{patch.Text}");
        }

        output.WriteLine($"Sections: {sections}. Time: {watch.Elapsed.TotalSeconds:F1} s. Failures: {failures.Count}.");
        Assert.Equal(RetailBinarySections + RetailTextSections, sections);
        Assert.Empty(failures);
    }

    /// <summary>
    /// Proof 1(d). Each retail section that a patch can find by its key leaves its file, with its child sections. The
    /// writer then writes the section back as a new section, and the merge must give the same lines. A copy writes
    /// a bool string in lowercase, because the merge stores a new boolean as lowercase text. So the capital bool
    /// strings are the only values that are not strictly equal.
    /// </summary>
    /// <remarks>
    /// Each write checks the whole file, so the sections go in chunks of <see cref="Chunk"/>: first the sections that
    /// are not child sections, with their child sections, and then the child sections with a key, which get
    /// <c>ol.parent</c>.
    /// </remarks>
    [Fact]
    public void EveryRetailSectionWithAUniqueKeyComesBackAsANewSection()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var watch = Stopwatch.StartNew();
        var proof = new NewSectionProof();

        foreach (var (file, document) in RetailDocuments(baseFolder))
        {
            var sections = document.Sections;
            var keyCounts = ProofSections.KeyCounts(sections);
            var unique = Enumerable.Range(0, sections.Count)
                .Where(i => ProofSections.KeyOf(sections[i]) is { } key && keyCounts[(sections[i].Name, key)] == 1)
                .ToList();
            foreach (var isChild in new[] { false, true })
            {
                foreach (var chunk in unique.Where(i => SectionKeys.IsChild(sections[i].Name) == isChild).Chunk(Chunk))
                    proof.WriteBack(file, document, chunk);
            }
        }

        output.WriteLine($"Written: {proof.Written}, of which children: {proof.Children}. Time: {watch.Elapsed.TotalSeconds:F1} s.");
        output.WriteLine($"Strict differences: {proof.StrictDifferences}. Capital bool strings: {proof.CapitalBoolStrings}. " +
                         $"Failures: {proof.Failures.Count}.");
        foreach (var failure in proof.Failures.Take(20))
            output.WriteLine(failure);

        Assert.Empty(proof.Failures);
        Assert.Equal(RetailUniqueKeySections, proof.Written);
        Assert.Equal(RetailUniqueKeyChildren, proof.Children);
        Assert.Equal(proof.CapitalBoolStrings, proof.StrictDifferences);
    }

    /// <summary>The state of proof 1(d).</summary>
    private sealed class NewSectionProof
    {
        public int Written;
        public int Children;
        public int StrictDifferences;
        public int CapitalBoolStrings;
        public readonly List<string> Failures = [];

        /// <summary>
        /// Removes the sections at <paramref name="chunk"/> from the file, writes them back as new sections and
        /// compares what the merge gives with the original sections.
        /// </summary>
        public void WriteBack(string file, IniDocument document, int[] chunk)
        {
            var sections = document.Sections;
            var ids = SectionIdentity.Identify(sections);
            var runs = chunk.ToDictionary(i => i, i => SectionKeys.IsChild(sections[i].Name) ? 1 : 1 + ProofSections.ChildCount(sections, i));
            var removed = runs.SelectMany(r => Enumerable.Range(r.Key, r.Value)).ToHashSet();
            var without = sections.Where((_, k) => !removed.Contains(k)).ToList();

            // The merge adds a new child after the other children of its parent, and any other new section at the end.
            var edited = new List<Section>();
            var at = new Dictionary<int, int>();
            var childrenOf = runs.Keys.Where(i => SectionKeys.IsChild(sections[i].Name)).ToLookup(i => ProofSections.Parent(sections, i));
            void AddChildren(int parentIndex)
            {
                foreach (var child in childrenOf[parentIndex])
                {
                    at[child] = edited.Count;
                    edited.Add(ProofSections.Copy(sections[child], document.IsBinary));
                }
            }
            AddChildren(-1);
            var parent = -1;
            for (var k = 0; k < sections.Count; k++)
            {
                if (removed.Contains(k))
                    continue;
                if (!SectionKeys.IsChild(sections[k].Name))
                {
                    AddChildren(parent);
                    parent = k;
                }
                edited.Add(sections[k]);
            }
            AddChildren(parent);
            foreach (var (start, count) in runs.Where(r => !SectionKeys.IsChild(sections[r.Key].Name)))
            {
                at[start] = edited.Count;
                edited.AddRange(sections.Skip(start).Take(count).Select(s => ProofSections.Copy(s, document.IsBinary)));
            }

            var patch = PatchWriter.Write(file, document.WithSections(without), edited, _ => []);
            if (!patch.Succeeded)
            {
                Failures.Add($"{file} {string.Join(", ", chunk.Select(i => ids[i]))}: " +
                             string.Join("; ", patch.Problems.Select(p => p.Message)));
                return;
            }
            var merged = Merge(document.WithSections(without), patch, Failures, file);
            if (merged.Count != edited.Count)
            {
                Failures.Add($"{file} {string.Join(", ", chunk.Select(i => ids[i]))}: the merge gives {merged.Count} " +
                             $"sections, not {edited.Count}.");
                return;
            }
            foreach (var (start, count) in runs)
            {
                Written++;
                Children += SectionKeys.IsChild(sections[start].Name) ? 1 : 0;
                for (var k = 0; k < count; k++)
                {
                    var original = sections[start + k];
                    var result = merged[at[start] + k];
                    Failures.AddRange(LineDifferences(edited[at[start] + k], result, document.IsBinary, IniValueComparer.EngineEqual)
                        .Select(d => $"{file} {ids[start + k]}: {d}"));
                    StrictDifferences += LineDifferences(original, result, document.IsBinary, IniValueComparer.StrictEqual).Count();
                    CapitalBoolStrings += original.SelectMany(e => e).Count(ProofSections.IsCapitalBoolString);
                }
            }
        }
    }

    /// <summary>Applies a patch to a copy of the section list with the real merge.</summary>
    private static List<Section> Merge(IniDocument target, PatchText patch, List<string> failures, string where)
    {
        var result = target.WithSections(target.Sections);
        var diagnostics = new List<Diagnostic>();
        IniPatcher.Apply(result, IniDocument.Parse("patch.ini", patch.ToBytes()).Sections,
            new PatchContext("proof", "patch.ini", where, null, new PatchLedger(), diagnostics));
        failures.AddRange(diagnostics.Select(d => $"{where}: the merge reports {d}"));
        return result.Sections;
    }

    /// <summary>The lines of two sections that differ in their name or in a value, by the value comparer.</summary>
    private static IEnumerable<string> LineDifferences(Section expected, Section actual, bool binary,
        Func<ValueBase, ValueBase, bool, bool> equal)
    {
        if (expected.Name != actual.Name || expected.Count != actual.Count)
            return [$"[{actual.Name}] with {actual.Count} lines, not [{expected.Name}] with {expected.Count}"];
        return expected.Zip(actual).SelectMany(p => p.First.Name != p.Second.Name || p.First.Count != p.Second.Count
            ? [$"'{p.Second}', not '{p.First}'"]
            : p.First.Select((value, i) => (value, i))
                .Where(v => !equal(v.value, p.Second[v.i], binary))
                .Select(v => $"{p.First.Name}: value {v.i + 1} '{v.value}' came back as '{p.Second[v.i]}'"));
    }

    /// <summary>Every retail INI file, parsed, with its path relative to the base folder.</summary>
    private static IEnumerable<(string File, IniDocument Document)> RetailDocuments(string baseFolder) =>
        Directory.EnumerateFiles(baseFolder, "*.ini", SearchOption.AllDirectories)
            .Select(file => (Path.GetRelativePath(baseFolder, file), IniDocument.Parse(file, File.ReadAllBytes(file))));

    /// <summary>Parses one patch line with the engine's text parser.</summary>
    private static Entry ReadLine(string line) =>
        IniFile.ParseFile("patch.ini", new MemoryStream(Encoding.ASCII.GetBytes("[Patch]\r\n" + line + "\r\n")))
            .Single().Single();

    private static IEnumerable<string> StrictDifferences(Entry expected, Entry actual, bool binary)
    {
        if (actual.Count != expected.Count)
            return [$"{actual.Count} values, not {expected.Count}"];
        return expected.Select((value, i) => (value, i))
            .Where(p => !IniValueComparer.StrictEqual(actual[p.i], p.value, binary))
            .Select(p => $"value {p.i + 1} '{p.value}' came back as '{actual[p.i]}'");
    }

    /// <summary>
    /// Writes the copied sections with the binary writer, reads the bytes back with the binary reader, and compares
    /// every value with the original file.
    /// </summary>
    private static IEnumerable<string> BinaryFileDifferences(string file, IReadOnlyList<Section> original,
        IReadOnlyList<Section> copies)
    {
        using var stream = new MemoryStream();
        IniWriter.WriteBini(stream, copies);
        var read = IniDocument.Parse(file, stream.ToArray());
        if (!read.IsBinary || read.Sections.Count != original.Count)
            return [$"{file}: the binary copy reads back {read.Sections.Count} sections, not {original.Count}."];
        return original.Zip(read.Sections).SelectMany(p => p.First.Count != p.Second.Count
            ? [$"{file} [{p.First.Name}]: the binary copy reads back {p.Second.Count} entries, not {p.First.Count}."]
            : p.First.Zip(p.Second).SelectMany(e =>
                StrictDifferences(e.First, e.Second, binary: true).Select(d => $"{file} [{p.First.Name}] {e.First.Name}: {d} in the bytes.")));
    }

    private sealed class Totals
    {
        public int Files;
        public int Sections;
        public int Entries;
        public int Values;

        public override string ToString() => $"{Files} files, {Sections} sections, {Entries} entries, {Values} values";
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Dump;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Proof 2: seeded random edits of the retail item, market, faction, base and star system files, over two lower
/// layers: retail, and retail with ol-sample.
/// </summary>
/// <remarks>
/// <para>Each edit is one that a patch can hold. So after each edit, the writer must write the patch of the file with
/// no problem. A change of one name in a section without other changes must give only the key and that name, or
/// its linked group.</para>
/// <para>At the end of each round, a quarter of the files go back to B, unit by unit. Then only their deleted
/// sections stay in the patch. Two mods then hold the edited files: one has the patches, and one has the edited
/// files as whole files. The merged files of the patch mod must give the edited files, and the engine must load the
/// same data from both mods (see <see cref="DataDump"/>), with the same engine messages.</para>
/// <para>Then each patch is written again as a modder can write it by hand (see <see cref="HandWrittenPatch"/>):
/// with comment lines, inline comments, other spaces, letter case and line ends, and in code page 1252 or UTF-8. A
/// second session loads that file as the patch of its active mod. With no edit, it must write the same lines with
/// CRLF line ends, in the same encoding. After it deletes one more section, every other part of the file must keep
/// its bytes (decisions D4 and D25). After it adds a line to a loaded section that changes a section of B, every
/// loaded line of that section must keep its text and its place.</para>
/// <para>A third session gets a lower layer that already has the result of one changed section, so that the lines of
/// that section equal B. It must still write the same bytes, because no edit touched these lines (rule F3).</para>
/// <para>One edit kind deletes a section and adds it again at the end with the same key. The patch must then delete
/// the section and add it again (review finding 3).</para>
/// <para>Two edit kinds of milestone M3 give a section of B a new key, which the patch writes as <c>ol.rename</c>, and
/// delete an object of a system file with its <c>parent</c> children.</para>
/// </remarks>
public sealed class RandomEditTests(ITestOutputHelper output)
{
    private const int Seed = 20_261_007;
    private const int EditsPerLayer = 10_000;
    private const int RoundsPerLayer = 5;
    private const int MaxEditsPerVisit = 12;

    /// <summary>
    /// A round stops after this many visits in a row that give no edit. A file stops its edits after a failure, so
    /// without this limit a round with many failures never ends.
    /// </summary>
    private const int MaxIdleVisits = 1_000;

    /// <summary>The engine loads of one round when only the engine lines differ (see <see cref="CompareRound"/>).</summary>
    private const int MaxLoads = 3;

    /// <summary>Each kind must occur at least this often.</summary>
    private const int MinEditsPerKind = 20;

    /// <summary>
    /// Entries whose target the engine needs: the engine load stops, or drops ship packages, when the target is
    /// missing. For example, a ship package names its hull good, and a hull good names its ship. The edits keep these
    /// entries and their targets, so that each round loads completely and the dump holds all data.
    /// </summary>
    private static readonly string[] RequiredReferences = ["hull", "ship", "motor", "explosion_arch", "next_ring", "prev_ring"];

    /// <summary>The entries that the edits never remove: the required references, and the category of a good.</summary>
    private static readonly string[] RequiredEntries = [.. RequiredReferences, "category"];

    static RandomEditTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void RandomEditsGiveTheSameDataThroughPatchesAsThroughWholeFiles()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var watch = Stopwatch.StartNew();
        var random = new Random(Seed);
        // The later sessions choose with their own generator, so that they do not change the edits.
        var sessionRandom = new Random(Seed + 1);
        var paths = EditablePaths(baseFolder);
        var run = new Run();
        Mod[][] layers = [[], [Mod.Load(GameDataPaths.SampleMod("ol-sample"))]];

        foreach (var mods in layers)
        {
            var lower = ModOverlay.Build(baseFolder, mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance,
                keepDocuments: true);
            Assert.False(lower.HasErrors, string.Join("\n", lower.Diagnostics));
            var requiredTargets = paths.SelectMany(p => lower.Document(p)!.Sections).SelectMany(s => s)
                .Where(e => RequiredReferences.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                .SelectMany(e => e.Select(v => v.ToString()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var round = 0; round < RoundsPerLayer; round++)
            {
                var files = new Dictionary<string, FileEditor>(VfsPath.Comparer);
                var idle = 0;
                for (var edits = 0; edits < EditsPerLayer / RoundsPerLayer && idle < MaxIdleVisits;)
                {
                    var path = paths[random.Next(paths.Count)];
                    if (!files.TryGetValue(path, out var file))
                        files[path] = file = new FileEditor(path, lower.Document(path)!, requiredTargets, random, run);
                    var before = edits;
                    for (var n = random.Next(1, MaxEditsPerVisit + 1); n > 0 && edits < EditsPerLayer / RoundsPerLayer && file.EditOnce(); n--)
                        edits++;
                    idle = edits == before ? idle + 1 : 0;
                }
                if (idle == MaxIdleVisits)
                    run.Failures.Add($"Round {run.Rounds + 1}: {MaxIdleVisits} visits in a row give no edit.");
                foreach (var file in files.Values.Where(_ => random.Next(4) == 0))
                    file.RevertAll();
                CompareRound(baseFolder, lower, files.Values, sessionRandom, run);
            }
        }

        output.WriteLine($"Files: {paths.Count}. Time: {watch.Elapsed.TotalSeconds:F1} s, of which the writer: {run.WriteTime.Elapsed.TotalSeconds:F1} s.");
        output.WriteLine("Edits: " + string.Join(", ", run.Edits.Select(e => $"{e.Key} {e.Value}")) + $". Total: {run.Edits.Values.Sum()}.");
        output.WriteLine($"Single-name checks: {run.SingleNameChecks}. Files reverted: {run.Reverted}. Rounds: {run.Rounds}, " +
                         $"with a complete engine load: {run.CompleteLoads}. Files compared: {run.FilesCompared}. " +
                         $"Loads again for late engine lines: {run.LateEngineLines}. Rounds where the engine loads other " +
                         $"data: {run.OtherEngineData}. Second sessions: {run.SecondSessions}, of which UTF-8: " +
                         $"{run.Utf8Sessions}, with a delete: {run.SecondSessionEdits}, then with a new line in a loaded " +
                         $"section: {run.SecondSessionAdds}, over a lower layer with a changed section: {run.LowerLayerSessions}.");
        foreach (var load in run.IncompleteLoads)
            output.WriteLine(load.Length > 400 ? load[..400] : load);
        output.WriteLine($"Failures: {run.Failures.Count}.");
        foreach (var failure in run.Failures.Take(20))
            output.WriteLine(failure);

        Assert.Empty(run.Failures);
        Assert.Equal(2 * EditsPerLayer, run.Edits.Values.Sum());
        Assert.Equal(Enum.GetNames<EditKind>().Order(), run.Edits.Keys.Order());
        Assert.All(run.Edits, e => Assert.True(e.Value >= MinEditsPerKind, $"{e.Key}: {e.Value} edits"));
        Assert.Equal(2 * RoundsPerLayer, run.CompleteLoads);
        Assert.Equal(run.FilesCompared, run.SecondSessions);
        Assert.InRange(run.Utf8Sessions, run.SecondSessions / 4, run.SecondSessions * 3 / 4);
        Assert.True(run.SecondSessionEdits > run.SecondSessions / 2, $"{run.SecondSessionEdits} second-session deletes");
        Assert.True(run.SecondSessionAdds > run.SecondSessions / 4, $"{run.SecondSessionAdds} second-session new lines");
        Assert.True(run.LowerLayerSessions > run.SecondSessions / 2, $"{run.LowerLayerSessions} lower-layer sessions");
    }

    /// <summary>
    /// Writes the edited files of a round into two mods, one with patches and one with whole files, and compares
    /// the data that each mod gives.
    /// </summary>
    private static void CompareRound(string baseFolder, ModOverlay lower, IEnumerable<FileEditor> files, Random random,
        Run run)
    {
        run.Rounds++;
        var lowerMods = lower.Mods;
        var root = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var patches = CreateMod(root, "proof-patches");
            var wholeFiles = CreateMod(root, "proof-files");
            var edited = new List<(FileEditor File, PatchText Patch)>();
            foreach (var file in files.Where(f => !f.Broken))
            {
                var patch = file.Document.Write();
                if (patch.Sections.Count == 0)
                    continue;
                WriteFile(Path.Combine(patches, Mod.PatchesFolder, file.Document.Path), patch.ToBytes());
                WriteFile(Path.Combine(wholeFiles, Mod.FilesFolder, file.Document.Path), file.WholeFile());
                edited.Add((file, patch));
            }

            var withPatches = ModOverlay.Build(baseFolder, [.. lowerMods, Mod.Load(patches)], DllStringResourceWriter.Instance,
                ThornEvaluator.Instance);
            var withFiles = ModOverlay.Build(baseFolder, [.. lowerMods, Mod.Load(wholeFiles)], DllStringResourceWriter.Instance,
                ThornEvaluator.Instance);
            run.Failures.AddRange(withPatches.Diagnostics.Concat(withFiles.Diagnostics)
                .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"Round {run.Rounds}: {d}"));

            // The merge of each patch gives the edited file.
            var fs = withPatches.CreateFileSystem();
            foreach (var (file, _) in edited)
            {
                run.FilesCompared++;
                var merged = IniDocument.Parse(file.Document.Path, fs.ReadAllBytes(file.Document.Path)).Sections;
                var working = file.Document.Working;
                var same = merged.Count == working.Count &&
                           merged.Zip(working).All(p =>
                               SectionComparer.Equivalent(p.First, p.Second, file.Document.IsBinary, file.Document.Path));
                if (!same)
                    run.Failures.Add($"Round {run.Rounds}: the merged {file.Document.Path} differs from the edited file.");
            }

            // The later sessions load each patch as a modder can write it by hand.
            var handFolder = CreateMod(root, "proof-hand");
            var handWritten = new Dictionary<string, HandWrittenPatch>(VfsPath.Comparer);
            foreach (var (file, patch) in edited)
            {
                handWritten[file.Document.Path] = HandWrittenPatch.From(patch.Text, random);
                WriteFile(Path.Combine(handFolder, Mod.PatchesFolder, file.Document.Path), handWritten[file.Document.Path].File);
            }
            var handMod = Mod.Load(handFolder);
            foreach (var (file, patch) in edited)
            {
                var hand = handWritten[file.Document.Path];
                CheckSecondSession(file.Document.Path, lower.Document(file.Document.Path)!, handMod, hand, random, run);
                CheckLowerLayerSession(file.Document, lower.Document(file.Document.Path)!, handMod, patch, hand, random, run);
            }

            var fromPatches = Snapshot(withPatches);
            var fromFiles = Snapshot(withFiles);
            if (fromPatches.Incomplete == null)
                run.CompleteLoads++;
            else
                run.IncompleteLoads.Add($"Round {run.Rounds}: {fromPatches.Incomplete}");
            // Upstream GameItemDb.LoadData can return before the tasks that InitSystems starts are done, because
            // LoadingTasks.WaitAll copies its task list before InitSystems adds them. A late line of such a task, for
            // example a missing exclusion zone, then misses the log capture. So when only the engine lines differ,
            // both loads run again. A difference in the data occurs in each load.
            for (var load = 2; load <= MaxLoads && fromPatches.Dump == fromFiles.Dump && fromPatches.Lines != fromFiles.Lines; load++)
            {
                run.LateEngineLines++;
                fromPatches = Snapshot(withPatches);
                fromFiles = Snapshot(withFiles);
            }
            if (fromPatches.Dump != fromFiles.Dump || fromPatches.Lines != fromFiles.Lines)
            {
                run.OtherEngineData++;
                // Keep both snapshots outside the repo, to compare them.
                var outFolder = GameDataPaths.OutFolder(baseFolder);
                var patchText = fromPatches.Dump + "\n" + fromPatches.Lines;
                var fileText = fromFiles.Dump + "\n" + fromFiles.Lines;
                File.WriteAllText(Path.Combine(outFolder, $"random-edits-round{run.Rounds}-patches.txt"), patchText);
                File.WriteAllText(Path.Combine(outFolder, $"random-edits-round{run.Rounds}-files.txt"), fileText);
                run.Failures.Add($"Round {run.Rounds}: the engine loads other data. " +
                                 $"{FirstDifference(patchText, fileText, "with patches", "with whole files")} " +
                                 $"The snapshots are in {outFolder}.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // A file that is still open stays until the next temp cleanup.
            }
        }
    }

    /// <summary>
    /// Opens a file in a second session, with the hand-written patch as the patch of the active mod. The session must
    /// write the same lines. Then it deletes one section that the patch does not change. The other parts of the file
    /// must keep their bytes, and only the lines of the delete come in. Then it adds a line to a loaded section, and
    /// only that line comes in.
    /// </summary>
    private static void CheckSecondSession(string path, IniDocument b, Mod handMod, HandWrittenPatch hand, Random random,
        Run run)
    {
        run.SecondSessions++;
        if (hand.Utf8)
            run.Utf8Sessions++;
        var keys = ProofSections.KeyCounts(b.Sections);
        var diagnostics = new List<Diagnostic>();
        var document = EditDocument.Open(path, b, handMod, diagnostics);
        var again = document.Write();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) || !again.Succeeded ||
            !again.ToBytes().AsSpan().SequenceEqual(hand.Expected))
        {
            run.Failures.Add($"{path}: a second session does not keep the hand-written lines. " +
                             string.Join("; ", diagnostics.Concat(again.Problems)) + " " +
                             FirstDifference(Decode(hand.Expected), again.Text, "hand-written", "written"));
            return;
        }

        // A section of B that the patch does not change, with a key that no other section has, and with no children.
        var inPatch = again.Sections.Select(s => s.Id).ToHashSet();
        var w = document.Working;
        var candidates = Enumerable.Range(0, w.Count)
            .Where(j => !inPatch.Contains(document.IdentityAt(j)) && !SectionKeys.IsChild(w[j].Name) &&
                        ProofSections.KeyOf(w[j]) is { } key && keys.GetValueOrDefault((w[j].Name, key)) == 1 &&
                        (j + 1 == w.Count || !SectionKeys.IsChild(w[j + 1].Name)))
            .ToList();
        if (candidates.Count == 0)
            return;
        var index = candidates[random.Next(candidates.Count)];
        var deleted = document.IdentityAt(index);
        document.Remove(index, 1);
        var edited = document.Write();
        run.SecondSessionEdits++;
        var stub = edited.Sections.SingleOrDefault(s => s.Id.Equals(deleted));
        var kept = edited.Sections.Where(s => !ReferenceEquals(s, stub)).ToList();
        if (!edited.Succeeded || stub?.Lines[^1] != "ol.delete = true" || kept.Count != again.Sections.Count ||
            kept.Zip(again.Sections).Any(p => !p.First.Id.Equals(p.Second.Id)))
        {
            run.Failures.Add($"{path}: after the delete of {deleted}, the patch does not have the same other sections. " +
                             string.Join("; ", edited.Problems));
            return;
        }
        // The file of the second session with the delete put in where the writer puts it.
        var others = new Queue<PatchSection>(again.Sections);
        var expected = again with { Sections = edited.Sections.Select(s => ReferenceEquals(s, stub) ? s : others.Dequeue()).ToList() };
        if (!edited.ToBytes().AsSpan().SequenceEqual(expected.ToBytes()))
        {
            run.Failures.Add($"{path}: after the delete of {deleted}, the other parts of the patch do not keep their bytes. " +
                             FirstDifference(expected.Text, edited.Text, "expected", "written"));
            return;
        }

        // Then a new line goes into a loaded section that changes a section of B. The writer then writes that section
        // line by line, and each loaded line must keep its text, its comment lines and its place.
        var changed = edited.Sections
            .Where(s => !ReferenceEquals(s, stub) && document.IndexOf(s.Id) >= 0 && keys.ContainsKey((s.Id.Type, s.Id.Key ?? "")) &&
                        !s.Lines.Any(l => l.TrimStart().StartsWith("ol.new", StringComparison.OrdinalIgnoreCase) ||
                                          l.TrimStart().StartsWith("ol.delete", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (changed.Count == 0)
            return;
        var target = changed[random.Next(changed.Count)];
        var j = document.IndexOf(target.Id);
        var name = run.FreshKey();
        var section = new Section(document.Working[j].Name);
        foreach (var entry in document.Working[j])
            section.Add(entry);
        section.Add(new Entry(section, name) { new Int32Value(7) });
        document.Replace(j, section, [name]);
        var added = document.Write();
        run.SecondSessionAdds++;
        var line = $"{name} = 7";
        var written = added.Sections.SingleOrDefault(s => s.Id.Equals(target.Id));
        if (!added.Succeeded || written == null || written.Lines.Count(l => l == line) != 1 ||
            !written.Lines.Where(l => l != line).SequenceEqual(target.Lines) ||
            !added.Sections.Where(s => !ReferenceEquals(s, written)).Select(s => s.Lines).SequenceEqual(
                edited.Sections.Where(s => !ReferenceEquals(s, target)).Select(s => s.Lines), new LinesComparer()))
            run.Failures.Add($"{path}: after a new line in {target.Id}, its loaded lines do not stay. " +
                             string.Join("; ", added.Problems) + " " +
                             FirstDifference(string.Join("\n", target.Lines), string.Join("\n", written?.Lines ?? []), "loaded", "written"));
    }

    /// <summary>Compares the lines of two sections.</summary>
    private sealed class LinesComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y) => x != null && y != null && x.SequenceEqual(y);

        public int GetHashCode(IReadOnlyList<string> obj) => obj.Count;
    }

    /// <summary>
    /// Opens a file in a session whose lower layer already has the result of one changed section of the patch, as
    /// when a lower mod sets the same values. The lines of that section then equal B, and no edit touched them. So the
    /// session must write the same bytes as the hand-written patch (rule F3).
    /// </summary>
    private static void CheckLowerLayerSession(EditDocument first, IniDocument b, Mod handMod, PatchText written,
        HandWrittenPatch hand, Random random, Run run)
    {
        var ids = SectionIdentity.Identify(b.Sections);
        var changed = written.Sections
            .Where(s => !s.Lines.Contains("ol.delete = true") && !s.Lines.Contains("ol.new = true") &&
                        Array.IndexOf(ids, s.Id) >= 0 && first.IndexOf(s.Id) >= 0)
            .ToList();
        if (changed.Count == 0)
            return;
        run.LowerLayerSessions++;
        var id = changed[random.Next(changed.Count)].Id;
        var sections = b.Sections.ToList();
        sections[Array.IndexOf(ids, id)] = first.Working[first.IndexOf(id)];
        var diagnostics = new List<Diagnostic>();
        var again = EditDocument.Open(first.Path, b.WithSections(sections), handMod, diagnostics).Write();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) || !again.Succeeded ||
            !again.ToBytes().AsSpan().SequenceEqual(hand.Expected))
            run.Failures.Add($"{first.Path}: over a lower layer that has the result of {id}, a session writes other bytes. " +
                             string.Join("; ", diagnostics.Concat(again.Problems)) + " " +
                             FirstDifference(Decode(hand.Expected), again.Text, "hand-written", "written"));
    }

    /// <summary>The text of the bytes as the engine's text parser reads them.</summary>
    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), IniDocument.TextEncoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The dump of the data that the engine loads, and the engine lines in a stable order.</summary>
    private static EngineSnapshot Snapshot(ModOverlay overlay)
    {
        var result = EngineDataLoader.Load(overlay.CreateFileSystem());
        var incomplete = result.Problems.FirstOrDefault(p => p.Source == EngineDataLoader.LoadSource)?.Message;
        string dump;
        try
        {
            dump = result.Db == null ? "No data." : DataDump.Create(result.Db);
        }
        catch (Exception e)
        {
            dump = $"The dump fails: {e.GetType().Name}: {e.Message}";
            incomplete ??= dump;
        }
        return new EngineSnapshot(dump, string.Join("\n", result.Problems.Select(p => p.ToString()).Order(StringComparer.Ordinal)),
            incomplete);
    }

    /// <param name="Incomplete">Null when the engine loaded all data and the dump has it. Otherwise the reason.</param>
    private sealed record EngineSnapshot(string Dump, string Lines, string? Incomplete);

    private static string FirstDifference(string a, string b, string labelA, string labelB)
    {
        var linesA = a.Split('\n');
        var linesB = b.Split('\n');
        var i = 0;
        while (i < linesA.Length && i < linesB.Length && linesA[i] == linesB[i])
            i++;
        return $"Line {i + 1}: '{linesA.ElementAtOrDefault(i)}' {labelA}, '{linesB.ElementAtOrDefault(i)}' {labelB}.";
    }

    /// <summary>
    /// The retail files that the edits change: ships and loadouts, equipment, goods and markets, factions, bases and
    /// star systems. The engine loads all of them, and the dump shows most of them.
    /// </summary>
    private static List<string> EditablePaths(string baseFolder)
    {
        string[] folders = [@"DATA\SHIPS", @"DATA\EQUIPMENT", @"DATA\UNIVERSE\SYSTEMS"];
        return folders.SelectMany(f => Directory.EnumerateFiles(Path.Combine(baseFolder, f), "*.ini",
                f.EndsWith("SYSTEMS") ? new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 } : new EnumerationOptions()))
            .Append(Path.Combine(baseFolder, @"DATA\INITIALWORLD.INI"))
            .Append(Path.Combine(baseFolder, @"DATA\MISSIONS\MBASES.INI"))
            .Select(f => VfsPath.Normalize(Path.GetRelativePath(baseFolder, f))!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CreateMod(string root, string id)
    {
        var folder = Path.Combine(root, id);
        WriteFile(Path.Combine(folder, ModManifest.FileName), Encoding.ASCII.GetBytes($$"""{ "id": "{{id}}", "formatVersion": 1 }"""));
        return folder;
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private enum EditKind
    {
        ChangeValue,
        RemoveName,
        RepeatLine,
        AddName,
        ChangeGroupLine,
        DeleteSection,
        AddSection,
        AddKeyedChild,
        RenameNew,
        SetBack,
        DeleteThenAddSameKey,
        RenameBase,
        DeleteCascade,
    }

    /// <summary>The results of the whole test.</summary>
    private sealed class Run
    {
        public readonly SortedDictionary<string, int> Edits = new(StringComparer.Ordinal);
        public readonly List<string> Failures = [];
        public int SingleNameChecks;
        public int Reverted;
        public int Rounds;
        public int CompleteLoads;
        public int LateEngineLines;
        public readonly Stopwatch WriteTime = new();
        public readonly List<string> IncompleteLoads = [];
        public int FilesCompared;
        public int SecondSessions;
        public int Utf8Sessions;
        public int SecondSessionEdits;
        public int SecondSessionAdds;
        public int LowerLayerSessions;
        public int OtherEngineData;
        private int nextKey;

        /// <summary>A key that no retail section has.</summary>
        public string FreshKey() => $"proof_key_{++nextKey}";
    }

    /// <summary>Makes random edits to one file, and checks the patch after each edit.</summary>
    private sealed class FileEditor
    {
        /// <summary>The kinds to pick from. Few retail sections fit a new keyed child, so that kind gets two chances, and
        /// each kind still occurs at least <see cref="MinEditsPerKind"/> times.</summary>
        private static readonly EditKind[] Kinds = [.. Enum.GetValues<EditKind>(), EditKind.AddKeyedChild];

        private readonly IniDocument b;
        private readonly IReadOnlySet<string> requiredTargets;
        private readonly Random random;
        private readonly Run run;
        private readonly Dictionary<SectionIdentity, int> baseIndex;

        /// <summary>The sections of B that a patch can find.</summary>
        private readonly HashSet<SectionIdentity> patchable;

        /// <summary>The sections of the last patch.</summary>
        private HashSet<SectionIdentity> written = [];

        /// <summary>The sections of B that the last patch deletes and adds again.</summary>
        private HashSet<SectionIdentity> addedAgain = [];

        /// <summary>The values of all lines of B, except the nickname line of each section.</summary>
        private readonly HashSet<string> namedKeys;

        /// <param name="requiredTargets">The keys that a required reference names (see
        /// <see cref="RequiredReferences"/>). These sections stay.</param>
        public FileEditor(string path, IniDocument b, IReadOnlySet<string> requiredTargets, Random random, Run run)
        {
            this.b = b;
            this.requiredTargets = requiredTargets;
            this.random = random;
            this.run = run;
            Document = EditDocument.Open(path, b, null, []);
            var ids = SectionIdentity.Identify(b.Sections);
            baseIndex = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
            var keys = ProofSections.KeyCounts(b.Sections);
            var types = b.Sections.CountBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
            patchable = ids.Where((_, i) => SectionKeys.For(b.Sections[i].Name).Kind switch
            {
                SectionMatchKind.Keyed => ProofSections.KeyOf(b.Sections[i]) is { } key && keys[(b.Sections[i].Name, key)] == 1,
                SectionMatchKind.Singleton => types[b.Sections[i].Name] == 1,
                _ => false,
            }).ToHashSet();
            namedKeys = b.Sections
                .SelectMany(s => s.Where(e => !(IsNickname(s) && IsKey(s, e))))
                .SelectMany(e => e.Select(v => v.ToString()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public EditDocument Document { get; }

        /// <summary>True after a failure. The editor then stops.</summary>
        public bool Broken { get; private set; }

        private IReadOnlyList<Section> W => Document.Working;

        private PatchText Write()
        {
            run.WriteTime.Start();
            var patch = Document.Write();
            run.WriteTime.Stop();
            return patch;
        }

        /// <summary>The bytes of the edited file in the format of B.</summary>
        public byte[] WholeFile() => b.WithSections(W).ToBytes();

        /// <summary>Makes one random edit and checks the patch. Returns false when no edit fits the file.</summary>
        public bool EditOnce()
        {
            if (Broken)
                return false;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var kind = Kinds[random.Next(Kinds.Length)];
                var edit = kind switch
                {
                    EditKind.ChangeValue => ChangeValue(groupOnly: false),
                    EditKind.RemoveName => RemoveName(),
                    EditKind.RepeatLine => RepeatLine(),
                    EditKind.AddName => AddName(),
                    EditKind.ChangeGroupLine => ChangeValue(groupOnly: true),
                    EditKind.DeleteSection => DeleteSection(),
                    EditKind.AddSection => AddSection(),
                    EditKind.AddKeyedChild => AddKeyedChild(),
                    EditKind.RenameNew => RenameNew(),
                    EditKind.SetBack => SetBack(),
                    EditKind.RenameBase => RenameBase(),
                    EditKind.DeleteCascade => DeleteCascade(),
                    _ => DeleteThenAddSameKey(),
                };
                if (edit == null)
                    continue;
                run.Edits[kind.ToString()] = run.Edits.GetValueOrDefault(kind.ToString()) + 1;
                Check(kind, edit);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gives each section of B that W has the lines of B, unit by unit. Removes the new sections, and the sections
        /// of B that the patch deletes and adds again.
        /// </summary>
        public void RevertAll()
        {
            if (Broken)
                return;
            run.Reverted++;
            for (var j = W.Count - 1; j >= 0; j--)
            {
                var id = Document.IdentityAt(j);
                if (!baseIndex.TryGetValue(id, out var i) || addedAgain.Contains(id))
                {
                    // The children of a section that is added again follow it with no directive, and go with it.
                    Document.Remove(j, SectionKeys.IsChild(W[j].Name) ? 1 : 1 + ProofSections.ChildCount(W, j));
                    continue;
                }
                foreach (var unit in ChangedUnits(W[j], b.Sections[i]).ToList())
                    SetUnit(j, unit, b.Sections[i].Where(e => unit.Contains(e.Name)).ToList());
            }
            var patch = Write();
            if (!patch.Succeeded || patch.Sections.Any(s => !s.Lines.Contains("ol.delete = true")))
            {
                run.Failures.Add($"{Document.Path}: after a revert, the patch is not only deletes: " +
                                 string.Join("; ", patch.Problems) + patch.Text);
                Broken = true;
            }
        }

        private void Check(EditKind kind, Edit edit)
        {
            var patch = Write();
            if (!patch.Succeeded)
            {
                run.Failures.Add($"{Document.Path}: {kind} of {edit.Section}: {string.Join("; ", patch.Problems.Select(p => p.Message))}");
                Broken = true;
                return;
            }
            if (edit.Unit != null)
            {
                run.SingleNameChecks++;
                var section = patch.Sections.SingleOrDefault(s => s.Id.Equals(edit.Section));
                var names = section == null ? null : WrittenNames(section);
                if (names == null || names.Count == 0 || !names.All(edit.Unit.Contains))
                    run.Failures.Add($"{Document.Path}: {kind} of {edit.Section} in {string.Join(", ", edit.Unit)} writes " +
                                     (names == null ? "no section." : string.Join(", ", names)));
            }
            written = patch.Sections.Select(s => s.Id).ToHashSet();
            addedAgain = patch.Sections.Where(s => baseIndex.ContainsKey(s.Id) && s.Lines.Contains("ol.new = true"))
                .Select(s => s.Id).ToHashSet();
        }

        /// <summary>
        /// Changes one number of one line. With <paramref name="groupOnly"/>, the line belongs to a linked group.
        /// </summary>
        private Edit? ChangeValue(bool groupOnly)
        {
            var j = PickEditable(s => s.Any(e => !IsKey(s, e) && (!groupOnly || InGroup(s, e)) && e.Any(IsNumber)));
            if (j == null)
                return null;
            var section = W[j.Value];
            var lines = section.Where(e => !IsKey(section, e) && (!groupOnly || InGroup(section, e)) && e.Any(IsNumber)).ToList();
            var line = lines[random.Next(lines.Count)];
            var positions = Enumerable.Range(0, line.Count).Where(i => IsNumber(line[i])).ToList();
            var position = positions[random.Next(positions.Count)];
            var changed = new Entry(line.Section, line.Name, line.Count);
            for (var i = 0; i < line.Count; i++)
                changed.Add(i == position ? Changed(line[i]) : line[i]);
            var unit = UnitOf(section, line.Name);
            return Set(j.Value, unit, section.Where(e => unit.Contains(e.Name)).Select(e => e == line ? changed : e).ToList());
        }

        private Edit? RemoveName()
        {
            var j = PickEditable(s => s.Any(e => !IsKey(s, e) && !IsRequired(e)));
            if (j == null)
                return null;
            var names = W[j.Value].Where(e => !IsKey(W[j.Value], e) && !IsRequired(e)).Select(e => e.Name).ToList();
            var name = names[random.Next(names.Count)];
            // The writer also writes the rest of a linked group.
            var group = UnitOf(W[j.Value], name);
            var edit = Set(j.Value, Unit([name]), []);
            return edit with { Unit = edit.Unit == null ? null : group };
        }

        /// <summary>Repeats a line at the end of its unit, as a copy makes it.</summary>
        private Edit? RepeatLine()
        {
            var j = PickEditable(s => s.Any(e => !IsKey(s, e)));
            if (j == null)
                return null;
            var section = W[j.Value];
            var lines = section.Where(e => !IsKey(section, e)).ToList();
            var line = lines[random.Next(lines.Count)];
            var unit = UnitOf(section, line.Name);
            var copy = Document.IsBinary ? ProofSections.Lowercase(line) : line;
            return Set(j.Value, unit, section.Where(e => unit.Contains(e.Name)).Append(copy).ToList());
        }

        private Edit? AddName()
        {
            var j = PickEditable(_ => true);
            if (j == null)
                return null;
            var name = run.FreshKey();
            var line = new Entry(W[j.Value], name);
            for (var count = random.Next(1, 4); count > 0; count--)
            {
                line.Add(random.Next(3) switch
                {
                    0 => new Int32Value(random.Next(-100_000, 100_000)),
                    1 => new SingleValue(random.Next(-1000, 1000) + 0.5f, null),
                    _ => new StringValue(name),
                });
            }
            return Set(j.Value, Unit([name]), [line]);
        }

        /// <summary>Deletes a section, with its child sections.</summary>
        private Edit? DeleteSection()
        {
            var j = PickEditable(s => ProofSections.KeyOf(s) is not { } key || !requiredTargets.Contains(key));
            if (j == null)
                return null;
            var id = Document.IdentityAt(j.Value);
            var count = SectionKeys.IsChild(W[j.Value].Name) ? 1 : 1 + ProofSections.ChildCount(W, j.Value);
            Document.Remove(j.Value, count);
            return new Edit(id, null);
        }

        /// <summary>Adds a copy of a section with a key, and of its child sections, with new keys.</summary>
        private Edit? AddSection()
        {
            var j = PickEditable(s => !SectionKeys.IsChild(s.Name) && IsNickname(s) && ProofSections.KeyOf(s) != null);
            if (j == null)
                return null;
            var sections = W.Skip(j.Value).Take(1 + ProofSections.ChildCount(W, j.Value)).ToList();
            var copies = sections.Select(s => ProofSections.KeyOf(s) == null ? Copy(s) : Copy(s, run.FreshKey())).ToList();
            foreach (var copy in copies)
                Document.Insert(W.Count, copy);
            return new Edit(Document.IdentityAt(W.Count - copies.Count), null);
        }

        /// <summary>Adds a copy of a child section with a key, with a new key, after the children of its parent.</summary>
        private Edit? AddKeyedChild()
        {
            var j = PickEditable(s => SectionKeys.IsChild(s.Name) && IsNickname(s) && ProofSections.KeyOf(s) != null);
            if (j == null)
                return null;
            var parent = ProofSections.Parent(W, j.Value);
            // ol.parent finds the parent by its key among the sections of all types.
            var parentKey = parent < 0 ? null : ProofSections.ParentKey(W[parent]);
            if (parentKey == null || W.Count(s => !SectionKeys.IsChild(s.Name) &&
                                                   string.Equals(ProofSections.ParentKey(s), parentKey, StringComparison.OrdinalIgnoreCase)) != 1)
                return null;
            var at = parent + 1 + ProofSections.ChildCount(W, parent);
            Document.Insert(at, Copy(W[j.Value], run.FreshKey()));
            return new Edit(Document.IdentityAt(at), null);
        }

        /// <summary>Gives a new section another key.</summary>
        private Edit? RenameNew()
        {
            // A section of B under a new key (RenameBase) is not a new section.
            var candidates = Enumerable.Range(0, W.Count)
                .Where(j => Document.BaseIdOf(Document.IdentityAt(j)) == null && !SectionKeys.IsChild(W[j].Name) && IsNickname(W[j]))
                .ToList();
            if (candidates.Count == 0)
                return null;
            var index = candidates[random.Next(candidates.Count)];
            var renamed = Copy(W[index], run.FreshKey());
            Document.Replace(index, renamed, [SectionKeys.DefaultKey]);
            return new Edit(Document.IdentityAt(index), null);
        }

        /// <summary>
        /// Gives a section of B a new key, as a rename in the Scene does (milestone M3): the patch writes
        /// <c>ol.rename</c>, and the section keeps its place. The edit skips a section with child sections, a child
        /// section, a required target and a key that another line names, as <see cref="DeleteThenAddSameKey"/> does.
        /// </summary>
        private Edit? RenameBase()
        {
            var j = PickEditable(s => !SectionKeys.IsChild(s.Name) && IsNickname(s) && ProofSections.KeyOf(s) is { } key &&
                                      !requiredTargets.Contains(key) && !namedKeys.Contains(key));
            if (j == null || !baseIndex.ContainsKey(Document.IdentityAt(j.Value)) || ProofSections.ChildCount(W, j.Value) > 0)
                return null;
            // The rename command changes only the key line. The other lines keep their values, as the section of B has
            // them, so a capital bool string stays as it is.
            var old = W[j.Value];
            var renamed = new Section(old.Name);
            var key = run.FreshKey();
            foreach (var entry in old)
                renamed.Add(IsKey(old, entry) ? new Entry(renamed, entry.Name) { new StringValue(key) } : entry);
            Document.Replace(j.Value, renamed, [SectionKeys.DefaultKey]);
            return new Edit(Document.IdentityAt(j.Value), null);
        }

        /// <summary>
        /// Deletes an object of a system file with its <c>parent</c> children, again and again, from the last section to
        /// the first, as a delete in the Scene does (decision D81 of the M3 design).
        /// </summary>
        private Edit? DeleteCascade()
        {
            var parents = Enumerable.Range(0, W.Count)
                .Where(j => W[j].Name.Equals("Object", StringComparison.OrdinalIgnoreCase) &&
                            ProofSections.KeyOf(W[j]) is { } key && !requiredTargets.Contains(key) &&
                            W.Any(s => s.Name.Equals("Object", StringComparison.OrdinalIgnoreCase) && Parent(s) is { } p &&
                                       p.Equals(key, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (parents.Count == 0)
                return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProofSections.KeyOf(W[parents[random.Next(parents.Count)]])! };
            for (var grown = true; grown;)
            {
                grown = false;
                foreach (var section in W.Where(s => s.Name.Equals("Object", StringComparison.OrdinalIgnoreCase)))
                {
                    if (Parent(section) is { } p && set.Contains(p) && ProofSections.KeyOf(section) is { } child &&
                        !requiredTargets.Contains(child) && set.Add(child))
                        grown = true;
                }
            }
            var indices = Enumerable.Range(0, W.Count)
                .Where(j => W[j].Name.Equals("Object", StringComparison.OrdinalIgnoreCase) &&
                            ProofSections.KeyOf(W[j]) is { } key && set.Contains(key))
                .ToList();
            if (indices.Any(j => !patchable.Contains(Document.IdentityAt(j)) && baseIndex.ContainsKey(Document.IdentityAt(j))))
                return null;
            var first = Document.IdentityAt(indices[0]);
            foreach (var j in indices.OrderByDescending(j => j))
                Document.Remove(j, 1 + ProofSections.ChildCount(W, j));
            return new Edit(first, null);

            static string? Parent(Section section) =>
                section.LastOrDefault(e => e.Name.Equals("parent", StringComparison.OrdinalIgnoreCase) && e.Count > 0)?[0].ToString();
        }

        /// <summary>
        /// Deletes a section of B with its child sections, and adds a copy of them at the end with the same keys, as
        /// when a modder deletes an item and makes it again from a template.
        /// </summary>
        /// <remarks>
        /// The engine reads some sections in file order. For example, a ship package needs its hull good before it, and
        /// an object needs its parent object before it. So the edit skips a section whose key another line of the file
        /// names, and a required target.
        /// </remarks>
        private Edit? DeleteThenAddSameKey()
        {
            var j = PickEditable(s => !SectionKeys.IsChild(s.Name) && IsNickname(s) && ProofSections.KeyOf(s) is { } key &&
                                      !requiredTargets.Contains(key) && !namedKeys.Contains(key));
            if (j == null || !baseIndex.ContainsKey(Document.IdentityAt(j.Value)))
                return null;
            var id = Document.IdentityAt(j.Value);
            var copies = W.Skip(j.Value).Take(1 + ProofSections.ChildCount(W, j.Value)).Select(s => Copy(s)).ToList();
            Document.Remove(j.Value, copies.Count);
            foreach (var copy in copies)
                Document.Insert(W.Count, copy);
            return new Edit(id, null);
        }

        /// <summary>
        /// Sets one changed unit of a section of B back to the lines of B. A section that the patch deletes and adds
        /// again is a new section, and a new line of a binary file cannot hold a bool string such as <c>True</c>. So
        /// such a section does not get the lines of B.
        /// </summary>
        private Edit? SetBack()
        {
            var changed = written.Where(id => baseIndex.ContainsKey(id) && Document.IndexOf(id) >= 0 && !addedAgain.Contains(id)).ToList();
            if (changed.Count == 0)
                return null;
            var id = changed[random.Next(changed.Count)];
            var j = Document.IndexOf(id);
            var original = b.Sections[baseIndex[id]];
            var units = ChangedUnits(W[j], original).ToList();
            if (units.Count == 0)
                return null;
            var unit = units[random.Next(units.Count)];
            SetUnit(j, unit, original.Where(e => unit.Contains(e.Name)).ToList());
            return new Edit(id, null);
        }

        /// <summary>
        /// Replaces the lines of a unit, at the position of its first line or at the end, as the merge does. For an
        /// unchanged section of B, the edit asks for the single-name check.
        /// </summary>
        private Edit Set(int j, HashSet<string> unit, IReadOnlyList<Entry> lines)
        {
            var id = Document.IdentityAt(j);
            var single = baseIndex.ContainsKey(id) && !written.Contains(id) && !SectionKeys.IsOrderSensitive(W[j].Name);
            SetUnit(j, unit, lines);
            return new Edit(id, single ? unit : null);
        }

        private void SetUnit(int j, HashSet<string> unit, IReadOnlyList<Entry> lines)
        {
            var old = W[j];
            var section = new Section(old.Name);
            var placed = false;
            foreach (var entry in old)
            {
                if (!unit.Contains(entry.Name))
                {
                    section.Add(entry);
                    continue;
                }
                if (!placed)
                {
                    foreach (var line in lines)
                        section.Add(line);
                    placed = true;
                }
            }
            if (!placed)
            {
                foreach (var line in lines)
                    section.Add(line);
            }
            Document.Replace(j, section, unit);
        }

        /// <summary>A random section that a patch can change: a section of B that a patch can find, or a new one.</summary>
        private int? PickEditable(Func<Section, bool> fits)
        {
            for (var attempt = 0; attempt < 20 && W.Count > 0; attempt++)
            {
                var j = random.Next(W.Count);
                var id = Document.IdentityAt(j);
                if ((patchable.Contains(id) || !baseIndex.ContainsKey(id)) && fits(W[j]))
                    return j;
            }
            return null;
        }

        /// <summary>A copy as the editor makes it for a new section, with a new key when <paramref name="key"/> is set.</summary>
        private Section Copy(Section section, string? key = null)
        {
            var copy = ProofSections.Copy(section, Document.IsBinary);
            if (key == null)
                return copy;
            var renamed = new Section(copy.Name);
            foreach (var entry in copy)
                renamed.Add(IsKey(copy, entry) ? new Entry(renamed, entry.Name) { new StringValue(key) } : entry);
            return renamed;
        }

        /// <summary>Another number of the same type.</summary>
        private ValueBase Changed(ValueBase value)
        {
            var step = random.Next(1, 10);
            if (value is Int32Value integer)
                return new Int32Value(unchecked((int)integer + step));
            var single = (float)(SingleValue)value;
            var changed = single + 0.25f * step;
            return new SingleValue(changed != single ? changed : single / 2, null);
        }

        private static bool IsNumber(ValueBase value) => value is Int32Value or SingleValue;

        private static bool IsRequired(Entry entry) => RequiredEntries.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);

        private static bool IsNickname(Section section) =>
            SectionKeys.For(section.Name) is { Kind: SectionMatchKind.Keyed, KeyEntry: SectionKeys.DefaultKey };

        private static bool IsKey(Section section, Entry entry) =>
            SectionKeys.For(section.Name) is { Kind: SectionMatchKind.Keyed } match &&
            entry.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase);

        private static bool InGroup(Section section, Entry entry) =>
            SectionKeys.LinkedGroups(section.Name).Any(g => g.Contains(entry.Name, StringComparer.OrdinalIgnoreCase));

        private static HashSet<string> Unit(IEnumerable<string> names) => new(names, StringComparer.OrdinalIgnoreCase);

        /// <summary>The names that the editor changes together with a name: its linked group, or the name.</summary>
        private static HashSet<string> UnitOf(Section section, string name) =>
            Unit(SectionKeys.LinkedGroups(section.Name).FirstOrDefault(g => g.Contains(name, StringComparer.OrdinalIgnoreCase)) ?? [name]);

        /// <summary>The units of a section whose lines differ from the original section, without the key.</summary>
        private IEnumerable<HashSet<string>> ChangedUnits(Section edited, Section original) =>
            edited.Concat(original)
                .Where(e => !IsKey(original, e))
                .Select(e => UnitOf(original, e.Name))
                .DistinctBy(u => string.Join(",", u.Order(StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase)
                .Where(u => !SameLines(edited.Where(e => u.Contains(e.Name)).ToList(), original.Where(e => u.Contains(e.Name)).ToList()));

        private bool SameLines(List<Entry> first, List<Entry> second) =>
            first.Count == second.Count &&
            first.Zip(second).All(p => p.First.Name == p.Second.Name && p.First.Count == p.Second.Count &&
                                       p.First.Zip(p.Second).All(v => IniValueComparer.EngineEqual(v.First, v.Second, Document.IsBinary)));

        /// <summary>The entry names of a patch section without its key, including the names of <c>ol.remove</c>.</summary>
        private static List<string> WrittenNames(PatchSection section)
        {
            var names = new List<string>();
            foreach (var line in section.Lines.Skip(1))
            {
                var name = line[..line.IndexOf(" =", StringComparison.Ordinal)];
                if (name == "ol.remove")
                    names.AddRange(line[(line.IndexOf('=') + 1)..].Split(',').Select(n => n.Trim()));
                else if (!name.StartsWith("ol.", StringComparison.Ordinal) && name != SectionKeys.For(section.Id.Type).KeyEntry)
                    names.Add(name);
            }
            return names;
        }
    }

    /// <param name="Section">The section that the edit changes.</param>
    /// <param name="Unit">For the single-name check: the names that the patch section can hold.</param>
    private sealed record Edit(SectionIdentity Section, HashSet<string>? Unit);
}

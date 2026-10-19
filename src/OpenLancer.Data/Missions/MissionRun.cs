using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The snapshot of one run of the mission checks (section 12.3 of the M6a design). The UI thread makes it. The worker
/// reads only the snapshot: it does not call the edit session, the data lists or the Console.
/// </summary>
public sealed class MissionInputs
{
    /// <summary>The file system of the edit view.</summary>
    public required FileSystem EditView { get; init; }

    /// <summary>The file system of the base folder without mods (B), or null. The base-game runs of the scripts that a
    /// mod changes read it (decision D551).</summary>
    public FileSystem? BaseView { get; init; }

    /// <summary>The sections of <c>freelancer.ini</c> of the edit view.</summary>
    public required IReadOnlyList<Section> FreelancerIni { get; init; }

    /// <summary>The names of the game data outside the scripts, and the files.</summary>
    public MissionWorld World { get; init; } = MissionWorld.Empty;

    /// <summary>Which mod changes which script and section.</summary>
    public IMissionLedger Ledger { get; init; } = NoMods.Instance;

    public IEngineCoverage Coverage { get; init; } = FullCoverage.Instance;

    public MissionSchema Schema { get; init; } = MissionSchema.Retail;

    /// <summary>The active mod, or null.</summary>
    public string? ActiveModId { get; init; }

    /// <summary>The check options of the active mod.</summary>
    public MissionCheckOptions Options { get; init; } = new();

    /// <summary>The number of the run. A run that a newer run replaced drops its result.</summary>
    public int Generation { get; init; }

    /// <summary>The install path of a path relative to the data folder, or null.</summary>
    public string? DataPath(string relative) => DataPaths.Resolve(FreelancerIni, relative);
}

/// <summary>The analysis of one mission script of a run.</summary>
/// <param name="Bytes">The bytes of the script in the edit view.</param>
/// <param name="Problems">The problems with their origins and severities.</param>
/// <param name="BaseIndex">The index of the base-game version of a script that a mod changes, or null.</param>
public sealed record MissionAnalysis(MissionScriptInfo Script, byte[] Bytes, MissionDocument Document, MissionIndex Index,
    IReadOnlyList<Diagnostic> Problems, MissionIndex? BaseIndex)
{
    /// <summary>The behavior prefab instances that the matcher finds (section 8 of the M6a design).</summary>
    public MissionPrefabResult? Prefabs { get; init; }

    /// <summary>The acts and the frame "Not started by the mission" (section 6.4 of the M6a design).</summary>
    public MissionActs? Acts { get; init; }

    /// <summary>The prefab instances of the base-game version of a changed script, or null.</summary>
    public MissionPrefabResult? BasePrefabs { get; init; }

    /// <summary>The install path of the mission ship file that <c>[Mission] npc_ship_file</c> names, or null.</summary>
    public string? ShipFile { get; init; }

    /// <summary>The number of <c>[NPCShipArch]</c> sections in the mission ship file.</summary>
    public int ShipBuilds { get; init; }

    /// <summary>The raw text of the script (decision D529): the file text with its comments for a text script that no
    /// mod changes, else the canonical text.</summary>
    public RawText RawText() => Editing.Fields.RawText.Of(Script.Path, Document.Sections.Select(s => s.Section).ToList(),
        Document.IsBinary, Bytes, fileText: Script.State == MissionScriptState.BaseGame);
}

/// <summary>A news item or a rumor with the story stages of its range (section 5.2 of the M6a design).</summary>
/// <param name="From">The index of the first stage.</param>
/// <param name="To">The index of the last stage.</param>
public readonly record struct StoryItem(int From, int To, bool Rumor);

/// <summary>The result of one run of the mission checks.</summary>
/// <param name="Scripts">The scripts of the browser: the slot scripts in slot order, then the other scripts.</param>
/// <param name="Analyses">The analysis of each script that exists, by install path.</param>
/// <param name="ShipFiles">The files of the NPC ship builds that the scripts can name.</param>
public sealed record MissionRunResult(int Generation, IReadOnlyList<MissionScriptInfo> Scripts,
    IReadOnlyDictionary<string, MissionAnalysis> Analyses, IReadOnlyList<string> ShipFiles, TimeSpan Elapsed)
{
    /// <summary>The news items and rumors with a stage range.</summary>
    public IReadOnlyList<StoryItem> StoryItems { get; init; } = [];

    /// <summary>The problems of all scripts.</summary>
    public IEnumerable<Diagnostic> Problems => Scripts.Select(s => Analyses.GetValueOrDefault(s.Path))
        .OfType<MissionAnalysis>().SelectMany(a => a.Problems);

    /// <summary>The analysis of a script, or null.</summary>
    public MissionAnalysis? Of(string path) => VfsPath.Normalize(path) is { } normal ? Analyses.GetValueOrDefault(normal) : null;

    /// <summary>True when the path is a mission script of the run (section 12.4 of the M6a design).</summary>
    public bool IsScript(string path) =>
        VfsPath.Normalize(path) is { } normal && Scripts.Any(s => VfsPath.Comparer.Equals(s.Path, normal));
}

/// <summary>
/// The worker run of the mission checks (section 12.3 of the M6a design): it finds the scripts, reads each one, builds
/// its document, its index and its prefab instances, runs the checks, and gives each problem its origin.
/// </summary>
public static class MissionRun
{
    /// <summary>Runs the checks on a snapshot. It reads only the snapshot, so a worker task can call it.</summary>
    public static MissionRunResult Execute(MissionInputs inputs)
    {
        var watch = Stopwatch.StartNew();
        var schema = inputs.Schema;
        var scanned = inputs.DataPath("MISSIONS") is { } missions ? MissionFiles.Scan(inputs.EditView, missions) : [];
        var slotPaths = schema.StorySlots
            .Select(slot => (slot, MissionFiles.SlotPath(inputs.FreelancerIni, slot) is { } path &&
                                   inputs.EditView.FileExists(path) ? path : null))
            .ToList();
        var scripts = MissionFiles.List(slotPaths, scanned, path => StateOf(inputs, path));

        var analyses = new Dictionary<string, MissionAnalysis>(VfsPath.Comparer);
        foreach (var script in scripts.Where(s => s.State != MissionScriptState.Missing))
        {
            if (Analyze(inputs, script) is { } analysis)
                analyses[script.Path] = analysis;
        }
        var shipFiles = NpcShipFiles.Find(inputs.DataPath, folder => inputs.EditView.GetDirectories(folder),
            analyses.Values.Select(a => (a.Script.Path, (IReadOnlyList<Section>?)a.Document.Sections.Select(s => s.Section).ToList())));
        return new MissionRunResult(inputs.Generation, scripts, analyses, shipFiles, watch.Elapsed)
        {
            StoryItems = ReadStoryItems(inputs),
        };
    }

    /// <summary>The news items of <c>news.ini</c> and the rumors of <c>mBases.ini</c> with a stage range. The stage
    /// names come from the story stages of the engine (section 14.6 of the M6a design).</summary>
    private static IReadOnlyList<StoryItem> ReadStoryItems(MissionInputs inputs)
    {
        var stages = StoryStages.Names.Select((name, i) => (name, i))
            .ToDictionary(p => p.name, p => p.i, StringComparer.OrdinalIgnoreCase);
        var result = new List<StoryItem>();
        void Add(Entry entry, bool rumor)
        {
            if (entry.Count >= 2 && stages.TryGetValue(MissionIndex.Text(entry[0]), out var from) &&
                stages.TryGetValue(MissionIndex.Text(entry[1]), out var to))
                result.Add(new StoryItem(from, to, rumor));
        }
        if (inputs.DataPath(@"MISSIONS\news.ini") is { } news && Read(inputs.EditView, news) is var (_, newsDocument))
        {
            foreach (var entry in newsDocument.Sections.SelectMany(s => s.Section).Where(e => MissionIndex.Is(e.Name, "rank")))
                Add(entry, false);
        }
        if (inputs.DataPath(@"MISSIONS\mBases.ini") is { } bases && Read(inputs.EditView, bases) is var (_, baseDocument))
        {
            foreach (var entry in baseDocument.Sections.SelectMany(s => s.Section)
                         .Where(e => MissionIndex.Is(e.Name, "rumor") || MissionIndex.Is(e.Name, "rumor_type2")))
                Add(entry, true);
        }
        return result;
    }

    /// <summary>Reads and checks one script. Null when the file cannot be read.</summary>
    internal static MissionAnalysis? Analyze(MissionInputs inputs, MissionScriptInfo script)
    {
        if (Read(inputs.EditView, script.Path) is not var (bytes, document))
            return null;
        var index = MissionIndex.Build(document, inputs.Schema, inputs.World, inputs.Coverage);
        var problems = MissionChecks.Run(index, inputs.Options);
        MissionIndex? baseIndex = null;
        IReadOnlyList<Diagnostic>? baseProblems = null;
        if (script.State != MissionScriptState.BaseGame && script.InBase && inputs.BaseView is { } baseView &&
            Read(baseView, script.Path) is var (_, baseDocument))
        {
            baseIndex = MissionIndex.Build(baseDocument, inputs.Schema, inputs.World, inputs.Coverage);
            baseProblems = MissionChecks.Run(baseIndex, inputs.Options);
        }
        var classified = MissionOrigins.Apply(problems, baseProblems, script, inputs.Ledger, inputs.ActiveModId);
        var prefabs = MissionPrefabs.Retail.Match(index);
        var shipFile = document.OfKind(MissionSectionKind.Mission).Select(s => ItemIndex.First(s.Section, "npc_ship_file"))
            .OfType<string>().Select(inputs.DataPath).OfType<string>().FirstOrDefault();
        var shipBuilds = shipFile != null && Read(inputs.EditView, shipFile) is var (_, ships)
            ? ships.Sections.Count(s => MissionIndex.Is(s.Type, "NPCShipArch"))
            : 0;
        return new MissionAnalysis(script, bytes, document, index, classified, baseIndex)
        {
            Prefabs = prefabs,
            Acts = MissionActs.Propose(index),
            BasePrefabs = baseIndex == null ? null : MissionPrefabs.Retail.Match(baseIndex),
            ShipFile = shipFile,
            ShipBuilds = shipBuilds,
        };
    }

    /// <summary>The bytes and the document of a script, or null when it cannot be read or parsed.</summary>
    private static (byte[] Bytes, MissionDocument Document)? Read(FileSystem files, string path)
    {
        try
        {
            if (!files.FileExists(path))
                return null;
            var bytes = files.ReadAllBytes(path);
            return (bytes, MissionDocument.Parse(path, bytes));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The state, the mod and the base presence of a script path.</summary>
    private static (MissionScriptState State, string? ModId, bool InBase) StateOf(MissionInputs inputs, string path)
    {
        var inBase = inputs.BaseView?.FileExists(path) ?? true;
        if (!inputs.EditView.FileExists(path))
            return (MissionScriptState.Missing, null, inBase);
        if (inputs.Ledger.ReplacedBy(path) is { } replaced)
            return (MissionScriptState.Replaced, replaced, inBase);
        return inputs.Ledger.ModsThatChangeFile(path) is [.., var last]
            ? (MissionScriptState.Changed, last, inBase)
            : (MissionScriptState.BaseGame, null, inBase);
    }
}

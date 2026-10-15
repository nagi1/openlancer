using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>The folder groups of the Cutscenes tab (section 7.2 of the M8 design).</summary>
internal enum CutsceneFolder
{
    Bases,
    Story,
    GCS,
    Missions,
    Extras,
    Vendors,
    Intro,

    /// <summary>The new cutscenes of the mods, under <c>DATA\MODS</c> (decision D67).</summary>
    Mods,
    Other,
}

/// <summary>What the scan of the Cutscenes tab reads from a script.</summary>
/// <param name="Problem">Why the editor cannot read the script, or null.</param>
internal sealed record CutsceneCounts(float Duration, int Entities, int Events, string? Problem);

/// <summary>One cutscene script of the edit view in the Cutscenes tab.</summary>
internal sealed class CutsceneEntry(string path)
{
    /// <summary>The install path, in normal form.</summary>
    public string Path { get; } = path;

    /// <summary>The file name without its extension.</summary>
    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(path);

    public CutsceneFolder Folder { get; } = CutsceneCatalog.FolderOf(path);

    /// <summary>The counts, or null until the scan reads the script.</summary>
    public CutsceneCounts? Counts { get; set; }
}

/// <summary>
/// The cutscene scripts of the edit view, and their counts. A scan evaluates each script once, a part in each frame,
/// so the editor stays fast while it reads all 1,506 retail scripts (about 1.2 s of evaluation). The scan runs on the UI
/// thread, because it reads the files of the game data, which the editor uses on that thread.
/// </summary>
internal sealed class CutsceneCatalog
{
    private readonly List<CutsceneEntry> entries;
    private int next;

    /// <param name="paths">The install paths of the scripts.</param>
    public CutsceneCatalog(IEnumerable<string> paths) =>
        entries = paths.Select(p => new CutsceneEntry(VfsPath.Normalize(p) ?? p)).ToList();

    /// <summary>The scripts, in the order of the paths.</summary>
    public IReadOnlyList<CutsceneEntry> Entries => entries;

    /// <summary>The number of scripts that the scan read.</summary>
    public int Scanned => next;

    /// <summary>True when the scan read all scripts.</summary>
    public bool Done => next >= entries.Count;

    /// <summary>The folder group of an install path: the folder below <c>DATA\SCRIPTS</c>, the missions, or the new
    /// cutscenes of the mods.</summary>
    public static CutsceneFolder FolderOf(string path)
    {
        var parts = (VfsPath.Normalize(path) ?? path).Split('\\');
        if (parts.Length < 3 || !parts[0].Equals("DATA", StringComparison.OrdinalIgnoreCase))
        {
            return CutsceneFolder.Other;
        }

        var folder = parts[1].ToUpperInvariant();
        if (folder is "MISSIONS" or "RANDOMMISSIONS")
        {
            return CutsceneFolder.Missions;
        }

        if (folder == "MODS")
        {
            return CutsceneFolder.Mods;
        }

        return folder == "SCRIPTS" && parts.Length > 3 && Enum.TryParse<CutsceneFolder>(parts[2], true, out var group) &&
               group is not (CutsceneFolder.Mods or CutsceneFolder.Other or CutsceneFolder.Missions)
            ? group
            : CutsceneFolder.Other;
    }

    /// <summary>
    /// Reads scripts until the time budget ends, at least one in each call. A script that does not read gets its
    /// problem.
    /// </summary>
    /// <param name="read">Gives the bytes of an install path.</param>
    /// <returns>True when the call read a script.</returns>
    public bool Scan(Func<string, byte[]> read, IThnEvaluator evaluator, TimeSpan budget)
    {
        var start = Stopwatch.GetTimestamp();
        var any = false;
        while (next < entries.Count && (!any || Stopwatch.GetElapsedTime(start) < budget))
        {
            var entry = entries[next++];
            entry.Counts = Read(entry.Path, read, evaluator);
            any = true;
        }

        return any;
    }

    /// <summary>
    /// The counts of one script: its duration and the number of its entities and events. A script whose globals the
    /// engine cannot read (ThnScript.cs:122-141) gets a problem.
    /// </summary>
    public static CutsceneCounts Read(string path, Func<string, byte[]> read, IThnEvaluator evaluator)
    {
        ThnTable globals;
        try
        {
            globals = evaluator.Evaluate(read(path), path);
        }
        catch (Exception e) when (e is ThnEvaluationException or System.IO.IOException)
        {
            return new CutsceneCounts(0, 0, 0, e.Message);
        }

        return globals[ThnScriptModel.DurationGlobal] is ThnNumber duration &&
               globals[ThnScriptModel.EntitiesGlobal] is ThnTable entities &&
               globals[ThnScriptModel.EventsGlobal] is ThnTable events
            ? new CutsceneCounts(duration.Value, entities.List.Length, events.List.Length, null)
            : new CutsceneCounts(0, 0, 0,
                "The script needs a number 'duration' and the tables 'entities' and 'events' (ThnScript.cs:122-141).");
    }

    /// <summary>The text of the state of a script for the active mod (section 7.2).</summary>
    public static string StateText(ThnFileSource source) => source.Kind switch
    {
        ThnFileKind.PatchedHere => "Patched here",
        ThnFileKind.WholeFileHere => "Replaced here",
        ThnFileKind.NewHere => "New here",
        _ => source.ChangedByLowerMod ? "Changed by a lower mod" : "Retail",
    };
}

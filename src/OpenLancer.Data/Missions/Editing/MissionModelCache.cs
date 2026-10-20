using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The analysis of each mission script by the state of W (section 7 of the M6b design). One state of a script has one
/// analysis, so an undo finds the analysis of its state again. The base-game run of a script that a mod changes comes
/// from the cache after its first run, because the base folder without mods does not change in a session.
/// </summary>
/// <remarks>The cache belongs to one snapshot of the names of the game data. A new snapshot clears it. The cache keeps
/// the last states of each script only, because each edit makes a new state.</remarks>
internal sealed class MissionModelCache
{
    /// <summary>The number of states of one script that the cache keeps: enough for some undo steps.</summary>
    private const int StatesPerScript = 8;

    private readonly Dictionary<string, List<(object State, MissionAnalysis Analysis)>> models = new(VfsPath.Comparer);
    private readonly Dictionary<string, MissionRun.BaseRun?> baseRuns = new(VfsPath.Comparer);

    /// <summary>The snapshot that the entries belong to, or null.</summary>
    public MissionInputs? Inputs { get; private set; }

    /// <summary>The number of analyses that the cache holds, for the tests.</summary>
    internal int Count => models.Values.Sum(l => l.Count);

    /// <summary>Makes a snapshot the snapshot of the cache. A new snapshot clears the entries.</summary>
    public void Use(MissionInputs inputs)
    {
        if (ReferenceEquals(Inputs, inputs))
            return;
        Inputs = inputs;
        models.Clear();
        baseRuns.Clear();
    }

    /// <summary>The analysis of a state of a script, or null.</summary>
    public MissionAnalysis? Find(string path, object state) =>
        models.TryGetValue(path, out var list) ? list.FirstOrDefault(e => ReferenceEquals(e.State, state)).Analysis : null;

    /// <summary>Keeps the analysis of a state of a script. The oldest state goes when the script has too many.</summary>
    public void Put(string path, object state, MissionAnalysis analysis)
    {
        if (!models.TryGetValue(path, out var list))
            models[path] = list = [];
        list.RemoveAll(e => ReferenceEquals(e.State, state));
        list.Insert(0, (state, analysis));
        if (list.Count > StatesPerScript)
            list.RemoveAt(list.Count - 1);
    }

    /// <summary>The base-game run of a script that a mod changes, from the cache after its first run.</summary>
    public MissionRun.BaseRun? BaseRun(MissionScriptInfo script)
    {
        if (script.State == MissionScriptState.BaseGame || !script.InBase || Inputs == null)
            return null;
        if (!baseRuns.TryGetValue(script.Path, out var run))
            baseRuns[script.Path] = run = MissionRun.ChangedBaseRun(Inputs, script);
        return run;
    }
}

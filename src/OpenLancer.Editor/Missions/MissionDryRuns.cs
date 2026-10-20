using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The dry runs of the Mission tab (section 4 of the M6c design): one dry run for each mission script of the session,
/// the toggle "Dry run", the toggle "Follow", and the fold after each new model of a script. The model comes after each
/// change of the script, also an Undo, a Redo and a restore of unsaved edits (<see cref="MissionEditor.ScriptsChanged"/>).
/// A rename moves the inputs to the new name, and an Undo of the rename moves them back. The class makes no ImGui call,
/// so the tests check it.
/// </summary>
internal sealed class MissionDryRuns(Func<double> clock)
{
    /// <summary>The least time between two folds while a drag or a typed text is open, in seconds (section 4.2 of the
    /// M6c design).</summary>
    public const double FoldInterval = 0.1;

    private readonly Dictionary<string, DryRun> runs = new(VfsPath.Comparer);
    private readonly Dictionary<string, List<(string From, string To)>> renames = new(VfsPath.Comparer);
    private double lastFold = double.NegativeInfinity;
    private (MissionGraph Graph, MissionTrace Trace, int Version, RunOverlay Overlay)? overlay;
    private bool on;

    /// <summary>True while the toggle "Dry run" of the Mission tab is on.</summary>
    public bool On
    {
        get => on;
        set
        {
            if (on == value)
                return;
            on = value;
            Touch();
        }
    }

    /// <summary>True while the toggle "Follow" of the Run panel is on: the graph frames the trigger of each new change.
    /// It is off by default.</summary>
    public bool Follow { get; set; }

    /// <summary>A number that grows with each change of a trace or of the toggle. A view that keeps a model of a run
    /// makes it again when the number changes.</summary>
    public int Version { get; private set; }

    /// <summary>The time of the last change of a trace, in seconds of the clock. The flashes of the graph start
    /// then.</summary>
    public double ChangedAt { get; private set; } = double.NegativeInfinity;

    /// <summary>The time now, in seconds of the clock.</summary>
    public double Now => clock();

    /// <summary>The dry run of a script, folded over the index of an analysis. A script without a dry run gets one
    /// without inputs.</summary>
    public DryRun For(MissionAnalysis analysis)
    {
        var path = analysis.Script.Path;
        if (!runs.TryGetValue(path, out var run))
            runs[path] = run = new DryRun(analysis.Index);
        return run;
    }

    /// <summary>The dry run of a script, or null.</summary>
    public DryRun? Of(string path) => runs.GetValueOrDefault(path);

    /// <summary>Records the trigger renames of a change of the history. The next fold of the script moves the inputs
    /// with them.</summary>
    public void ScriptsChanged(MissionScriptsChange change)
    {
        foreach (var (from, to) in change.Renames)
        {
            // A script without a dry run has no inputs to move.
            if (!VfsPath.Comparer.Equals(from.Script, to.Script) || !runs.ContainsKey(from.Script))
                continue;
            if (!renames.TryGetValue(from.Script, out var list))
                renames[from.Script] = list = [];
            list.Add((from.Name, to.Name));
        }
    }

    /// <summary>
    /// Folds the dry run of a script again when its model changed (section 4.2 of the M6c design). The renames of the
    /// changes since the last fold move the inputs first. While a drag or a typed text is open, the fold runs at most
    /// each 100 ms. Returns true when it folded.
    /// </summary>
    /// <param name="inTransaction">True while a drag or a typed text is open (section 4.11 of the M6b design).</param>
    public bool Sync(MissionAnalysis analysis, bool inTransaction)
    {
        var path = analysis.Script.Path;
        if (!runs.TryGetValue(path, out var run) || ReferenceEquals(run.Index, analysis.Index))
            return false;
        var now = clock();
        if (inTransaction && now - lastFold < FoldInterval)
            return false;
        lastFold = now;
        var moved = renames.Remove(path, out var list) ? list : [];
        run.Rebuild(analysis.Index, moved);
        Touch();
        return true;
    }

    /// <summary>Adds an input to the dry run of a script.</summary>
    public void Add(MissionAnalysis analysis, DryRunInput input)
    {
        For(analysis).Add(input);
        Touch();
    }

    /// <summary>Removes the last input of the dry run of a script ("Back").</summary>
    public void Back(MissionAnalysis analysis)
    {
        if (For(analysis).Back())
            Touch();
    }

    /// <summary>Removes all inputs of the dry run of a script ("Reset").</summary>
    public void Reset(MissionAnalysis analysis)
    {
        For(analysis).Reset();
        Touch();
    }

    /// <summary>Sets the bound of the timer ranges of the dry run of a script ("Timer ranges").</summary>
    public void SetTimers(MissionAnalysis analysis, TimerBound timers)
    {
        For(analysis).SetTimers(timers);
        Touch();
    }

    /// <summary>The run overlay of a graph and a trace. The overlay stays until the graph, the trace or
    /// <see cref="Version"/> changes.</summary>
    public RunOverlay Overlay(MissionGraph graph, MissionTrace trace)
    {
        if (overlay is var (g, t, v, known) && ReferenceEquals(g, graph) && ReferenceEquals(t, trace) && v == Version)
            return known;
        var made = RunOverlay.Of(graph, trace);
        overlay = (graph, trace, Version, made);
        return made;
    }

    /// <summary>Forgets all dry runs, for example when the profile closes.</summary>
    public void Clear()
    {
        runs.Clear();
        renames.Clear();
        overlay = null;
        on = false;
        Follow = false;
        Touch();
    }

    private void Touch()
    {
        Version++;
        ChangedAt = clock();
    }
}

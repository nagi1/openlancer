using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Missions;

/// <summary>One change of the live trace, with its sequence number in its play.</summary>
/// <param name="Sequence">The number of the change in its play, from 1.</param>
/// <param name="Time">The time of the change since the game loaded the mission, in seconds.</param>
internal sealed record LiveChange(long Sequence, double Time, string Trigger, MissionChangeKind Change);

/// <summary>The result of the live trace query of the shared command layer (section 7.2 of the M6c design).</summary>
/// <param name="Play">The number of the play of the changes, or 0 before the first mission event.</param>
/// <param name="Changes">The changes of the play after the sequence number of the request.</param>
/// <param name="Status">The Play status of OpenLancer.</param>
internal sealed record LiveTraceResult(int Play, IReadOnlyList<LiveChange> Changes, PlayStatus Status)
{
    /// <summary>The name of the mission of the game, for example <c>Mission_02</c>, or null.</summary>
    public string? Mission { get; init; }

    /// <summary>The install path of the script of the mission, or null for a mission without a story slot.</summary>
    public string? Script { get; init; }

    /// <summary>The changes that the game dropped in the play because its queue was full.</summary>
    public int Dropped { get; init; }

    /// <summary>True while the game of a play copy runs. The real game has no channel to the editor, so the trace then
    /// gives only the status.</summary>
    public bool GameRuns { get; init; }
}

/// <summary>
/// The live trace of the mission of the OpenLancer game (sections 4.5, 6.3 and 7.2 of the M6c design): the trigger
/// changes of the mission events of Play, as the steps of one trace for the overlay and the Run log. A step holds the
/// changes of one time. The play number grows at each load of a mission in the game, so at each start and each restart.
/// After the exit of the game, the trace stays until the next play or until the modder clears it. The class makes no
/// ImGui call, so the tests check it.
/// </summary>
internal sealed class MissionLive(Func<double> clock)
{
    /// <summary>The time that a trigger waits before the Console gets the stall note, in seconds.</summary>
    public const double StallSeconds = MissionTrace.StallSeconds;

    private readonly List<LiveChange> changes = [];
    private readonly HashSet<string> stalled = new(StringComparer.OrdinalIgnoreCase);
    private double loadedAt;
    private long sequence;

    /// <summary>The number of the play, from 1, or 0 before the first mission event.</summary>
    public int Play { get; private set; }

    /// <summary>The name of the mission of the game, for example <c>Mission_02</c>, or null before the first event and
    /// after <see cref="Clear"/>.</summary>
    public string? Mission { get; private set; }

    /// <summary>The steps of the live trace.</summary>
    public MissionTrace Trace { get; private set; } = new(live: true);

    /// <summary>The changes of the current play since the last <see cref="Clear"/>, in order.</summary>
    public IReadOnlyList<LiveChange> Changes => changes;

    /// <summary>The setup triggers of the test layer of the current play. The Run log shows them as "Play setup:
    /// {trigger}".</summary>
    public IReadOnlySet<string> Setup { get; private set; } = new HashSet<string>();

    /// <summary>The changes that the game dropped in the current play because its queue was full.</summary>
    public int Dropped { get; private set; }

    /// <summary>A number that grows with each change of the trace. A view that keeps a model of the trace makes it
    /// again when the number changes.</summary>
    public int Version { get; private set; }

    /// <summary>The time of the last mission event, in seconds of the clock. The flash of a run starts then.</summary>
    public double ChangedAt { get; private set; } = double.NegativeInfinity;

    /// <summary>The time now in the live trace: the seconds since the game loaded the mission, by the clock of the
    /// editor.</summary>
    public double Now => clock() - loadedAt;

    /// <summary>
    /// Takes a mission event of the game. A reset starts a new play: the trace of the play before it goes. After
    /// <see cref="Clear"/>, the next event shows the mission again, and the sequence numbers of the play go on.
    /// </summary>
    /// <param name="setup">The setup triggers of the test layer of the play that runs. A reset keeps them for the
    /// play.</param>
    public void Take(PlayEvent.MissionChanges e, IReadOnlySet<string>? setup = null)
    {
        if (e.Reset || Play == 0)
        {
            Play++;
            Trace = new MissionTrace(live: true);
            changes.Clear();
            stalled.Clear();
            Dropped = 0;
            sequence = 0;
            loadedAt = clock();
            Setup = setup ?? new HashSet<string>();
        }

        Mission = e.Mission;
        var notes = new List<TraceChange>();
        if (e.Dropped > 0)
        {
            Dropped += e.Dropped;
            notes.Add(TraceChange.Dropped(e.Dropped));
        }

        foreach (var group in e.Changes.GroupBy(c => c.Ms))
        {
            var time = group.Key / 1000.0;
            var rows = new List<TraceChange>(notes);
            notes.Clear();
            foreach (var change in group)
            {
                changes.Add(new LiveChange(++sequence, time, change.Trigger, change.Change));
                rows.Add(new TraceChange(change.Change switch
                {
                    MissionChangeKind.Started => TraceChangeKind.Started,
                    MissionChangeKind.Ran => TraceChangeKind.Ran,
                    _ => TraceChangeKind.Stopped,
                }, change.Trigger));
            }

            Trace.Append(new TraceStep(Trace.Steps.Count + 1, "", rows) { Time = time });
        }

        if (notes.Count > 0)
        {
            Trace.Append(new TraceStep(Trace.Steps.Count + 1, "", notes) { Time = Now });
        }

        Version++;
        ChangedAt = clock();
    }

    /// <summary>Removes the trace ("Clear"). The play number and the sequence numbers stay, so the live trace query
    /// does not give a number twice in one play.</summary>
    public void Clear()
    {
        Mission = null;
        Trace = new MissionTrace(live: true);
        changes.Clear();
        stalled.Clear();
        Dropped = 0;
        Version++;
        ChangedAt = double.NegativeInfinity;
    }

    /// <summary>The triggers that wait for <see cref="StallSeconds"/> now and did not stall before in this play (section
    /// 6.3 of the M6c design). Each trigger comes once in a play.</summary>
    public IReadOnlyList<string> NewStalls()
    {
        var now = Now;
        var result = new List<string>();
        foreach (var (name, state) in Trace.States)
        {
            if (Trace.StallMinutes(state, now) != null && stalled.Add(name))
                result.Add(name);
        }
        return result;
    }

    /// <summary>
    /// The changes of the live trace query (section 7.2 of the M6c design): the changes of the current play after a
    /// sequence number. A request with another play number gets all changes of the current play.
    /// </summary>
    public IReadOnlyList<LiveChange> Since(int play, long since) =>
        play == Play ? changes.Where(c => c.Sequence > since).ToList() : changes.ToList();
}

/// <summary>The Console notes of the live view (sections 6.5 and 8.4 of the M6c design). The trigger changes go to the
/// Run log only.</summary>
internal static class LiveNotes
{
    /// <summary>The end notes of the triggers that ran in a mission event: "{slot} ends with success at trigger
    /// '{trigger}'." or "{slot} ends with fail at trigger '{trigger}'."</summary>
    public static IEnumerable<string> Ends(MissionAnalysis analysis, PlayEvent.MissionChanges changes)
    {
        var slot = SlotOf(analysis);
        foreach (var change in changes.Changes.Where(c => c.Change == MissionChangeKind.Ran))
        {
            if (analysis.Index.Trigger(change.Trigger) is not { } trigger ||
                trigger.ActionsOf("Act_ChangeState").FirstOrDefault()?.Value(0) is not { } state)
                continue;
            if (MissionIndex.Is(state, "SUCCEED"))
                yield return $"{slot} ends with success at trigger '{trigger.Nickname}'.";
            else if (MissionIndex.Is(state, "FAIL"))
                yield return $"{slot} ends with fail at trigger '{trigger.Nickname}'.";
        }
    }

    /// <summary>The stall note of a trigger that waits for 10 minutes. A condition without effect in OpenLancer gets
    /// its own text, because the trigger never runs here.</summary>
    public static string Stall(MissionAnalysis analysis, MissionTrigger trigger) =>
        trigger.Condition?.Mark == EngineMark.NoEffect
            ? $"Trigger '{trigger.Nickname}' of {SlotOf(analysis)} waits for 10 minutes. Its condition has no effect in " +
              "OpenLancer, so it never runs here. The retail game runs it."
            : $"Trigger '{trigger.Nickname}' of {SlotOf(analysis)} waits for 10 minutes. Look in the graph for the event " +
              "that it waits for.";

    /// <summary>The origin of a stall note (decision D551 of the M6a design): the base game when no mod changes the
    /// script, else the last mod that changes it. So the base-game toggle hides the stall notes of unchanged retail
    /// missions.</summary>
    public static MessageOrigin StallOrigin(MissionScriptInfo script) =>
        script is { State: not MissionScriptState.BaseGame, ModId: { } mod }
            ? MessageOrigin.Mod(mod, ModEvidence.File)
            : MessageOrigin.BaseGame;

    /// <summary>The story slot of a script, or its file name.</summary>
    private static string SlotOf(MissionAnalysis analysis) =>
        analysis.Script.Slot?.Slot ?? MissionFiles.Display(analysis.Script.Path);
}

using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Missions;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The UI texts of the dry run (section 8 of the M6c design) pass the STE lint of the M6a tests: the sentence
/// length, no semicolons and no contractions (section 12.2 of the M6c design).</summary>
public sealed class DryRunTextsTests
{
    private static IEnumerable<(string Name, string Text)> Texts()
    {
        yield return ("toggle", MissionView.DryRunTip);
        yield return ("node menu", GraphView.RunThisTip);
        yield return ("empty", RunPanel.EmptyText);
        yield return ("help", RunPanel.Help.WhatItIs);
        yield return ("help writes", RunPanel.Help.WhatItWrites);
        yield return ("page help", MissionPageModel.RunHelp.WhatItIs);
        foreach (var (name, text) in new[]
                 {
                     ("start", RunPanel.StartTip), ("load", RunPanel.LoadTip), ("run", RunPanel.RunTip),
                     ("heartbeat", RunPanel.HeartbeatTip), ("next timer", RunPanel.NextTimerTip), ("back", RunPanel.BackTip),
                     ("reset", RunPanel.ResetTip), ("timers", RunPanel.TimersTip), ("player", RunPanel.PlayerTip),
                     ("system", RunPanel.SystemTip), ("space", RunPanel.SpaceTip), ("waiting", RunPanel.WaitingTip),
                     ("happens", RunPanel.HappensTip), ("is true", RunPanel.IsTrueTip), ("cause", RunPanel.CauseTip),
                     ("follow", RunPanel.FollowTip), ("log", RunPanel.LogTip),
                     ("waits", RunOverlay.WaitsTip), ("ran", RunOverlay.RanTip), ("has run", RunOverlay.HasRunTip),
                     ("stopped", RunOverlay.StoppedTip), ("starts next", RunOverlay.StartsNextTip),
                     ("stall", RunOverlay.StallTip(12)), ("frame", RunOverlay.FrameChip(2).Tooltip),
                     ("chain", TriggerMachine.ChainMessage("ol_trigger")),
                 })
            yield return (name, text);
    }

    [Fact]
    public void EachTextPassesTheSteLint()
    {
        var problems = Texts().SelectMany(t => SteLint.Problems(t.Text).Select(p => $"{t.Name}: {p}")).ToList();

        Assert.Empty(problems);
    }

    [Fact]
    public void TheLogRowsHaveTheTextsOfTheDesign()
    {
        var trace = new MissionTrace();
        trace.Append(new TraceStep(1, "Start mission",
        [
            new TraceChange(TraceChangeKind.Ran, "t_a"), new TraceChange(TraceChangeKind.Started, "t_b"),
            new TraceChange(TraceChangeKind.Stopped, "t_c"), new TraceChange(TraceChangeKind.Ignored, "t_a") { Name = "t_x" },
            new TraceChange(TraceChangeKind.Checkpoint, "t_a") { Name = "t_r" },
            new TraceChange(TraceChangeKind.Note, "") { Text = "No trigger waits for this event." },
        ]));

        Assert.Equal("Step 1: Start mission", trace.Steps[0].Title);
        Assert.Equal(["Ran t_a", "Started t_b", "Stopped t_c", "Ignored: no trigger 't_x'", "Checkpoint: t_r",
            "No trigger waits for this event."], trace.Steps[0].Changes.Select(c => c.LogText));
        Assert.All(trace.Steps[0].Changes.Select(c => c.LogText!), text => Assert.Empty(SteLint.Problems(text)));
    }
}

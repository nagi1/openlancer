using System;
using System.Linq;
using LibreLancer.Data.Schema.Missions;
using OpenLancer.Data.Missions;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The marks of the engine for the mission names (section 14.8 of the M6a design, decision D526). The pins change only
/// with a change of the mission runtime of the engine fork.
/// </summary>
public sealed class EngineCoverageTests
{
    private static int Count(Type names, EngineMark mark) =>
        Enum.GetNames(names).Count(n => EngineCoverage.Instance.Of(n) == mark);

    /// <summary>The counts of runs, no effect and cannot load. Since OL-0061, the fork skips the unsupported names, so
    /// no name stops the load of a mission. Before, 8 actions and 2 conditions could not load.</summary>
    [Fact]
    public void ThePinsOfTheActionsAndTheConditions()
    {
        Assert.Equal((49, 36, 0), (Count(typeof(TriggerActions), EngineMark.Runs),
            Count(typeof(TriggerActions), EngineMark.NoEffect), Count(typeof(TriggerActions), EngineMark.CannotLoad)));
        Assert.Equal((33, 11, 0), (Count(typeof(TriggerConditions), EngineMark.Runs),
            Count(typeof(TriggerConditions), EngineMark.NoEffect), Count(typeof(TriggerConditions), EngineMark.CannotLoad)));
    }

    [Fact]
    public void EachEnumNameHasAMarkAndAnUnknownNameRuns()
    {
        Assert.Equal(Enum.GetNames<TriggerActions>().Length + Enum.GetNames<TriggerConditions>().Length,
            EngineCoverage.Instance.All.Count);
        Assert.Equal(EngineMark.Runs, EngineCoverage.Instance.Of("Act_NoSuchAction"));
        // The names compare without letter case, as the engine reads them.
        var name = Enum.GetNames<TriggerActions>()[0];
        Assert.Equal(EngineCoverage.Instance.Of(name), EngineCoverage.Instance.Of(name.ToUpperInvariant()));
    }

    /// <summary>A script with one trigger for each rule difference of section 6.4 of the M6c design, and one trigger
    /// without a difference. All names are invented.</summary>
    private const string Differences = """
        [Trigger]
        nickname = t_two
        Cnd_Timer = 5
        Cnd_Timer = 6

        [Trigger]
        repeatable = true
        nickname = t_before
        Cnd_Timer = 1

        [Trigger]
        nickname = t_dup
        Cnd_Timer = 1

        [Trigger]
        nickname = t_dup
        Cnd_Timer = 2

        [Trigger]
        nickname = t_order
        Cnd_Timer = 1
        Act_ActTrig = t_now
        Act_PlayMusic = ol_music

        [Trigger]
        nickname = t_now
        Cnd_True = no_params

        [Trigger]
        nickname = t_system
        Cnd_SystemEnter = ol_system

        [Trigger]
        nickname = t_restarter
        Cnd_Timer = 1
        Act_DeactTrig = t_again
        Act_ActTrig = t_again

        [Trigger]
        nickname = t_again
        Cnd_Timer = 3

        [Trigger]
        nickname = t_repeats
        Cnd_Timer = 3
        repeatable = true

        [Trigger]
        nickname = t_watcher
        Cnd_WatchTrigger = t_watched, COMPLETE

        [Trigger]
        nickname = t_watched
        Cnd_Timer = 3

        [Trigger]
        nickname = t_plain
        Cnd_Timer = 1
        Act_ActTrig = t_now
        """;

    /// <summary>The rule differences of the fork runtime for each trigger (section 6.4 of the M6c design). A start of a
    /// Cnd_True trigger as the last action does not change the order of the actions. The coverage without the engine
    /// gives none.</summary>
    [Theory]
    [InlineData("t_two", RuleDifference.TwoConditions)]
    [InlineData("t_before", RuleDifference.LinesBeforeNickname)]
    [InlineData("t_dup", RuleDifference.RepeatedNickname)]
    [InlineData("t_order", RuleDifference.StartOrder)]
    [InlineData("t_system", RuleDifference.SystemEnter)]
    [InlineData("t_again", RuleDifference.Restart)]
    [InlineData("t_repeats", RuleDifference.Repeats)]
    [InlineData("t_watched", RuleDifference.WatchComplete)]
    public void EachRuleDifferenceOfTheForkHasItsTrigger(string trigger, RuleDifference difference)
    {
        var index = MissionTestData.Index(Differences);

        Assert.Equal([difference], EngineCoverage.Instance.RuleDifferences(index, index.Trigger(trigger)!));
        Assert.Empty(FullCoverage.Instance.RuleDifferences(index, index.Trigger(trigger)!));
    }

    [Theory]
    [InlineData("t_plain")]
    [InlineData("t_now")]
    [InlineData("t_watcher")]
    [InlineData("t_restarter")]
    public void ATriggerWithoutADifferenceHasNone(string trigger)
    {
        var index = MissionTestData.Index(Differences);

        Assert.Empty(EngineCoverage.Instance.RuleDifferences(index, index.Trigger(trigger)!));
    }

    /// <summary>The second section of a repeated nickname has the difference too.</summary>
    [Fact]
    public void BothSectionsOfARepeatedNicknameDiffer()
    {
        var index = MissionTestData.Index(Differences);
        var second = index.Triggers.Single(t => t.IsDuplicate);

        Assert.Equal([RuleDifference.RepeatedNickname], EngineCoverage.Instance.RuleDifferences(index, second));
    }

    /// <summary>Each reason of the chip "Rules differ" passes the STE lint (section 8.1 of the M6c design).</summary>
    [Fact]
    public void EachReasonPassesTheSteLint()
    {
        Assert.All(Enum.GetValues<RuleDifference>(), d => Assert.Empty(SteLint.Problems(RuleDifferences.Reason(d))));
        Assert.Equal(8, Enum.GetValues<RuleDifference>().Select(RuleDifferences.Reason).Distinct().Count());
    }

    [Fact]
    public void TheIndexGivesEachLineTheMarkOfTheEngine()
    {
        var name = EngineCoverage.Instance.All.First(p => p.Value == EngineMark.NoEffect && p.Key.StartsWith("Act_")).Key;
        var index = MissionTestData.Index($"[Trigger]\nnickname = t1\nCnd_True = no_params\n{name} = x\n",
            coverage: EngineCoverage.Instance);

        Assert.Equal(EngineMark.NoEffect, index.Triggers[0].Rows.Single(r => r.Key == name).Mark);
    }
}

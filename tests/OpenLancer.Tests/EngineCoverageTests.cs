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

    [Fact]
    public void ThePinsOfTheActionsAndTheConditions()
    {
        Assert.Equal((49, 28, 8), (Count(typeof(TriggerActions), EngineMark.Runs),
            Count(typeof(TriggerActions), EngineMark.NoEffect), Count(typeof(TriggerActions), EngineMark.CannotLoad)));
        Assert.Equal((33, 9, 2), (Count(typeof(TriggerConditions), EngineMark.Runs),
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

    [Fact]
    public void TheIndexGivesEachLineTheMarkOfTheEngine()
    {
        var name = EngineCoverage.Instance.All.First(p => p.Value == EngineMark.NoEffect && p.Key.StartsWith("Act_")).Key;
        var index = MissionTestData.Index($"[Trigger]\nnickname = t1\nCnd_True = no_params\n{name} = x\n",
            coverage: EngineCoverage.Instance);

        Assert.Equal(EngineMark.NoEffect, index.Triggers[0].Rows.Single(r => r.Key == name).Mark);
    }
}

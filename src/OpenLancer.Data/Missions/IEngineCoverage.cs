using System.Collections.Generic;

namespace OpenLancer.Data.Missions;

/// <summary>What the OpenLancer engine does with a condition or an action name (section 14.8 of the M6a design).</summary>
public enum EngineMark
{
    /// <summary>The engine runs the line.</summary>
    Runs,

    /// <summary>The engine reads the line but does nothing with it.</summary>
    NoEffect,

    /// <summary>The engine stops when it loads the line, so the mission does not start.</summary>
    CannotLoad,

    /// <summary>The engine never runs the line. The line is an action of a trigger whose condition has no effect in the
    /// engine, so the trigger never runs its actions. The index gives this mark, not the engine.</summary>
    NeverRuns,
}

/// <summary>A trigger rule that the OpenLancer engine runs in another way than the retail game (section 6.4 of the M6c
/// design, decision D745). The live view of a mission play names it with the chip "Rules differ".</summary>
public enum RuleDifference
{
    /// <summary>TR1: the trigger has two conditions. The retail game uses only the last one.</summary>
    TwoConditions,

    /// <summary>TR2: the trigger has lines before its nickname.</summary>
    LinesBeforeNickname,

    /// <summary>TR4: another trigger has the same nickname.</summary>
    RepeatedNickname,

    /// <summary>TR7: the trigger starts a trigger that runs at once, then does more actions.</summary>
    StartOrder,

    /// <summary>TR8: the condition waits for a system.</summary>
    SystemEnter,

    /// <summary>TR9: a trigger stops this trigger and starts it again.</summary>
    Restart,

    /// <summary>TR10: the trigger repeats.</summary>
    Repeats,

    /// <summary>CN-15: a trigger watches the COMPLETE state of this trigger.</summary>
    WatchComplete,
}

/// <summary>The texts of the rule differences (section 8.1 of the M6c design).</summary>
public static class RuleDifferences
{
    /// <summary>The reason of a difference in the tooltip of the chip "Rules differ".</summary>
    public static string Reason(RuleDifference difference) => difference switch
    {
        RuleDifference.TwoConditions => "This trigger has two conditions. The retail game uses only the last one.",
        RuleDifference.LinesBeforeNickname => "This trigger has lines before its nickname. The retail game does not read them.",
        RuleDifference.RepeatedNickname => "Another trigger has the same nickname. The retail game uses the first one.",
        RuleDifference.StartOrder =>
            "This trigger starts a trigger that runs at once, then does more actions. The actions run in another order.",
        RuleDifference.SystemEnter => "This trigger waits for a system. OpenLancer waits for a jump into the system.",
        RuleDifference.Restart => "A trigger stops this trigger and starts it again. OpenLancer does not start it again.",
        RuleDifference.Repeats => "This trigger repeats. OpenLancer runs it only once.",
        _ => "A trigger watches the COMPLETE state of this trigger. OpenLancer keeps that state after a new start.",
    };
}

/// <summary>
/// The marks of the OpenLancer engine for the mission names (decision D526). The editor reads them from the engine at run
/// time and gives them to the index, so the data layer has no reference to the engine runtime. When the own runtime
/// replaces the fork, only the class behind this interface changes.
/// </summary>
public interface IEngineCoverage
{
    /// <summary>The mark of a condition or action name, without letter case. An unknown name runs, because the engine
    /// skips an unknown key.</summary>
    EngineMark Of(string name);

    /// <summary>The trigger rules that the engine runs in another way than the retail game for a trigger of an index
    /// (section 6.4 of the M6c design), in the order of the enum.</summary>
    IReadOnlyList<RuleDifference> RuleDifferences(MissionIndex index, MissionTrigger trigger);
}

/// <summary>Coverage that runs each name, for a run without the engine, for example a test of the checks.</summary>
public sealed class FullCoverage : IEngineCoverage
{
    public static readonly FullCoverage Instance = new();

    public EngineMark Of(string name) => EngineMark.Runs;

    public IReadOnlyList<RuleDifference> RuleDifferences(MissionIndex index, MissionTrigger trigger) => [];
}

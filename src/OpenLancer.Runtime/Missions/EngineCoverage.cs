using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using LibreLancer.Data.Schema.Missions;
using LibreLancer.Missions;
using LibreLancer.Missions.Actions;
using LibreLancer.Missions.Conditions;
using OpenLancer.Data.Missions;
using MissionTrigger = OpenLancer.Data.Missions.MissionTrigger;

namespace OpenLancer.Runtime.Missions;

/// <summary>
/// The marks of the fork mission runtime for each condition and action name (section 14.8 of the M6a design, decision
/// D526). It reads the types of the fork at run time:
/// <list type="bullet">
/// <item>A name that the fork lists as unsupported (<c>ScriptedAction.Unsupported</c> and
/// <c>ScriptedCondition.Unsupported</c>) has no effect. Since OL-0061, the fork skips such a line with a warning, and a
/// trigger whose only conditions it skips never runs.</item>
/// <item>An action has no effect when its class does not override <c>Invoke</c>, because the base only logs.</item>
/// <item>A condition has no effect when its class does not override <c>CheckCondition</c>, because the base logs once
/// and gives false. An event condition overrides it in its base class.</item>
/// <item>Each other name runs.</item>
/// </list>
/// The reflection reads types only. It makes no instance and calls no constructor, so it logs nothing.
/// </summary>
public sealed class EngineCoverage : IEngineCoverage
{
    private static readonly Lazy<EngineCoverage> Fork = new(() => new EngineCoverage());

    private readonly Dictionary<string, EngineMark> marks = new(StringComparer.OrdinalIgnoreCase);

    private EngineCoverage()
    {
        var types = typeof(ScriptedAction).Assembly.GetTypes();
        var actions = types.Where(t => !t.IsAbstract && typeof(ScriptedAction).IsAssignableFrom(t))
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var conditions = types.Where(t => !t.IsAbstract && typeof(ScriptedCondition).IsAssignableFrom(t))
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in Enum.GetValues<TriggerActions>())
        {
            marks[name.ToString()] = !ScriptedAction.Unsupported.Contains(name) &&
                                     actions.TryGetValue(name.ToString(), out var type) &&
                                     Overrides(type, nameof(ScriptedAction.Invoke), typeof(ScriptedAction))
                ? EngineMark.Runs
                : EngineMark.NoEffect;
        }
        foreach (var name in Enum.GetValues<TriggerConditions>())
        {
            marks[name.ToString()] = !ScriptedCondition.Unsupported.Contains(name) &&
                                     conditions.TryGetValue(name.ToString(), out var type) &&
                                     Overrides(type, nameof(ScriptedCondition.CheckCondition), typeof(ScriptedCondition))
                ? EngineMark.Runs
                : EngineMark.NoEffect;
        }
    }

    /// <summary>The marks of the fork that the editor loads.</summary>
    public static EngineCoverage Instance => Fork.Value;

    /// <summary>The names of the fork enums, conditions and actions, with their marks.</summary>
    public IReadOnlyDictionary<string, EngineMark> All => marks;

    public EngineMark Of(string name) => marks.GetValueOrDefault(name.Trim(), EngineMark.Runs);

    /// <summary>
    /// The trigger rules of the fork runtime that differ from the retail game for a trigger (section 6.4 of the M6c
    /// design, decision D745). MF1 keeps the rules of the fork (decision D549 b), so this class is the one place that
    /// knows them:
    /// <list type="bullet">
    /// <item>TR1: the fork needs each condition, and retail uses the last one.</item>
    /// <item>TR2: the fork reads the lines before the nickname.</item>
    /// <item>TR4: the fork keeps the last of two triggers with one name, and retail the first.</item>
    /// <item>TR7: the fork starts a trigger at once, depth first, so a start of a Cnd_True trigger runs its actions before
    /// the actions after the start.</item>
    /// <item>TR8: the fork Cnd_SystemEnter waits for a jump into the system.</item>
    /// <item>TR9: the fork does not start again a trigger that a stop marked in the same check.</item>
    /// <item>TR10: the fork does not read <c>repeatable</c>.</item>
    /// <item>CN-15: the fork keeps COMPLETE after a new start.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<RuleDifference> RuleDifferences(MissionIndex index, MissionTrigger trigger)
    {
        var result = new List<RuleDifference>();
        if (trigger.Conditions.Count > 1)
            result.Add(RuleDifference.TwoConditions);
        if (trigger.LinesBeforeNickname.Count > 0)
            result.Add(RuleDifference.LinesBeforeNickname);
        if (RepeatedNames(index).Contains(trigger.Nickname))
            result.Add(RuleDifference.RepeatedNickname);
        if (StartsAtOnceBeforeMore(index, trigger))
            result.Add(RuleDifference.StartOrder);
        if (trigger.ConditionIs("Cnd_SystemEnter"))
            result.Add(RuleDifference.SystemEnter);
        if (index.Restarts.Any(l => l.Target == trigger))
            result.Add(RuleDifference.Restart);
        if (trigger.Repeats)
            result.Add(RuleDifference.Repeats);
        if (index.LinksTo(trigger).Any(l => l.Kind == MissionLinkKind.Watch &&
                                          string.Equals(l.State, "COMPLETE", StringComparison.OrdinalIgnoreCase)))
            result.Add(RuleDifference.WatchComplete);
        return result;
    }

    // The nicknames that two or more triggers of an index have. The live view asks for each node several times in a
    // second, so the set stays with its index.
    private static readonly ConditionalWeakTable<MissionIndex, HashSet<string>> Repeated = new();

    private static HashSet<string> RepeatedNames(MissionIndex index) => Repeated.GetValue(index, i =>
        i.Triggers.Where(t => t.IsDuplicate).Select(t => t.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>True when a trigger starts a trigger with Cnd_True and has more actions after that start.</summary>
    private static bool StartsAtOnceBeforeMore(MissionIndex index, MissionTrigger trigger)
    {
        var actions = trigger.Actions;
        for (var i = 0; i < actions.Count - 1; i++)
        {
            if (actions[i].Key.Trim().Equals("Act_ActTrig", StringComparison.OrdinalIgnoreCase) &&
                actions[i].Value(0) is { } name && index.Trigger(name) is { } target && target.ConditionIs("Cnd_True"))
                return true;
        }
        return false;
    }

    /// <summary>True when a class of the method declares it below the base class: the class or a base class between
    /// them overrides it.</summary>
    private static bool Overrides(Type type, string method, Type baseType) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == method && m.GetBaseDefinition().DeclaringType == baseType)
            .Any(m => m.DeclaringType != baseType);
}

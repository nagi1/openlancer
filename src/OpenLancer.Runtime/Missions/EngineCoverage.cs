using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LibreLancer.Data.Schema.Missions;
using LibreLancer.Missions;
using LibreLancer.Missions.Actions;
using LibreLancer.Missions.Conditions;
using OpenLancer.Data.Missions;

namespace OpenLancer.Runtime.Missions;

/// <summary>
/// The marks of the fork mission runtime for each condition and action name (section 14.8 of the M6a design, decision
/// D526). It reads the types of the fork at run time:
/// <list type="bullet">
/// <item>A name that the fork lists as unsupported (<c>ScriptedAction.Unsupported</c> and
/// <c>ScriptedCondition.Unsupported</c>) cannot load.</item>
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
            marks[name.ToString()] = ScriptedAction.Unsupported.Contains(name) ? EngineMark.CannotLoad
                : actions.TryGetValue(name.ToString(), out var type) && Overrides(type, nameof(ScriptedAction.Invoke),
                    typeof(ScriptedAction)) ? EngineMark.Runs
                : EngineMark.NoEffect;
        }
        foreach (var name in Enum.GetValues<TriggerConditions>())
        {
            marks[name.ToString()] = ScriptedCondition.Unsupported.Contains(name) ? EngineMark.CannotLoad
                : conditions.TryGetValue(name.ToString(), out var type) && Overrides(type,
                    nameof(ScriptedCondition.CheckCondition), typeof(ScriptedCondition)) ? EngineMark.Runs
                : EngineMark.NoEffect;
        }
    }

    /// <summary>The marks of the fork that the editor loads.</summary>
    public static EngineCoverage Instance => Fork.Value;

    /// <summary>The names of the fork enums, conditions and actions, with their marks.</summary>
    public IReadOnlyDictionary<string, EngineMark> All => marks;

    public EngineMark Of(string name) => marks.GetValueOrDefault(name.Trim(), EngineMark.Runs);

    /// <summary>True when a class of the method declares it below the base class: the class or a base class between
    /// them overrides it.</summary>
    private static bool Overrides(Type type, string method, Type baseType) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == method && m.GetBaseDefinition().DeclaringType == baseType)
            .Any(m => m.DeclaringType != baseType);
}

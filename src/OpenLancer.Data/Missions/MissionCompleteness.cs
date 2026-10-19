using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The completeness reports of a mission and of its prefab instances (section 13.5 of the M6a design, AGENTS.md UI rule
/// 12). A step is done when no shown problem of its rules exists, and its member roles and checks hold.
/// </summary>
/// <remarks>A missing step is hidden when the base game misses it too (decision D551): its problems are base-game
/// problems, or the base-game run of the script has the same instance with the same step missing. An instance is the same
/// when its prefab and its core trigger are the same. In a script that no mod changes, each missing step is
/// hidden.</remarks>
public static class MissionCompleteness
{
    /// <summary>The fixed checks that a step of the prefab data can name.</summary>
    public static readonly IReadOnlySet<string> Checks = new HashSet<string>
    {
        "sceneRebuild", "bothPaths", "watchersStopEachOther", "sidesOfSpawned", "targetsMarked",
    };

    /// <summary>The steps of a mission, in the order of section 13.5 of the M6a design, with their rules.</summary>
    private static readonly (string Name, string[] Rules, string Reason)[] MissionSteps =
    [
        ("Start trigger", ["G01"], "Without a start trigger, nothing in the mission runs."),
        ("Mission success", ["G02", "G03"], "Without one success, the story cannot go to the next mission."),
        ("Fail texts", ["G04"], "The game shows this text when the mission fails."),
        ("One condition for each trigger", ["M01", "M03"],
            "A trigger without a condition stops the game when it starts. The game ignores all conditions but the last."),
        ("Names resolve", ["M05", "M06", "M21", "M22", "M23"],
            "The game ignores a name that it cannot find, so the line does nothing."),
        ("Each trigger can start", ["G05"], "The game never runs a trigger that nothing starts."),
        ("Cutscenes end", ["G06"],
            "Without \"End camera\", the camera does not come back to the player, and the player stays invulnerable."),
    ];

    /// <summary>The completeness of a mission: its 7 steps.</summary>
    public static CompletenessReport OfMission(MissionAnalysis analysis)
    {
        var steps = MissionSteps.Select(s => RuleStep(s.Name, s.Reason, analysis.Problems.Where(p => p.Rule != null &&
            s.Rules.Contains(p.Rule)).ToList())).ToList();
        return new CompletenessReport("Mission", steps);
    }

    /// <summary>The completeness of a prefab instance: the steps of its prefab.</summary>
    public static CompletenessReport OfInstance(MissionAnalysis analysis, MissionPrefabInstance instance)
    {
        var sections = instance.Triggers.Select(t => t.Section).Append(instance.CoreSection).OfType<MissionSection>()
            .Select(s => s.Ref).ToHashSet();
        var baseInstance = analysis.BasePrefabs?.Instances.FirstOrDefault(i => i.Key == instance.Key);
        var steps = new List<CompletenessStep>();
        foreach (var step in instance.Definition.Steps)
        {
            if (step.Rules.Count > 0)
            {
                var problems = analysis.Problems.Where(p => p.Rule != null && step.Rules.Contains(p.Rule) &&
                                                            p.Target != null && sections.Contains(p.Target)).ToList();
                var ruleStep = RuleStep(step.Name, step.Reason, problems);
                if (ruleStep.State != CompletenessState.Done || step.Requires == null && step.Check == null)
                {
                    steps.Add(ruleStep);
                    continue;
                }
            }
            var done = Holds(analysis.Index, instance, step);
            var state = done ? CompletenessState.Done
                : analysis.Script.State == MissionScriptState.BaseGame ||
                  baseInstance != null && analysis.BaseIndex is { } baseIndex && !Holds(baseIndex, baseInstance, step)
                    ? CompletenessState.Hidden
                    : CompletenessState.Missing;
            steps.Add(new CompletenessStep(step.Name, state, step.Reason, GoTo: instance.CoreSection?.Ref));
        }
        return new CompletenessReport(instance.Definition.Name, steps);
    }

    /// <summary>A step whose state comes from the problems of its rules.</summary>
    private static CompletenessStep RuleStep(string name, string reason, IReadOnlyList<Diagnostic> problems)
    {
        if (problems.Count == 0)
            return new CompletenessStep(name, CompletenessState.Done, reason);
        var shown = problems.Where(p => p.Origin?.IsBaseGame != true).ToList();
        var first = shown.FirstOrDefault() ?? problems[0];
        return new CompletenessStep(name, shown.Count > 0 ? CompletenessState.Missing : CompletenessState.Hidden, reason,
            first.Message, first.Target, IsError: shown.Any(p => p.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>True when the member role or the check of a step holds.</summary>
    private static bool Holds(MissionIndex index, MissionPrefabInstance instance, PrefabStep step)
    {
        if (step.Requires != null && instance.Role(step.Requires).Count == 0)
            return false;
        return step.Check switch
        {
            null => true,
            "sceneRebuild" => SceneRebuild(index, instance),
            "bothPaths" => instance.Role("on").Count > 0 && instance.Role("off").Count > 0,
            "watchersStopEachOther" => WatchersStopEachOther(instance),
            "sidesOfSpawned" => SidesOfSpawned(index, instance),
            "targetsMarked" => TargetsMarked(instance),
            _ => false,
        };
    }

    /// <summary>The restore trigger, or a trigger with Cnd_True that it starts, spawns ships, sets sides or sets an
    /// objective.</summary>
    private static bool SceneRebuild(MissionIndex index, MissionPrefabInstance instance)
    {
        foreach (var restore in instance.Role("restore"))
        {
            var candidates = index.Links.Where(l => l.Source == restore && l.Kind == MissionLinkKind.Start &&
                                                    l.Target?.ConditionIs("Cnd_True") == true)
                .Select(l => l.Target!).Prepend(restore);
            if (candidates.Any(t => t.Actions.Any(a => a.Name?.Group is "spawn" or "sides" or "objective")))
                return true;
        }
        return false;
    }

    /// <summary>Each watcher of the active state stops each watcher of the other state, and the other way.</summary>
    private static bool WatchersStopEachOther(MissionPrefabInstance instance)
    {
        var on = instance.Role("on");
        var off = instance.Role("off");
        return on.All(a => off.All(b => Stops(a, b))) && off.All(a => on.All(b => Stops(a, b)));
    }

    private static bool Stops(MissionTrigger trigger, MissionTrigger target) =>
        trigger.ActionsOf("Act_DeactTrig").Any(a => MissionIndex.Is(a.Value(0), target.Nickname));

    /// <summary>An attitude line of the spawn trigger names a spawned ship, formation, member or label.</summary>
    private static bool SidesOfSpawned(MissionIndex index, MissionPrefabInstance instance)
    {
        if (instance.Core is not { } core)
            return false;
        var spawned = MissionPrefabs.SpawnedNames(index, core);
        return core.Actions.Where(a => a.Name?.Group == "sides")
            .Any(a => spawned.Contains(a.Value(0) ?? "") || spawned.Contains(a.Value(1) ?? ""));
    }

    /// <summary>Each target that a done trigger waits for has a mark.</summary>
    private static bool TargetsMarked(MissionPrefabInstance instance)
    {
        if (instance.Core is not { } core)
            return false;
        var marked = core.ActionsOf("Act_MarkObj").Select(a => a.Value(0)).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return instance.Role("done").Select(t => t.Condition?.Value(0)).OfType<string>().All(marked.Contains);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The completeness reports of a mission, of its items and of its prefab instances (section 13.5 of the M6a design,
/// AGENTS.md UI rule 12). A step is done when no shown problem of its rules exists, and its member roles and checks
/// hold. The rule data holds the names and the reasons of the steps. The class makes no ImGui call, so the Inspector,
/// the graph and later an agent server (roadmap row MCP1) read the same reports.
/// </summary>
/// <remarks>A missing step is hidden when the base game misses it too (decision D551). Then its problems are base-game
/// problems, or the base-game run of the script has the same item with the same step missing. An instance of a prefab is
/// the same when its prefab and its core trigger are the same. A mission ship is the same when its section is the
/// same.</remarks>
public static class MissionCompleteness
{
    /// <summary>The fixed checks that a step of the prefab data can name.</summary>
    public static readonly IReadOnlySet<string> Checks = new HashSet<string>
    {
        "sceneRebuild", "bothPaths", "watchersStopEachOther", "sidesOfSpawned", "targetsMarked",
    };

    /// <summary>The fixed checks that a step of a mission item can name.</summary>
    public static readonly IReadOnlySet<string> ItemChecks = new HashSet<string> { "shipBuildParts" };

    /// <summary>The completeness of a mission: its 7 steps.</summary>
    public static CompletenessReport OfMission(MissionAnalysis analysis)
    {
        var item = analysis.Index.Schema.Completeness["mission"];
        var steps = item.Steps.Select(s => RuleStep(s.Name, s.Reason, ProblemsOf(analysis.Problems, s))).ToList();
        return new CompletenessReport(item.Title, steps) { HiddenMessages = BaseGameMessages.Count(analysis.Problems) };
    }

    /// <summary>
    /// The completeness of a mission ship (section 10.6 of the M6a design). The steps are its pilot and voice, its ship
    /// build, and the archetype, the loadout and the pilot of the build. The first two steps come from the checks of
    /// the names of the ship and of its <c>[NPC]</c>. The build is in a mission ship file, so the third step compares
    /// its names with the names of the game data. A step that the game data cannot tell stays done.
    /// </summary>
    public static CompletenessReport OfShip(MissionRunResult result, MissionAnalysis analysis, MissionSection ship)
    {
        var item = analysis.Index.Schema.Completeness["missionShip"];
        var npc = NpcOf(analysis.Index, ship);
        var sections = new[] { ship, npc }.OfType<MissionSection>().Select(s => s.Ref).ToHashSet();
        var problems = analysis.Problems.Where(p => p.Target != null && sections.Contains(p.Target)).ToList();
        var steps = item.Steps.Select(step => step.Check == "shipBuildParts"
            ? BuildParts(result, analysis, ship, step)
            : RuleStep(step.Name, step.Reason, ProblemsOf(problems, step))).ToList();
        return new CompletenessReport(item.Title, steps)
        {
            HiddenMessages = BaseGameMessages.Count(problems.Where(p => p.Rule != null && item.Steps.Any(s => s.Rules.Contains(p.Rule)))),
        };
    }

    /// <summary>The completeness of a conversation: its comm lines and its speakers (rules M21 and M19).</summary>
    public static CompletenessReport OfConversation(MissionAnalysis analysis, MissionSection conversation) =>
        OfLines(analysis, "conversation", analysis.Problems.Where(p => Equals(p.Target, conversation.Ref)).ToList());

    /// <summary>The completeness of a cutscene link: the file, the anchor and the end camera of an
    /// <c>Act_CallThorn</c> line.</summary>
    public static CompletenessReport OfCutsceneLink(MissionAnalysis analysis, MissionLine line) =>
        OfLines(analysis, "cutsceneLink", ProblemsOfLine(analysis, line));

    /// <summary>The completeness of a base scene link: the file and the path length of an <c>Act_AddRTC</c> or
    /// <c>Act_RemoveRTC</c> line.</summary>
    public static CompletenessReport OfBaseSceneLink(MissionAnalysis analysis, MissionLine line) =>
        OfLines(analysis, "baseSceneLink", ProblemsOfLine(analysis, line));

    /// <summary>The problems of the checks for one line.</summary>
    private static List<Diagnostic> ProblemsOfLine(MissionAnalysis analysis, MissionLine line) =>
        analysis.Problems.Where(p => Equals(p.Target, line.Section.Ref) && p.EntryIndex == line.Entry).ToList();

    /// <summary>A report whose steps all come from the problems of their rules.</summary>
    private static CompletenessReport OfLines(MissionAnalysis analysis, string id, IReadOnlyList<Diagnostic> problems)
    {
        var item = analysis.Index.Schema.Completeness[id];
        return new CompletenessReport(item.Title,
            item.Steps.Select(s => RuleStep(s.Name, s.Reason, ProblemsOf(problems, s))).ToList())
        {
            HiddenMessages = BaseGameMessages.Count(problems),
        };
    }

    /// <summary>The problems of the rules of a step, on the lines of its key.</summary>
    private static List<Diagnostic> ProblemsOf(IEnumerable<Diagnostic> problems, MissionStepSpec step) =>
        problems.Where(p => p.Rule != null && step.Rules.Contains(p.Rule) &&
                            (step.Key == null || MissionIndex.Is(p.Key, step.Key))).ToList();

    /// <summary>The <c>[NPC]</c> that a mission ship names, or null.</summary>
    public static MissionSection? NpcOf(MissionIndex index, MissionSection ship) =>
        index.LinesOf(ship).FirstOrDefault(l => MissionIndex.Is(l.Key, "npc"))?.Value(0) is { } name
            ? index.Item(MissionSectionKind.Npc, name)
            : null;

    /// <summary>The ship build that the <c>[NPC]</c> of a mission ship names, or null.</summary>
    public static NpcShipBuild? BuildOf(NpcShipBuilds builds, MissionIndex index, MissionSection ship) =>
        NpcOf(index, ship) is { } npc &&
        index.LinesOf(npc).FirstOrDefault(l => MissionIndex.Is(l.Key, "npc_ship_arch"))?.Value(0) is { } build
            ? builds.Find(build)
            : null;

    /// <summary>
    /// The step "Archetype, loadout and pilot" of a mission ship: each name of its build is in the game data. The step
    /// is hidden when the base game misses the same names for the same ship (decision D551). So a mod that removes a
    /// loadout of a retail ship build gives a missing step, also when the mod does not change the script. A step of a
    /// script that only a mod has is never hidden.
    /// </summary>
    private static CompletenessStep BuildParts(MissionRunResult result, MissionAnalysis analysis, MissionSection ship,
        MissionStepSpec step)
    {
        var index = analysis.Index;
        var build = BuildOf(result.ShipBuilds, index, ship);
        var missing = build != null ? MissingParts(build, index.World) : [];
        if (build == null || missing.Count == 0)
            return new CompletenessStep(step.Name, CompletenessState.Done, step.Reason);

        var baseIndex = analysis.BaseIndex ?? (analysis.Script.State == MissionScriptState.BaseGame ? index : null);
        var baseShip = baseIndex?.Document.Sections.FirstOrDefault(s => s.Ref.Equals(ship.Ref));
        var baseBuild = baseShip != null ? BuildOf(result.BaseShipBuilds ?? result.ShipBuilds, baseIndex!, baseShip) : null;
        var baseMissing = baseBuild != null ? MissingParts(baseBuild, result.BaseWorld ?? index.World) : [];
        var hidden = missing.All(baseMissing.Contains);
        var (kind, name) = missing[0];
        var detail = $"Ship build '{build.Nickname}' names {KindText(kind)} '{name}'. The game data has no " +
                     $"{KindText(kind)} with this name.";
        return new CompletenessStep(step.Name, hidden ? CompletenessState.Hidden : CompletenessState.Missing, step.Reason,
            detail, build.Ref, IsError: !hidden);
    }

    /// <summary>The names of a build that a world does not have, without letter case. A kind that the world does not
    /// know does not count.</summary>
    private static List<(MissionValueKind Kind, string Name)> MissingParts(NpcShipBuild build, MissionWorld world) =>
        build.Parts.Where(p => world.Knows(p.Kind) && !world.Has(p.Kind, p.Name))
            .Select(p => (p.Kind, p.Name.ToLowerInvariant())).ToList();

    private static string KindText(MissionValueKind kind) => kind switch
    {
        MissionValueKind.ShipArch => "ship archetype",
        MissionValueKind.Loadout => "loadout",
        _ => "pilot",
    };

    /// <summary>
    /// The completeness of a prefab in prefab mode (section 8.6 of the M6b design): the prefab file has no problem of the
    /// format check, the lines of the template have no error, and each field is in a line. A missing step of a field
    /// goes to the field on the Prefab page.
    /// </summary>
    public static CompletenessReport OfPrefab(Editing.PrefabModeModel prefab)
    {
        var steps = new List<CompletenessStep>
        {
            prefab.Asset.Problems is [var first, ..]
                ? new CompletenessStep(PrefabFileStep, CompletenessState.Missing, PrefabFileReason, first.ToString(), IsError: true)
                : new CompletenessStep(PrefabFileStep, CompletenessState.Done, PrefabFileReason),
        };
        if (prefab.Template is { } template)
        {
            steps.Add(RuleStep(TemplateLinesStep, TemplateLinesReason,
                prefab.Analysis?.Problems.Where(p => p.Severity == DiagnosticSeverity.Error).ToList() ?? []));
            var unused = template.Fields.FirstOrDefault(f => !template.Sections.Any(s => s.Lines.Any(l =>
                Prefabs.PrefabSlot.KeyField(l.Key) == f.Id || Prefabs.PrefabSlot.Of(l.Value).Any(slot => slot.Field == f.Id))));
            steps.Add(unused == null
                ? new CompletenessStep(FieldsStep, CompletenessState.Done, FieldsReason)
                : new CompletenessStep(FieldsStep, CompletenessState.Missing, FieldsReason,
                    $"Field '{unused.Label}' is in no line, so it changes nothing. Make it the value of a line, or remove it.",
                    new MissionRef(prefab.Asset.Ref.Script, MissionItemKind.Mission, Key: "field:" + unused.Id)));
        }
        return new CompletenessReport(prefab.Asset.Name, steps);
    }

    // The steps of a prefab in prefab mode.
    private const string PrefabFileStep = "Prefab file";
    private const string PrefabFileReason = "The editor places and compiles only a prefab whose file has no problem.";
    private const string TemplateLinesStep = "Valid lines";
    private const string TemplateLinesReason = "Each instance gets these lines, so an error of a line is an error of each instance.";
    private const string FieldsStep = "Fields in lines";
    private const string FieldsReason = "A field changes the instances only through the lines that hold its value.";

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
            steps.Add(new CompletenessStep(step.Name, state, step.Reason, GoTo: instance.CoreSection?.Ref,
                Fix: state == CompletenessState.Missing && Editing.MissionFixes.HasStepFix(instance.Definition.Id, step.Id)
                    ? Editing.MissionFixes.StepSubject(instance, step.Id)
                    : null));
        }
        if (instance.Linked is { } linked)
        {
            // A linked instance also needs the lines of its prefab (rules P01 to P03, section 6.10 of the M6b design).
            var problems = analysis.Problems.Where(p => p.Rule is "P01" or "P02" or "P03" &&
                                                        p.Subject == $"{p.Rule}|{linked.Record.Id}").ToList();
            steps.Add(RuleStep(Prefabs.PrefabLinks.StepName, Prefabs.PrefabLinks.StepReason, problems));
        }
        return new CompletenessReport(instance.Definition.Name, steps)
        {
            HiddenMessages = BaseGameMessages.Count(analysis.Problems.Where(p => p.Target != null && sections.Contains(p.Target))),
        };
    }

    /// <summary>A step whose state comes from the problems of its rules (decision D551). The step is done without a
    /// problem, and missing with a problem that shows. It is hidden when each problem is a base-game problem.</summary>
    public static CompletenessStep RuleStep(string name, string reason, IReadOnlyList<Diagnostic> problems)
    {
        if (problems.Count == 0)
            return new CompletenessStep(name, CompletenessState.Done, reason);
        var shown = problems.Where(p => p.Origin?.IsBaseGame != true).ToList();
        var first = shown.FirstOrDefault() ?? problems[0];
        // A problem with a fix that needs no choice gives the step its fix (section 8.7 of the M6b design).
        return new CompletenessStep(name, shown.Count > 0 ? CompletenessState.Missing : CompletenessState.Hidden, reason,
            first.Message, first.Target, Editing.MissionFixes.HasFix(first.Rule) ? first.Subject : null,
            IsError: shown.Any(p => p.Severity == DiagnosticSeverity.Error));
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

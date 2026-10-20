using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>A fix of a problem or of a missing completeness step: its id, its label and its commands.</summary>
/// <param name="Id">The id of the fix, for example <c>keepLast</c>.</param>
/// <param name="Label">The label of the button, for example "Keep the last condition".</param>
public sealed record MissionFix(string Id, string Label, IReadOnlyList<MissionCommand> Commands);

/// <summary>
/// The completeness fixes of section 8.7 of the M6b design (decision D684): only fixes that need no choice of the user.
/// Each fix is one <see cref="ApplyFix"/> command and one undo entry. The other rules and steps keep "Go to".
/// </summary>
public static class MissionFixes
{
    /// <summary>The rules that have a fix.</summary>
    private static readonly HashSet<string> Rules =
        ["M01", "M03", "M04", "M05", "M06", "M07", "M08", "M09", "M13", "M14", "M15", "M23", "P01", "P03"];

    /// <summary>The space between the node of a trigger and the node of a new trigger below it, in canvas points.</summary>
    private const float BesideGap = 200;

    /// <summary>The tooltip of the button "Fix" (section 8.11 of the M6b design).</summary>
    public const string ButtonTip = "Make the change that the step needs. Undo takes it back.";

    /// <summary>The start of the subject of a fix of a completeness step of a prefab instance.</summary>
    public const string StepPrefix = "step|";

    /// <summary>The steps of the prefabs that have a fix: the prefab id and the step id.</summary>
    private static readonly HashSet<(string Prefab, string Step)> Steps =
        [("branch", "stopEachOther"), ("spawnWave", "end"), ("offer", "decline")];

    /// <summary>True when a rule has a fix.</summary>
    public static bool HasFix(string? rule) => rule != null && Rules.Contains(rule);

    /// <summary>True when a step of a prefab has a fix.</summary>
    public static bool HasStepFix(string prefab, string step) => Steps.Contains((prefab, step));

    /// <summary>The subject of the fix of a step of a prefab instance, for <see cref="ApplyFix"/>.</summary>
    public static string StepSubject(MissionPrefabInstance instance, string step) => $"{StepPrefix}{instance.Key}|{step}";

    /// <summary>The fixes of a problem of an analysis. None for a problem without a fix.</summary>
    public static IReadOnlyList<MissionFix> Of(MissionAnalysis analysis, Diagnostic problem)
    {
        if (problem.Rule is "P01" or "P03" && problem.Subject is { } subject &&
            analysis.Prefabs?.Instances.FirstOrDefault(i => i.Linked?.Record.Id == subject[(subject.IndexOf('|') + 1)..]) is
                { } linked)
        {
            // The fixes of a linked prefab instance (section 8.7 of the M6b design).
            var instance = new InstanceRef(analysis.Script.Path, linked.Id);
            var compile = new MissionFix("compile", "Compile again", [new CompilePrefabInstance(instance)]);
            return problem.Rule == "P01"
                ? [compile, new MissionFix("unpack", "Unpack", [new UnpackPrefabInstance(instance)])]
                : [compile];
        }
        if (!HasFix(problem.Rule) || problem.Target is not { } target || analysis.Document.Find(target) is not { } section)
            return [];
        var index = analysis.Index;
        var script = analysis.Script.Path;
        var item = new ItemRef(script, section.Ref.Id);
        var trigger = index.Triggers.FirstOrDefault(t => t.Section == section);
        var lines = index.LinesOf(section);
        var line = problem.EntryIndex >= 0 && problem.EntryIndex < lines.Count ? lines[problem.EntryIndex] : null;
        RowRef Row(MissionLine l) => new(item, l.Entry) { ExpectKey = l.Key };
        MissionFix One(string id, string label, MissionCommand command) => new(id, label, [command]);
        switch (problem.Rule)
        {
            case "M01" when trigger != null:
                return [One("atOnce", "Add the condition \"At once\"",
                    new SetCondition(new TriggerRef(script, trigger.Nickname), LineSpec.Raw("Cnd_True", "no_params")))];
            case "M03" when trigger is { Conditions.Count: > 1 }:
                // From the last line to the first, so each row index stays valid.
                return [new MissionFix("keepLast", "Keep the last condition", trigger.Conditions.Take(trigger.Conditions.Count - 1)
                    .OrderByDescending(c => c.Entry).Select(c => (MissionCommand)new RemoveLine(Row(c))).ToList())];
            case "M04" when trigger != null && line != null:
                return
                [
                    One("remove", "Remove the line", new RemoveLine(Row(line))),
                    One("move", "Move it after the nickname", new MoveLine(Row(line)) { Before = trigger.NicknameLine + 1 }),
                ];
            case "M05" or "M06" or "M23" when line != null && !IsKeyLine(section, line):
                return [One("remove", "Remove the line", new RemoveLine(Row(line)))];
            case "M07" when line != null && SpawnFormation(index, line) is { } spawn:
                return [One("spawnFormation", "Spawn the formation", new SetActionLine(Row(line), spawn))];
            case "M08" when line?.Value(0) is { } ship && index.Item(MissionSectionKind.Ship, ship) is { } shipSection:
                return [One("moveShip", "Move the ship before the formation",
                    new MoveSection(new ItemRef(script, shipSection.Ref.Id)) { Before = item })];
            case "M09" when line != null && lines.FirstOrDefault(l => MissionIndex.Is(l.Key, "formation") ||
                                                                      MissionIndex.Is(l.Key, "ship")) is { } first:
                return [One("moveLine", $"Move the line before '{first.Key}'", new MoveLine(Row(line)) { Before = first.Entry })];
            case "M13" when line?.References.FirstOrDefault(r => r.State == MissionReferenceState.WrongCase) is
                { Expected: { } token } wrong:
                return [One("case", $"Write '{token}'", new SetLineValue(Row(line), token) { Index = wrong.ValueIndex })];
            case "M14" when line != null && BoolAt(line) is { } at:
                return [One("false", "Write 'false'", new SetLineValue(Row(line), "false") { Index = at })];
            case "M15" when line != null && FractionAt(line) is var (at, whole):
                return [One("whole", $"Write {MissionIndex.Number(whole)}",
                    new SetLineValue(Row(line), MissionIndex.Number(whole)) { Index = at })];
            default:
                return [];
        }
    }

    /// <summary>
    /// The fix of a missing step of a prefab instance (section 8.7 of the M6b design), or null. "Watchers stop each other"
    /// adds the stop lines. "End trigger" of a wave with one spawned item adds a trigger that waits until the item is
    /// destroyed, and the spawn trigger starts it. "Decline trigger" adds a trigger with the decline answer, which starts
    /// where the accept trigger starts.
    /// </summary>
    /// <param name="graph">The graph file of the script, for the place of a new node, or null.</param>
    public static MissionFix? OfStep(MissionAnalysis analysis, MissionPrefabInstance instance, string step,
        GraphFile? graph = null)
    {
        var script = analysis.Script.Path;
        var taken = analysis.Index.Triggers.Select(t => t.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);
        switch ((instance.Definition.Id, step))
        {
            case ("branch", "stopEachOther"):
            {
                var on = instance.Role("on");
                var off = instance.Role("off");
                var commands = on.SelectMany(a => off.Select(b => (a, b))).Concat(off.SelectMany(a => on.Select(b => (a, b))))
                    .Where(p => !p.a.ActionsOf("Act_DeactTrig").Any(l => MissionIndex.Is(l.Value(0), p.b.Nickname)))
                    .Select(p => (MissionCommand)new LinkTriggers(new TriggerRef(script, p.a.Nickname), p.b.Nickname, LinkKind.Stop))
                    .ToList();
                return commands.Count == 0 ? null : new MissionFix("stops", "Add the stop lines", commands);
            }
            case ("spawnWave", "end") when instance.Core is { } core && instance.Role("end").Count == 0:
            {
                var spawned = core.Actions.Where(a => MissionIndex.Is(a.Key, "Act_SpawnShip") || MissionIndex.Is(a.Key, "Act_SpawnFormation"))
                    .Select(a => a.Value(0)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (spawned.Count != 1)
                    return null;
                var name = MissionNaming.Free(script, "Trigger", taken, core.Nickname + "_end");
                return new MissionFix("end", "Add an end trigger",
                [
                    new AddTrigger(new ScriptRef(script))
                    {
                        Nickname = name,
                        Condition = LineSpec.Of("Cnd_Destroyed", ("target", spawned[0]), ("count", -1)),
                        // A node beside the spawn trigger, when the graph file has its place.
                        Place = graph?.Place(core.Nickname) is { } place ? new GraphPoint(place.X, place.Y + BesideGap) : null,
                    },
                    new LinkTriggers(new TriggerRef(script, core.Nickname), name, LinkKind.Start),
                ]);
            }
            case ("offer", "decline") when instance.Role("decline").Count == 0 && instance.Core is { } accept:
            {
                // The decline trigger waits for the answer at the same time as the accept trigger. So it starts with the
                // mission when the accept trigger does, and each trigger that starts the accept trigger starts it too.
                // Without a start, the game never runs it.
                var name = MissionNaming.Free(script, "Trigger", taken, accept.Nickname + "_decline");
                var commands = new List<MissionCommand>
                {
                    new AddTrigger(new ScriptRef(script))
                    {
                        Nickname = name, Condition = LineSpec.Of("Cnd_MsnResponse", ("answer", "reject")),
                        StartsWithMission = accept.StartsActive,
                        Place = graph?.Place(accept.Nickname) is { } place ? new GraphPoint(place.X, place.Y + BesideGap) : null,
                    },
                };
                commands.AddRange(analysis.Index.LinksTo(accept)
                    .Where(l => l.Kind == MissionLinkKind.Start && l.Source != accept)
                    .Select(l => l.Source).Distinct()
                    .Select(source => new LinkTriggers(new TriggerRef(script, source.Nickname), name, LinkKind.Start)));
                return new MissionFix("decline", "Add a decline trigger", commands);
            }
            default:
                return null;
        }
    }

    /// <summary>The fixes of a subject of <see cref="ApplyFix"/> in an analysis: of the first problem with the subject,
    /// or of a step of a prefab instance.</summary>
    public static IReadOnlyList<MissionFix> OfSubject(MissionAnalysis analysis, string subject, GraphFile? graph = null)
    {
        if (subject.StartsWith(StepPrefix, StringComparison.Ordinal))
        {
            var parts = subject[StepPrefix.Length..];
            var split = parts.LastIndexOf('|');
            if (split < 0 || analysis.Prefabs?.Instances.FirstOrDefault(i => i.Key == parts[..split]) is not { } instance)
                return [];
            return OfStep(analysis, instance, parts[(split + 1)..], graph) is { } fix ? [fix] : [];
        }
        return analysis.Problems.FirstOrDefault(p => p.Subject == subject) is { } problem ? Of(analysis, problem) : [];
    }

    private static bool IsKeyLine(MissionSection section, MissionLine line) =>
        section.Kind == MissionSectionKind.Trigger
            ? line.Trigger?.NicknameLine == line.Entry
            : MissionIndex.Is(line.Key, "nickname") && section.Section.ToList().FindIndex(e => MissionIndex.Is(e.Name, "nickname")) == line.Entry;

    /// <summary>The line of <c>Act_SpawnFormation</c> for a spawn of a ship that a formation takes: the formation, and
    /// the position and the rotation of the spawn line.</summary>
    private static LineSpec? SpawnFormation(MissionIndex index, MissionLine line)
    {
        if (!MissionIndex.Is(line.Key, "Act_SpawnShip") || line.Value(0) is not { } ship ||
            !index.TakenShips.TryGetValue(ship, out var formation) || formation.Nickname is not { } name)
            return null;
        var texts = new List<string> { name };
        var position = Enumerable.Range(2, 3).Select(line.Value).ToList();
        if (position.All(v => v != null))
        {
            texts.AddRange(position!);
            var rotation = Enumerable.Range(5, 4).Select(line.Value).ToList();
            if (rotation.All(v => v != null))
                texts.AddRange(rotation!);
        }
        return LineSpec.Raw("Act_SpawnFormation", [.. texts]);
    }

    /// <summary>The index of a bool value that the game does not read as a bool, or null.</summary>
    private static int? BoolAt(MissionLine line)
    {
        foreach (var spec in line.Specs.Where(s => s.Kind == MissionValueKind.Bool))
        {
            for (var i = spec.Index; i < Math.Min(spec.End, line.Count); i++)
            {
                if (line.Value(i) is { } value && !TriggerRules.ReadsAsBool(value))
                    return i;
            }
        }
        return null;
    }

    /// <summary>The index and the whole part of a fraction in a whole-number value, or null.</summary>
    private static (int At, double Whole)? FractionAt(MissionLine line)
    {
        foreach (var spec in line.Specs.Where(s => s.Kind == MissionValueKind.WholeNumber || s.Whole))
        {
            for (var i = spec.Index; i < Math.Min(spec.End, line.Count); i++)
            {
                if (line.Value(i) is { } value &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    number != Math.Truncate(number))
                    return (i, Math.Truncate(number));
            }
        }
        return null;
    }
}

/// <summary>
/// Runs a fix of section 8.7 of the M6b design: the fix of a problem by its subject, or of a missing step of a prefab
/// instance. The command finds the problem in W now, so a fix that no longer applies refuses (<c>FixNotFound</c>).
/// </summary>
/// <param name="Subject">The subject of the problem, for example <c>M03|ol_wave|Cnd_True</c>, or the subject of a step
/// (<see cref="MissionFixes.StepSubject"/>).</param>
/// <param name="Fix">The id of the fix, for example <c>keepLast</c>.</param>
public sealed record ApplyFix(ScriptRef Script, string Subject, string Fix) : MissionCommand
{
    protected override string Describe() => "Fix";

    internal override void Run(MissionDraft d)
    {
        var document = d.Script(Script);
        var analysis = d.Editor.ModelOf(document, document.State).Analysis;
        var fix = MissionFixes.OfSubject(analysis, Subject, d.Graph(document)).FirstOrDefault(f => f.Id == Fix) ??
                  throw d.Refuse(MissionErrors.FixNotFoundError(document.Path));
        var inverses = new List<MissionCommand>();
        foreach (var command in fix.Commands)
            inverses.InsertRange(0, d.RunInner(command));
        d.Inverse(inverses.Count == 1 ? inverses[0] : new CommandGroup(inverses));
    }
}

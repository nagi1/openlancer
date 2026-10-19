using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions;

/// <summary>One name of a mission line that names an item of the world: an item of the script, or an object of a
/// system.</summary>
/// <param name="Line">The line.</param>
/// <param name="Name">The name as the line gives it.</param>
/// <param name="Section">The section of an item of the script, or null for an object of a system.</param>
public sealed record MissionUse(MissionLine Line, string Name, MissionSection? Section)
{
    /// <summary>True for an object of a system, for example the gate of a lock line.</summary>
    public bool IsObject => Section == null;
}

/// <summary>
/// The links between the lines of a mission and the items of the world (section 7.3 of the M6a design): the items that a
/// line names, and the lines that name an item. The cross-highlighting of the graph and the Scene, the Used by sections
/// and the Behaviors section of a system object read them. A label stands for each of its members. The class makes no
/// ImGui call, so the tests and later an agent server (roadmap row MCP1) call it.
/// </summary>
public static class MissionUses
{
    /// <summary>The kinds of the values that name items of the world: the cast, the labels, the order lists, the
    /// objectives and the objects of a system.</summary>
    private static readonly HashSet<MissionValueKind> WorldKinds =
    [
        MissionValueKind.Object, MissionValueKind.Group, MissionValueKind.Ship, MissionValueKind.Formation,
        MissionValueKind.Solar, MissionValueKind.Loot, MissionValueKind.ObjList, MissionValueKind.Objective,
        MissionValueKind.SystemObject, MissionValueKind.Base,
    ];

    /// <summary>The items of the world that lines name, in line order. A label gives each of its members. A name
    /// that the script does not have and that is not Player is an object of a system.</summary>
    public static IReadOnlyList<MissionUse> Of(MissionIndex index, IEnumerable<MissionLine> lines)
    {
        var result = new List<MissionUse>();
        foreach (var line in lines)
        {
            foreach (var reference in line.References.Where(r => r.State == MissionReferenceState.Found &&
                                                                 WorldKinds.Contains(r.Kind)))
            {
                if (reference.Section is { } section)
                    Add(result, new MissionUse(line, reference.Name, section));
                else if (index.Labels.TryGetValue(reference.Name.Trim(), out var members))
                {
                    foreach (var member in members)
                        Add(result, new MissionUse(line, reference.Name, member));
                }
                else if (!MissionIndex.Is(reference.Name, "Player"))
                    Add(result, new MissionUse(line, reference.Name, null));
            }
        }
        return result;
    }

    private static void Add(List<MissionUse> uses, MissionUse use)
    {
        if (!uses.Any(u => u.Line == use.Line && u.Section == use.Section &&
                           (use.Section != null || MissionIndex.Is(u.Name, use.Name))))
            uses.Add(use);
    }

    /// <summary>The lines of the triggers of a selection: one line, all lines of a trigger, or all lines of the triggers
    /// of a prefab instance.</summary>
    public static IReadOnlyList<MissionLine> LinesOf(IEnumerable<MissionTrigger> triggers) =>
        triggers.SelectMany(t => t.Rows).ToList();

    /// <summary>The lines that name an item of the script: its nickname, or a label that it has. A line of a
    /// duplicate trigger does not count, because the game skips that trigger.</summary>
    public static IReadOnlyList<MissionLine> Naming(MissionIndex index, MissionSection section)
    {
        if (section.Nickname is not { } nickname)
            return [];
        var kind = section.Kind switch
        {
            MissionSectionKind.OrderList => MissionValueKind.ObjList,
            MissionSectionKind.Objective => MissionValueKind.Objective,
            MissionSectionKind.Conversation => MissionValueKind.Dialog,
            MissionSectionKind.Npc => MissionValueKind.Npc,
            MissionSectionKind.RandomEncounter => MissionValueKind.RandomEncounter,
            _ => MissionValueKind.Object,
        };
        var references = index.ReferencesTo(kind, nickname).Where(r => r.Section == section).ToList();
        if (kind == MissionValueKind.Object)
        {
            foreach (var (label, members) in index.Labels)
            {
                if (members.Contains(section))
                    references.AddRange(index.ReferencesTo(MissionValueKind.Object, label).Where(r => r.Section == null));
            }
        }
        return Distinct(references.Select(r => r.Line));
    }

    /// <summary>The lines that name an object of a system or a base by its nickname, for example the gate of a lock
    /// line or the object of an arrival line.</summary>
    public static IReadOnlyList<MissionLine> NamingObject(MissionIndex index, string nickname)
    {
        var references = index.ReferencesTo(MissionValueKind.SystemObject, nickname).Where(r => r.Section == null)
            .Concat(index.ReferencesTo(MissionValueKind.Base, nickname));
        return Distinct(references.Where(r => !index.Labels.ContainsKey(r.Name.Trim())).Select(r => r.Line));
    }

    /// <summary>The triggers of lines, without repeats, in file order.</summary>
    public static IReadOnlyList<MissionTrigger> TriggersOf(IEnumerable<MissionLine> lines) =>
        lines.Select(l => l.Trigger).OfType<MissionTrigger>().Where(t => !t.IsDuplicate).Distinct()
            .OrderBy(t => t.Section.Index).ToList();

    private static IReadOnlyList<MissionLine> Distinct(IEnumerable<MissionLine> lines) =>
        lines.Where(l => l.Trigger?.IsDuplicate != true).Distinct().OrderBy(l => l.Section.Index).ThenBy(l => l.Entry)
            .ToList();
}

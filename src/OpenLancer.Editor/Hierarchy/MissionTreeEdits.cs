using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;

namespace OpenLancer.Editor.Hierarchy;

/// <summary>
/// The edit gestures of the tree of a mission in the Hierarchy (sections 8.4 and 8.9 of the M6b design): the "+" menu,
/// F2, Ctrl+D, Delete, the drags of rows and the drop of a ship tile on the group "Cast". Each gesture gives one batch of
/// mission commands, the same commands as the graph. The class makes no ImGui call, so the tests check each gesture.
/// </summary>
internal static class MissionTreeEdits
{
    /// <summary>The items of the "+" menu: the kind of the item and its label.</summary>
    public static readonly IReadOnlyList<(string Kind, string Label)> CreateKinds =
    [
        ("trigger", "Trigger"), ("ship", "Mission ship"), ("formation", "Formation"), ("solar", "Solar"), ("loot", "Loot"),
        ("order_list", "Order list"), ("objective", "Objective"), ("conversation", "Conversation"), ("npc", "NPC"),
        ("random_encounter", "Random encounter"), ("act", "Act"),
    ];

    /// <summary>The reason of a "+" item that cannot add, or null. A new mission ship of the menu gets the pilot of the
    /// first <c>[NPC]</c> of the script. Without one, a ship tile of the Assets panel adds a ship.</summary>
    public static string? CreateRefusal(MissionAnalysis analysis, string kind) =>
        kind == "ship" && FirstNpc(analysis) == null
            ? "The script has no pilot. Drag a ship from the Assets panel onto the group Cast."
            : null;

    private static string? FirstNpc(MissionAnalysis analysis) =>
        analysis.Document.OfKind(MissionSectionKind.Npc).Select(s => s.Nickname).FirstOrDefault(n => n != null);

    /// <summary>
    /// The batch of an item of the "+" menu (section 8.4 of the M6b design). A new trigger starts at once, so it has a
    /// condition. A new mission ship uses the pilot and the ship build of the first <c>[NPC]</c> of the script. Each
    /// result selects the new item. Null for a kind that cannot add now.
    /// </summary>
    public static MissionBatch? Create(MissionAnalysis analysis, string kind)
    {
        var script = new ScriptRef(analysis.Script.Path);
        MissionCommand? command = kind switch
        {
            "trigger" => new AddTrigger(script) { Condition = LineSpec.Raw("Cnd_True", "no_params") },
            "ship" when FirstNpc(analysis) is { } npc => new AddMissionShip(script) { Npc = npc },
            "act" => new AddAct(script),
            "ship" => null,
            _ => new AddItem(script, kind),
        };
        var label = CreateKinds.FirstOrDefault(k => k.Kind == kind).Label ?? kind;
        return command == null ? null : new MissionBatch($"Add {InText(label)}", [command]);
    }

    /// <summary>A label of the "+" menu inside a text: the first letter in lowercase, except in a name of capitals such as
    /// "NPC".</summary>
    private static string InText(string label) =>
        label.Length > 1 && char.IsUpper(label[1]) ? label : char.ToLowerInvariant(label[0]) + label[1..];

    /// <summary>The batch of a rename (F2) of a row: a trigger, an item of the script or an act. The graph and the
    /// Hierarchy give the same batch for a trigger. Null for another row, for a line of a trigger or for the same
    /// name.</summary>
    public static MissionBatch? Rename(MissionAnalysis analysis, MissionRef item, string name, MissionActs? acts)
    {
        name = name.Trim();
        if (name.Length == 0)
            return null;
        var script = analysis.Script.Path;
        if (item.Kind == MissionItemKind.Act && (acts ?? analysis.Acts)?.Act(item.Key ?? "") is { } act)
            return new MissionBatch($"Rename act {MissionGraph.ActTitle(act, null)}",
                [new SetAct(new ActRef(script, MissionActs.NicknameOf(act))) { Name = name }]);
        if (SectionOf(analysis, item) is not { Nickname: { } old } section || old == name)
            return null;
        if (section.Kind == MissionSectionKind.Trigger)
            return TriggerOf(analysis, section) is { } trigger ? GraphEditModel.Rename(trigger, name) : null;
        return new MissionBatch($"Rename {old} to {name}", [new RenameItem(new ItemRef(script, section.Ref.Id), name)]);
    }

    /// <summary>True when F2 can rename a row: a trigger, an item of the script with a nickname, or an act.</summary>
    public static bool CanRename(MissionAnalysis analysis, MissionRef item, MissionActs? acts) =>
        item.Kind == MissionItemKind.Act
            ? (acts ?? analysis.Acts)?.Act(item.Key ?? "") != null
            : SectionOf(analysis, item) is { Nickname: not null, Kind: not MissionSectionKind.Mission };

    /// <summary>The batch of Ctrl+D on a row: a copy of a trigger or of an item of the script with a free name. The
    /// graph and the Hierarchy give the same batch for a trigger. Null for another row or for a line of a
    /// trigger.</summary>
    public static MissionBatch? Duplicate(MissionAnalysis analysis, MissionRef item)
    {
        if (SectionOf(analysis, item) is not { Nickname: { } name } section)
            return null;
        if (section.Kind == MissionSectionKind.Trigger)
            return TriggerOf(analysis, section) is { } trigger ? GraphEditModel.Duplicate(analysis.Index, [(trigger, null)]) : null;
        return new MissionBatch($"Duplicate {name}", [new DuplicateItem(new ItemRef(analysis.Script.Path, section.Ref.Id))]);
    }

    /// <summary>The trigger of a section of a trigger, or null.</summary>
    private static MissionTrigger? TriggerOf(MissionAnalysis analysis, MissionSection section) =>
        analysis.Index.Triggers.FirstOrDefault(t => t.Section == section);

    /// <summary>The batch of Delete on a row: a trigger, a mission ship with its unused parts (decision D669), another
    /// item of the script, or an act. Lines that name the item stay and show "Missing". Null for another row and for a
    /// line of a trigger, so the key does not delete the whole trigger.</summary>
    public static MissionBatch? Delete(MissionAnalysis analysis, MissionRef item, MissionActs? acts)
    {
        var script = analysis.Script.Path;
        if (item.Kind == MissionItemKind.Act && (acts ?? analysis.Acts)?.Act(item.Key ?? "") is { } act)
            return new MissionBatch("Delete act", [new RemoveAct(new ActRef(script, MissionActs.NicknameOf(act)))]);
        if (SectionOf(analysis, item) is not { } section || section.Kind is MissionSectionKind.Mission)
            return null;
        var reference = new ItemRef(script, section.Ref.Id);
        MissionCommand command = section.Kind switch
        {
            MissionSectionKind.Trigger when section.Nickname != null &&
                                            analysis.Index.Triggers.FirstOrDefault(t => t.Section == section) is { IsDuplicate: false } =>
                new RemoveTrigger(new TriggerRef(script, section.Nickname)),
            MissionSectionKind.Ship => new RemoveMissionShip(reference),
            _ => new RemoveItem(reference),
        };
        return new MissionBatch($"Delete {section.Nickname ?? section.Ref.Id.ToString()}", [command]);
    }

    /// <summary>
    /// Why a row cannot drop on another row, or null (section 8.9 of the M6b design). A ship drops on a formation, a
    /// ship, a formation or a solar drops on a label, and a trigger drops on an act.
    /// </summary>
    public static string? DropRefusal(MissionAnalysis analysis, MissionRef dragged, MissionRef target)
    {
        var source = SectionOf(analysis, dragged);
        return target.Kind switch
        {
            MissionItemKind.Formation when source?.Kind == MissionSectionKind.Ship => null,
            MissionItemKind.Label when source?.Kind is MissionSectionKind.Ship or MissionSectionKind.Formation or
                MissionSectionKind.Solar => null,
            MissionItemKind.Act when source?.Kind == MissionSectionKind.Trigger && target.Key != MissionActs.NotStartedKey => null,
            MissionItemKind.Formation => "Only a mission ship can join a formation.",
            MissionItemKind.Label => "Only a ship, a formation or a solar can have a label.",
            MissionItemKind.Act => "Only a trigger can join an act.",
            _ => "Drop the row on a formation, a label or an act.",
        };
    }

    /// <summary>
    /// The batch of a drop of a row on another row (section 8.9 of the M6b design): a ship joins a formation
    /// (<see cref="SetFormationMembers"/>), an item gets a label (<see cref="AddLabel"/>), and a trigger moves to an act
    /// (<see cref="SetActMembers"/>). Null when the drop does not fit or changes nothing.
    /// </summary>
    public static MissionBatch? Drop(MissionAnalysis analysis, MissionRef dragged, MissionRef target, MissionActs? acts)
    {
        if (DropRefusal(analysis, dragged, target) != null || SectionOf(analysis, dragged) is not { Nickname: { } name } source)
            return null;
        var script = analysis.Script.Path;
        switch (target.Kind)
        {
            case MissionItemKind.Formation when SectionOf(analysis, target) is { Nickname: { } formation } section:
                var members = section.Section.Where(e => MissionIndex.Is(e.Name, "ship") && e.Count > 0)
                    .Select(e => MissionIndex.Text(e[0])).ToList();
                if (members.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return null;
                return new MissionBatch($"Add {name} to {formation}",
                    [new SetFormationMembers(new ItemRef(script, section.Ref.Id), [.. members, name])]);
            case MissionItemKind.Label when target.Key is { } label:
                if (analysis.Index.Labels.TryGetValue(label, out var labeled) && labeled.Contains(source))
                    return null;
                return new MissionBatch($"Add {name} to {label}", [new AddLabel(new ItemRef(script, source.Ref.Id), label)]);
            case MissionItemKind.Act when (acts ?? analysis.Acts ?? MissionActs.Propose(analysis.Index)).Act(target.Key ?? "") is { } act:
                if (act.Triggers.Any(t => t.Section == source))
                    return null;
                return new MissionBatch($"Move {name} to {MissionGraph.ActTitle(act, null)}",
                    [new SetActMembers(new ActRef(script, MissionActs.NicknameOf(act)), [.. act.Triggers.Select(t => t.Nickname), name])]);
            default:
                return null;
        }
    }

    /// <summary>
    /// The batch of a drop of a ship tile of the Assets panel on the group "Cast" (section 8.9 of the M6b design): one
    /// <see cref="AddMissionShip"/>, so one undo entry. The tile gives the ship archetype and its loadout. The pilot comes
    /// from the list, and the faction from a small menu.
    /// </summary>
    /// <param name="loadout">The loadout of the ship archetype, or null when the game data has none. The ship build then
    /// names no loadout, and the page shows "Missing" until the user picks one.</param>
    public static MissionBatch AddShip(MissionAnalysis analysis, string archetype, string? loadout, string? pilot,
        string faction) =>
        new($"Add mission ship {archetype}", [new AddMissionShip(new ScriptRef(analysis.Script.Path))
        {
            Pilot = new PilotSpec { Faction = faction },
            NewBuild = new ShipBuildSpec(archetype, loadout ?? MissionLibrary.NoName) { Pilot = pilot },
        }]);

    /// <summary>The section of a row of the tree, or null. A line of a trigger, for example a row of the group "Links", is
    /// not its section (review finding 7 of M6b-2).</summary>
    private static MissionSection? SectionOf(MissionAnalysis analysis, MissionRef item) =>
        item.Kind != MissionItemKind.Row && item.Section is { } section ? analysis.Document.Find(section) : null;
}

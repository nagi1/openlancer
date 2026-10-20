using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The graph file of a script in the draft (section 6.8 of the M6b design): the node places and the acts. The
/// graph file is an editor file of the session, so the edit history, the save and the recovery cover it.</summary>
internal sealed partial class MissionDraft
{
    /// <summary>The records of the graph file of a script now. A read does not touch the file. The editor keeps the
    /// records of each text, so a command reads a file once while it does not change.</summary>
    public GraphFile Graph(EditDocument script) => IsTemplate(script) ? GraphFile.Empty : Editor.Graph(script.Path);

    /// <summary>
    /// Writes the graph file of a script. The nodes and the act members of triggers that the script does not have go
    /// (section 6.8 of the M6b design). A file without records goes, so a save deletes it.
    /// </summary>
    /// <exception cref="EditException">The session has no active mod, the file cannot be read, or it has a format that
    /// this editor does not read.</exception>
    public void SetGraph(EditDocument script, GraphFile file)
    {
        if (file.Unreadable is { } reason)
            throw Refuse(new EditError(EditErrorKind.Refused, EditPlan.PlanRefused,
                $"{GraphFile.PathOf(script.Path)}: {reason}"));
        var names = TriggerNames(script).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = file.Pruned(names.Contains).Write(script.Path);
        try
        {
            ModFile(GraphFile.PathOf(script.Path)).SetText(text);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.IO.IOException or
                                      UnauthorizedAccessException)
        {
            throw Refuse(new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, e.Message));
        }
    }

    /// <summary>The acts of a graph file for an act edit. The first act edit writes all proposed acts into the file
    /// (decision D679), so a file without acts gives the proposed acts of W now, with their made nicknames. A proposed act
    /// gets no name, so its title stays "Act {n}: {system name}" (review finding 13 of M6b-2).</summary>
    /// <param name="written">True when the acts are the proposed acts.</param>
    public List<GraphAct> ActsForEdit(EditDocument script, GraphFile file, out bool written)
    {
        written = file.Acts.Count == 0;
        if (!written)
            return file.Acts.ToList();
        var inputs = Editor.WorldNow();
        var index = MissionIndex.Build(MissionDocument.Of(script.Path, script.Working, script.IsBinary), Schema, inputs.World,
            inputs.Coverage);
        return MissionActs.Propose(index).Acts.Select(a => new GraphAct(MissionActs.NicknameOf(a), "", "",
            a.Triggers.Select(t => t.Nickname).ToList())).ToList();
    }

    /// <summary>The index of an act in a list, by its nickname without letter case.</summary>
    /// <exception cref="EditException"><c>ItemNotFound</c>.</exception>
    public int ActIndex(EditDocument script, List<GraphAct> acts, ActRef act)
    {
        var at = acts.FindIndex(a => a.Nickname.Equals(act.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            return at;
        throw Refuse(MissionErrors.ItemNotFoundError(script.Path, "act", act.Name,
            ItemRefs.Hint(act.Name, acts.Select(a => a.Nickname))) with { Target = act.ToString() });
    }

    /// <summary>The names of triggers of a script with the letter case of the script.</summary>
    /// <exception cref="EditException"><c>TriggerNotFound</c> for a name that the script does not have.</exception>
    public List<string> TriggerNamesOf(EditDocument script, IEnumerable<string> names)
    {
        var result = new List<string>();
        foreach (var name in names)
        {
            var index = TriggerIndex(script, name);
            if (index < 0)
                throw Refuse(MissionErrors.TriggerNotFoundError(script.Path, name, ItemRefs.Hint(name, TriggerNames(script))));
            var nickname = TriggerRules.Nickname(script.Working[index])!;
            if (!result.Contains(nickname, StringComparer.OrdinalIgnoreCase))
                result.Add(nickname);
        }
        return result;
    }

    /// <summary>
    /// The inverse of an act edit: the commands that give the old act records back. After the first act edit, the
    /// inverse removes each act, so the file has no act records again and the graph shows the proposed acts.
    /// </summary>
    public MissionCommand ActsInverse(ScriptRef script, IReadOnlyList<GraphAct> before, IReadOnlyList<GraphAct> after,
        bool written)
    {
        var commands = new List<MissionCommand>();
        if (written)
        {
            commands.AddRange(after.Select(a => new RemoveAct(new ActRef(script.Path, a.Nickname))));
            return commands.Count == 1 ? commands[0] : new CommandGroup(commands);
        }
        foreach (var act in after)
        {
            if (!before.Any(b => b.Nickname.Equals(act.Nickname, StringComparison.OrdinalIgnoreCase)))
                commands.Add(new RemoveAct(new ActRef(script.Path, act.Nickname)));
        }
        for (var i = 0; i < before.Count; i++)
        {
            var old = before[i];
            var now = after.FirstOrDefault(a => a.Nickname.Equals(old.Nickname, StringComparison.OrdinalIgnoreCase));
            var reference = new ActRef(script.Path, old.Nickname);
            if (now == null)
            {
                commands.Add(new AddAct(script)
                {
                    Nickname = old.Nickname, Name = old.Name, Text = old.Text, Members = old.Members, Index = i,
                });
                continue;
            }
            if (now.Name != old.Name || now.Text != old.Text)
                commands.Add(new SetAct(reference) { Name = old.Name, Text = old.Text });
            if (!now.Members.SequenceEqual(old.Members))
                commands.Add(new SetActMembers(reference, old.Members));
        }
        return commands.Count == 1 ? commands[0] : new CommandGroup(commands);
    }

    /// <summary>A made nickname of a new act, free in the acts: <c>act_{n}</c>.</summary>
    public static string NewActName(IReadOnlyList<GraphAct> acts)
    {
        for (var n = acts.Count + 1;; n++)
        {
            var name = "act_" + n.ToString(CultureInfo.InvariantCulture);
            if (!acts.Any(a => a.Nickname.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }
}

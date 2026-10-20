using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The place of the node of a trigger in the graph, in canvas points, or the removal of its place.</summary>
/// <param name="Trigger">The nickname of the trigger.</param>
public sealed record NodePlace(string Trigger, double X, double Y)
{
    /// <summary>True removes the place, so the node takes the automatic layout again.</summary>
    public bool Remove { get; init; }

    /// <summary>The place of a new record in the list of the node records, or null for the end. The inverses use it, so
    /// the order of the records comes back.</summary>
    public int? Index { get; init; }
}

/// <summary>Sets the places of nodes in the graph file of a script (section 6.8 of the M6b design). One drag of nodes is
/// one command, so one history entry.</summary>
public sealed record MoveNodes(ScriptRef Script, IReadOnlyList<NodePlace> Places) : MissionCommand
{
    protected override string Describe() => Places.Count == 1 ? $"Move {Places[0].Trigger}" : "Move nodes";

    internal override void Run(MissionDraft d)
    {
        var document = d.Script(Script);
        var graph = d.Graph(document);
        var nodes = graph.Nodes.ToList();
        var inverse = new List<NodePlace>();
        foreach (var place in Places)
        {
            var name = place.Remove ? place.Trigger.Trim() : d.TriggerNamesOf(document, [place.Trigger])[0];
            var at = nodes.FindIndex(n => n.Trigger.Equals(name, StringComparison.OrdinalIgnoreCase));
            inverse.Add(at >= 0
                ? new NodePlace(nodes[at].Trigger, nodes[at].X, nodes[at].Y) { Index = place.Remove ? at : null }
                : new NodePlace(name, 0, 0) { Remove = true });
            if (place.Remove)
            {
                if (at >= 0)
                    nodes.RemoveAt(at);
                continue;
            }
            var node = new GraphNodePlace(name, (float)Math.Round(place.X), (float)Math.Round(place.Y));
            if (at >= 0)
                nodes[at] = node;
            else
                nodes.Insert(Math.Clamp(place.Index ?? nodes.Count, 0, nodes.Count), node);
        }
        d.SetGraph(document, graph with { Nodes = nodes });
        inverse.Reverse();
        d.Inverse(new MoveNodes(Script, inverse));
    }
}

/// <summary>Adds an act to the graph file (section 6.8 of the M6b design). The first act edit writes all proposed acts
/// first (decision D679). A trigger is in one act, so each member leaves its other act.</summary>
public sealed record AddAct(ScriptRef Script) : MissionCommand
{
    /// <summary>The nickname of the act, or null for a made nickname <c>act_{n}</c>.</summary>
    public string? Nickname { get; init; }

    /// <summary>The name of the act, or an empty name for "Act {n}".</summary>
    public string Name { get; init; } = "";

    /// <summary>The walkthrough text of the act.</summary>
    public string Text { get; init; } = "";

    /// <summary>The triggers of the act, in order.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>The place of the act in the list of the acts, or null for the end.</summary>
    public int? Index { get; init; }

    protected override string Describe() => $"Add act {Name}".TrimEnd();

    internal override void Run(MissionDraft d)
    {
        var document = d.Script(Script);
        var graph = d.Graph(document);
        var acts = d.ActsForEdit(document, graph, out var written);
        var before = acts.ToList();
        var nickname = Nickname?.Trim() ?? MissionDraft.NewActName(acts);
        if (!MissionNaming.IsValidNew(nickname, "Act"))
            throw d.Refuse(MissionErrors.NameInvalidError(nickname, 47));
        if (acts.Any(a => a.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)))
            throw d.Refuse(MissionErrors.NameTakenError(document.Path, "act", nickname));
        var members = d.TriggerNamesOf(document, Members);
        acts = acts.Select(a => a with { Members = a.Members.Where(m => !members.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList() })
            .ToList();
        var name = Name.Trim().Length > 0 ? Name.Trim() : $"Act {acts.Count + 1}";
        acts.Insert(Math.Clamp(Index ?? acts.Count, 0, acts.Count), new GraphAct(nickname, name, Text.Trim(), members));
        d.SetGraph(document, graph with { Acts = acts });
        var reference = new ActRef(document.Path, nickname);
        d.Adds.Add(reference.ToString());
        d.Select = reference.ToString();
        d.Inverse(d.ActsInverse(new ScriptRef(document.Path), before, acts, written));
    }
}

/// <summary>Sets the name or the walkthrough text of an act. A null value keeps its value.</summary>
public sealed record SetAct(ActRef Act) : MissionCommand
{
    public string? Name { get; init; }

    public string? Text { get; init; }

    protected override string Describe() => $"Set act {Act.Name}";

    internal override void Run(MissionDraft d)
    {
        var document = d.Script(Act.Script);
        var graph = d.Graph(document);
        var acts = d.ActsForEdit(document, graph, out var written);
        var before = acts.ToList();
        var at = d.ActIndex(document, acts, Act);
        acts[at] = acts[at] with
        {
            Name = Name?.Trim() is { Length: > 0 } name ? name : acts[at].Name,
            Text = Text?.Trim() ?? acts[at].Text,
        };
        if (!written && acts[at].Equals(before[at]))
            return;
        d.SetGraph(document, graph with { Acts = acts });
        d.Changes.Add(Act.ToString());
        d.Inverse(d.ActsInverse(new ScriptRef(document.Path), before, acts, written));
    }
}

/// <summary>Sets the triggers of an act in order (section 6.8 of the M6b design). A trigger is in one act, so each
/// trigger of the list leaves its other act.</summary>
public sealed record SetActMembers(ActRef Act, IReadOnlyList<string> Triggers) : MissionCommand
{
    protected override string Describe() => $"Set the triggers of act {Act.Name}";

    internal override void Run(MissionDraft d)
    {
        var document = d.Script(Act.Script);
        var graph = d.Graph(document);
        var acts = d.ActsForEdit(document, graph, out var written);
        var before = acts.ToList();
        var at = d.ActIndex(document, acts, Act);
        var members = d.TriggerNamesOf(document, Triggers);
        acts = acts.Select((a, i) => i == at
            ? a with { Members = members }
            : a with { Members = a.Members.Where(m => !members.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList() }).ToList();
        if (!written && acts.SequenceEqual(before))
            return;
        d.SetGraph(document, graph with { Acts = acts });
        d.Changes.Add(Act.ToString());
        d.Inverse(d.ActsInverse(new ScriptRef(document.Path), before, acts, written));
    }
}

/// <summary>Removes an act from the graph file. Its triggers stay. When no act stays, the graph shows the proposed
/// acts again.</summary>
public sealed record RemoveAct(ActRef Act) : MissionCommand
{
    protected override string Describe() => $"Delete act {Act.Name}";

    internal override void Run(MissionDraft d)
    {
        var document = d.Script(Act.Script);
        var graph = d.Graph(document);
        var acts = d.ActsForEdit(document, graph, out var written);
        var before = acts.ToList();
        var at = d.ActIndex(document, acts, Act);
        acts.RemoveAt(at);
        d.SetGraph(document, graph with { Acts = acts });
        d.Deletes.Add(new ResultDelete(Act.ToString(), OpenLancer.Data.Editing.Scene.DeleteReason.Target, null));
        d.Inverse(d.ActsInverse(new ScriptRef(document.Path), before, acts, written));
    }
}

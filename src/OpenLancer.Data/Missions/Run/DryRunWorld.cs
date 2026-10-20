using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions.Run;

/// <summary>
/// The world of a dry run (section 4.3 of the M6c design, decision D755): the world that the modder controls. An event
/// matches the waiting lines with its key. A fact stays true until the modder clears it. The player has a system and is
/// in space or not. The world also records the comm lines that the run sent and the objects that it spawned, so the
/// world panel can show the rows that the run makes likely.
/// </summary>
public sealed class DryRunWorld : ITriggerWorld
{
    private static readonly HashSet<string> CommActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Act_SendComm", "Act_EtherComm",
    };

    private static readonly HashSet<string> SpawnActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Act_SpawnShip", "Act_SpawnShipRel", "Act_SpawnFormation", "Act_SpawnSolar", "Act_SpawnLoot",
    };

    private static readonly HashSet<MissionValueKind> ObjectKinds =
    [
        MissionValueKind.Object, MissionValueKind.Group, MissionValueKind.Ship, MissionValueKind.Formation,
        MissionValueKind.Solar, MissionValueKind.Loot,
    ];

    private readonly MissionIndex index;
    private readonly HashSet<string> facts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> sent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> spawned = new(StringComparer.OrdinalIgnoreCase);

    public DryRunWorld(MissionIndex index) => this.index = index;

    /// <summary>The system of the player, or null. <c>Cnd_SystemEnter</c> is true at the start of a trigger in this
    /// system (TR8).</summary>
    public string? PlayerSystem { get; set; }

    /// <summary>True while the player is in space. A timer with <c>RUN_IN_SPACE_ONLY</c> counts only then. A new dry run
    /// starts in space.</summary>
    public bool InSpace { get; set; } = true;

    /// <summary>The number of the step that runs now. The world records it with each comm line and spawn.</summary>
    public int Step { get; set; }

    /// <summary>The keys of the facts that are true.</summary>
    public IReadOnlyCollection<string> Facts => facts;

    /// <summary>True when the fact of a line is true.</summary>
    public bool IsFact(MissionLine line) => facts.Contains(WorldEvents.KeyOf(line));

    /// <summary>Sets or clears the fact of a key.</summary>
    public void SetFact(string key, bool on)
    {
        if (on)
            facts.Add(key);
        else
            facts.Remove(key);
    }

    public bool Matches(MissionTrigger trigger, MissionLine condition, WorldEvent worldEvent) =>
        WorldEvents.KeyOf(condition) == worldEvent.Key;

    public bool Poll(MissionTrigger trigger, MissionLine condition) =>
        facts.Count > 0 && facts.Contains(WorldEvents.KeyOf(condition));

    public bool TestAtStart(MissionTrigger trigger, MissionLine condition) =>
        PlayerSystem != null && MissionIndex.Is(condition.Value(0), PlayerSystem);

    /// <summary>Records the comm lines that an action sends and the objects that it spawns: a ship, the ships of a
    /// formation, and the labels of each.</summary>
    public void Effect(MissionTrigger trigger, MissionLine action)
    {
        if (CommActions.Contains(action.Key.Trim()) && action.ValueOf("line") is { } line)
        {
            sent[line] = Step;
            return;
        }
        if (!SpawnActions.Contains(action.Key.Trim()) || action.Value(0) is not { } name)
            return;
        spawned[name] = Step;
        var sections = index.Items(MissionSectionKind.Formation, name).Take(1)
            .Concat(index.Items(MissionSectionKind.Ship, name).Take(1))
            .Concat(index.Items(MissionSectionKind.Solar, name).Take(1))
            .Concat(index.Items(MissionSectionKind.Loot, name).Take(1)).ToList();
        foreach (var section in sections.ToList())
        {
            foreach (var ship in section.Section.Where(e => MissionIndex.Is(e.Name, "ship") && e.Count > 0))
            {
                var shipName = MissionIndex.Text(ship[0]);
                spawned[shipName] = Step;
                sections.AddRange(index.Items(MissionSectionKind.Ship, shipName).Take(1));
            }
        }
        foreach (var label in sections.SelectMany(s => s.Section).Where(e => MissionIndex.Is(e.Name, "label") && e.Count > 0))
            spawned[MissionIndex.Text(label[0])] = Step;
    }

    /// <summary>Why a waiting line is likely to come next: a comm line that the run sent, or an object that the run
    /// spawned. Null for another line.</summary>
    public WorldCause? CauseOf(MissionLine condition)
    {
        if (condition.Name is not { } name)
            return null;
        if (MissionIndex.Is(name.Name, "Cnd_CommComplete"))
            return condition.ValueOf("line") is { } line && sent.TryGetValue(line, out var at) ? new WorldCause(false, at) : null;
        var steps = name.Values.Where(v => ObjectKinds.Contains(v.Kind))
            .SelectMany(v => Enumerable.Range(v.Index, Math.Max(0, Math.Min(v.End, condition.Count) - v.Index)))
            .Select(condition.Value).OfType<string>()
            .Select(value => spawned.TryGetValue(value, out var step) ? step : -1).Where(s => s >= 0).ToList();
        return steps.Count > 0 ? new WorldCause(true, steps.Max()) : null;
    }

    /// <summary>A copy, for the snapshots of the dry run.</summary>
    public DryRunWorld Copy()
    {
        var copy = new DryRunWorld(index) { PlayerSystem = PlayerSystem, InSpace = InSpace, Step = Step };
        copy.facts.UnionWith(facts);
        foreach (var (key, value) in sent)
            copy.sent[key] = value;
        foreach (var (key, value) in spawned)
            copy.spawned[key] = value;
        return copy;
    }

    /// <summary>A world with the player and the facts of this world, without the records of the run. A new run takes
    /// the world of the run before it, as the world stays when a game loads.</summary>
    public DryRunWorld Next(MissionIndex next)
    {
        var copy = new DryRunWorld(next) { PlayerSystem = PlayerSystem, InSpace = InSpace, Step = Step };
        copy.facts.UnionWith(facts);
        return copy;
    }
}

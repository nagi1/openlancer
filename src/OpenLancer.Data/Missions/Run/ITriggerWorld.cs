namespace OpenLancer.Data.Missions.Run;

/// <summary>
/// One world event of a run: one distinct condition line that waits for an event of the world (section 4.3 of the M6c
/// design). All active triggers whose condition has the same line react to it, in the order of the active list.
/// </summary>
/// <param name="Key">The key of the line: its condition name and its values, so two lines with the same key are one
/// event.</param>
public sealed record WorldEvent(string Key);

/// <summary>
/// The world of a run of the trigger rule machine (section 3.1 of the M6c design, decision D743). The machine has no
/// world, so it asks this interface about the events, the facts and the place of the player. It tells it each action
/// that has no flow. The dry run gives a world that the modder controls. A later runtime can give the game world.
/// </summary>
public interface ITriggerWorld
{
    /// <summary>True when an event matches the condition of an active trigger. The machine asks only for the condition
    /// that the game uses (TR1).</summary>
    bool Matches(MissionTrigger trigger, MissionLine condition, WorldEvent worldEvent);

    /// <summary>True when a poll condition of the world is true now. The machine asks at each heartbeat (TR11).</summary>
    bool Poll(MissionTrigger trigger, MissionLine condition);

    /// <summary>True when the one test at the start of a trigger is true (TR8). The machine asks only for
    /// <c>Cnd_SystemEnter</c>.</summary>
    bool TestAtStart(MissionTrigger trigger, MissionLine condition);

    /// <summary>An action without flow ran, for example a spawn or a comm line.</summary>
    void Effect(MissionTrigger trigger, MissionLine action);

    /// <summary>True while the player is in space. A timer with the token <c>RUN_IN_SPACE_ONLY</c> counts only
    /// then.</summary>
    bool InSpace { get; }
}

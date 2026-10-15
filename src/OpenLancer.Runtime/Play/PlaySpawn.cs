using LibreLancer.Server;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// The heading of the ship after a spawn in space (decision D39). The server drops the heading of a save and spawns
/// the ship with the identity orientation. So the play turns the ship after the spawn, as missions do.
/// </summary>
public static class PlaySpawn
{
    /// <summary>
    /// Turns the ship of a player in space to the heading of a start in space. It does nothing for a start at a base,
    /// or when the player is not in space.
    /// </summary>
    /// <remarks>
    /// Call it after the client got the spawn. The server sends the spawn from its spawn action, before it spawns the
    /// ship. The turn queues a new action in the same world, and a world runs its actions in order. So the turn runs
    /// after the spawn action, when the ship exists. Before the spawn action, the turn would not find the ship, and
    /// the world thread would end the process.
    /// </remarks>
    /// <param name="start">The start of the save of the server: the start of a play, or the start of the place of a
    /// reload (<see cref="PlayPlace.Start"/>). In space, the ship spawned at its position and at rest, so the turn does
    /// not move it. The ship flies along its -Z axis.</param>
    public static void Orient(Player player, PlayStart start)
    {
        if (start is PlayStart.InSpace space)
            player.Space?.ForceMove(space.Position, space.Orientation);
    }
}

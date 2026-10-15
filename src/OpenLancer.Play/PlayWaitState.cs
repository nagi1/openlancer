using System;
using LibreLancer;
using LibreLancer.Client;
using LibreLancer.Server;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Play;

/// <summary>
/// Waits for the spawn of the player, as <c>NetWaitState</c> does, and tells the host where the player arrived
/// (section 10.4 of the M4 design). The session changes the state when the spawn or the base enter arrives. The game
/// unloads the state too when it exits during the wait. The start of a play and each reload use this state.
/// </summary>
internal sealed class PlayWaitState : NetWaitState
{
    private readonly CGameSession session;
    private readonly Player player;
    private readonly PlayStart start;
    private readonly Action<string?> arrived;
    private bool exiting;

    /// <param name="player">The player on the server.</param>
    /// <param name="start">The place that the save of the server has: the start of the play, or the start of the
    /// place of a reload (<see cref="PlayPlace.Start"/>).</param>
    /// <param name="arrived">Gets the base, or null in space. In space, the ship has its heading then.</param>
    public PlayWaitState(CGameSession session, FreelancerGame game, Player player, PlayStart start,
        Action<string?> arrived) : base(session, game)
    {
        this.session = session;
        this.player = player;
        this.start = start;
        this.arrived = arrived;
    }

    /// <summary>The game exits. The game calls this before <see cref="OnUnload"/>, and the player did not arrive.</summary>
    public override void Exiting()
    {
        exiting = true;
        base.Exiting();
    }

    protected override void OnUnload()
    {
        if (exiting)
            return;
        if (session.PlayerBase is { } dock)
        {
            arrived(dock);
            return;
        }

        // The client has the spawn, so the server ran its spawn action, and the ship exists for the turn (decision D39).
        PlaySpawn.Orient(player, start);
        arrived(null);
    }
}

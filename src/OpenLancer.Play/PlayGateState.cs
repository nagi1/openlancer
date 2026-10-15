using System;
using LibreLancer;

namespace OpenLancer.Play;

/// <summary>
/// Shows the splash image until the play can start (section 10.2 of the M4 design). The editor validates the save while
/// the game loads, and sends start when the validation passes (decision D43). So the player spawns only after a passed
/// validation.
/// </summary>
internal sealed class PlayGateState : PlaySplashState
{
    private readonly Func<bool> open;
    private readonly Action start;

    /// <param name="open">True when the play can start.</param>
    /// <param name="start">Starts the play. It changes the state.</param>
    public PlayGateState(FreelancerGame game, Func<bool> open, Action start) : base(game)
    {
        this.open = open;
        this.start = start;
    }

    public override void Update(double delta)
    {
        if (open())
            start();
    }
}

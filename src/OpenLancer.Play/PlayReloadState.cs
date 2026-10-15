using System;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Play;

/// <summary>
/// Shows the splash image while a reload loads its data on a task (section 12.2 of the M4 design). The server of the
/// play stopped before this state, so no state of the game uses it.
/// </summary>
internal sealed class PlayReloadState : PlaySplashState
{
    private readonly Task<PlayData> load;
    private readonly Action<Task<PlayData>> loaded;

    /// <param name="load">Loads the data of the reload. The state runs it on a task.</param>
    /// <param name="loaded">Gets the ended task, also a failed one, on the UI thread. It changes the state.</param>
    public PlayReloadState(FreelancerGame game, Func<PlayData> load, Action<Task<PlayData>> loaded) : base(game)
    {
        this.load = Task.Run(load);
        this.loaded = loaded;
    }

    public override void Update(double delta)
    {
        if (load.IsCompleted)
            loaded(load);
    }
}

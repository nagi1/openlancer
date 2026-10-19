using System;
using System.Threading.Tasks;
using LibreLancer;

namespace OpenLancer.Editor;

/// <summary>
/// One load of the engine data. It runs in the background and gives the action that ends the load on the UI thread. It
/// must not throw: it gives a failed load to the action that ends it.
/// </summary>
internal delegate Action DataLoad();

/// <summary>
/// Runs the loads of the engine data of one session in the background: the first load when the profile opens, and a
/// reload after each save that changed files. Each load fills the resources of the session, so only one load runs at a
/// time.
/// </summary>
/// <remarks>
/// A save can come while a load runs, for example a save of the restored edits of an earlier session during the first
/// load. The save writes its files at once, and its reload waits until the running load ends, as Unity imports the
/// assets of a save after the running import. More saves during one load give only one more reload, because a reload
/// reads the saved files when it starts.
/// </remarks>
internal sealed class DataLoads(IUIThread ui)
{
    private Func<DataLoad>? waitingReload;

    /// <summary>True while the first load runs.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>True while a reload runs.</summary>
    public bool IsReloading { get; private set; }

    /// <summary>True while the first load or a reload runs.</summary>
    public bool IsBusy => IsLoading || IsReloading;

    /// <summary>True when a reload waits for the end of the running load.</summary>
    public bool ReloadWaits => waitingReload != null;

    /// <summary>Starts the first load. Call it on the UI thread.</summary>
    /// <exception cref="InvalidOperationException">A load runs.</exception>
    public void Load(DataLoad load)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("The game data loads already.");
        }

        IsLoading = true;
        Run(load);
    }

    /// <summary>Starts a reload now, or after the running load. Call it on the UI thread.</summary>
    /// <param name="start">Starts the reload on the UI thread and gives its load. It reads the saved files when the
    /// reload starts, so a reload that waits reads the files of the last save.</param>
    /// <returns>True when the reload started. False when it waits for the running load.</returns>
    public bool Reload(Func<DataLoad> start)
    {
        if (IsBusy)
        {
            waitingReload = start;
            return false;
        }

        var load = start();
        IsReloading = true;
        Run(load);
        return true;
    }

    private void Run(DataLoad load) => Task.Run(() =>
    {
        var end = load();
        ui.QueueUIThread(() => Ended(end));
    });

    /// <summary>Ends a load on the UI thread, then starts the reload that waits for it.</summary>
    private void Ended(Action end)
    {
        IsLoading = false;
        IsReloading = false;
        end();
        if (waitingReload is { } start)
        {
            waitingReload = null;
            Reload(start);
        }
    }
}

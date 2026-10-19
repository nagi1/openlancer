using System;
using System.Collections.Generic;
using System.Threading;
using OpenLancer.Editor;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The loads of the engine data of a session, without a window (see <see cref="DataLoads"/>). Each load fills the same
/// resources of the session, so two loads must never run at the same time. The fake loads count the loads that use the
/// resources at the same time, and the queued UI thread runs the ends of the loads as the frames of the editor do.
/// </summary>
public sealed class DataLoadsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The resources of a session. They count the loads that use them at the same time.</summary>
    private sealed class Resources
    {
        private int users;
        private int most;

        /// <summary>The most loads that used the resources at the same time.</summary>
        public int Most => Volatile.Read(ref most);

        /// <summary>Uses the resources until <paramref name="until"/> is set, or for a moment when it is null.</summary>
        public void Use(ManualResetEventSlim? until)
        {
            var now = Interlocked.Increment(ref users);
            for (var seen = Volatile.Read(ref most); now > seen; seen = Volatile.Read(ref most))
            {
                Interlocked.CompareExchange(ref most, now, seen);
            }

            if (until == null)
            {
                Thread.Sleep(20);
            }
            else
            {
                Assert.True(until.Wait(Timeout), "The test did not end the load.");
            }

            Interlocked.Decrement(ref users);
        }
    }

    /// <summary>
    /// The race of fix S1: the user restores the unsaved edits of an earlier session at the open of the profile, then
    /// presses Ctrl+S before the first load of the game data ends. The save asks for a reload. The reload must wait for
    /// the first load, because both fill the same resources. A second save during the load gives no second reload.
    /// </summary>
    [Fact]
    public void ASaveDuringTheFirstLoadReloadsAfterTheLoadEnds()
    {
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        var resources = new Resources();
        var events = new List<string>();
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        loads.Load(() =>
        {
            running.Set();
            resources.Use(release);
            return () => events.Add("loaded");
        });
        Assert.True(running.Wait(Timeout, TestContext.Current.CancellationToken));

        var starts = 0;
        DataLoad StartReload()
        {
            var number = ++starts;
            events.Add($"reload {number} started");
            return () =>
            {
                resources.Use(null);
                return () => events.Add($"reload {number} loaded");
            };
        }

        // Two saves during the first load.
        Assert.False(loads.Reload(StartReload));
        Assert.False(loads.Reload(StartReload));

        Assert.True(loads.IsLoading);
        Assert.False(loads.IsReloading);
        Assert.True(loads.ReloadWaits);
        Assert.Equal(0, starts);

        release.Set();
        ui.RunUntil(() => events.Contains("reload 1 loaded"), "the reload after the first load");

        Assert.Equal(1, resources.Most);
        Assert.Equal(["loaded", "reload 1 started", "reload 1 loaded"], events);
        Assert.False(loads.IsBusy);
        Assert.False(loads.ReloadWaits);
    }

    /// <summary>A save during a reload gives one more reload after it, which reads the files of the last save.</summary>
    [Fact]
    public void ASaveDuringAReloadReloadsAgainAfterIt()
    {
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        var resources = new Resources();
        var events = new List<string>();
        using var release = new ManualResetEventSlim();
        loads.Load(() => () => events.Add("loaded"));
        ui.RunUntil(() => !loads.IsBusy, "the first load");

        Assert.True(loads.Reload(() =>
        {
            events.Add("reload 1 started");
            return () =>
            {
                resources.Use(release);
                return () => events.Add("reload 1 loaded");
            };
        }));
        Assert.False(loads.Reload(() =>
        {
            events.Add("reload 2 started");
            return () =>
            {
                resources.Use(null);
                return () => events.Add("reload 2 loaded");
            };
        }));
        Assert.True(loads.IsReloading);

        release.Set();
        ui.RunUntil(() => events.Contains("reload 2 loaded"), "the second reload");

        Assert.Equal(1, resources.Most);
        Assert.Equal(["loaded", "reload 1 started", "reload 1 loaded", "reload 2 started", "reload 2 loaded"], events);
        Assert.False(loads.IsBusy);
    }

    /// <summary>The first load starts one time. A second start while it runs is a bug of the caller.</summary>
    [Fact]
    public void TheFirstLoadDoesNotStartWhileALoadRuns()
    {
        var loads = new DataLoads(new QueuedUIThread());
        using var release = new ManualResetEventSlim();
        loads.Load(() =>
        {
            release.Wait(Timeout);
            return () => { };
        });

        Assert.Throws<InvalidOperationException>(() => loads.Load(() => () => { }));
        release.Set();
    }
}

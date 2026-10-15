using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Save;
using LibreLancer.Resources;
using LibreLancer.Server;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Local patch 0021 (section 7.3 of the M4 design): <c>GameServer.Stop</c> finishes each world, also a world that
/// loads during the stop. Before the patch, a stopped server kept the physics world of each world with its buffer
/// memory and its worker threads. In a Debug build, the finalizer of such a physics world fails an assert, and the
/// process ends. <c>apply-librelancer-patches.ps1 -Check</c> alone guards the other lines of the patch: the order of
/// <c>WorldReady</c> after the store, and the <c>Mouse_MouseUp</c> line of <c>SpaceGameplay</c>.
/// </summary>
public sealed class ServerStopRetailTests(ITestOutputHelper output)
{
    /// <summary>The limit from the start of the server to the spawn packet. The first load of Li01 is the slow part.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(30);

    /// <summary>A ship that no object of Li01 and no start at a base in Li01 uses.</summary>
    private const string HeldShip = "rh_elite";

    static ServerStopRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// After a space start in Li01, the buffer pool of the world has memory. After <c>Stop</c>, it has none, because
    /// the stop disposed the physics world.
    /// </summary>
    [Fact]
    public void AStoppedServerFreesThePhysicsOfItsWorld()
    {
        GameDataPaths.RequireBase();
        using var headless = new HeadlessServer(RetailGame.Instance.Data);
        var save = NewCharacter(headless, null, "li01");
        save.Player!.Position = new Vector3(-33270, 0, -25039);

        var clock = Stopwatch.StartNew();
        headless.Start(save);
        headless.WaitForPacket(HeadlessServer.SpawnPacket, StartLimit);
        output.WriteLine($"Spawn packet after {clock.ElapsedMilliseconds} ms.");
        var (world, before) = headless.OnWorld(w => (w, w.GameWorld.Physics!.BufferPool.GetTotalAllocatedByteCount()));
        var after = before;
        try
        {
            headless.Stop();
            after = world.GameWorld.Physics!.BufferPool.GetTotalAllocatedByteCount();
        }
        finally
        {
            // Without the patch, the world stays alive after the stop. Then the test finishes it, so that its finalizer
            // cannot end the test host at a later collection.
            if (after != 0)
                world.Finish();
        }

        output.WriteLine($"Buffer pool of {world.System.Nickname}: {before} bytes before Stop, {after} bytes after it.");
        Assert.Equal("li01", world.System.Nickname, ignoreCase: true);
        Assert.True(before > 0, "The world of the player has no physics memory.");
        Assert.Equal(0UL, after);
    }

    /// <summary>A start at a base loads no world, and its stop ends without an exception.</summary>
    [Fact]
    public void AServerWithThePlayerAtABaseStops()
    {
        GameDataPaths.RequireBase();
        using var headless = new HeadlessServer(RetailGame.Instance.Data);
        var save = NewCharacter(headless, "Li01_01_Base", "li01");

        var clock = Stopwatch.StartNew();
        headless.Start(save);
        headless.WaitForPacket(HeadlessServer.BaseEnterPacket, StartLimit);
        output.WriteLine($"Base enter packet after {clock.ElapsedMilliseconds} ms.");
        clock.Restart();
        headless.Stop();
        output.WriteLine($"Stop took {clock.ElapsedMilliseconds} ms.");

        Assert.Null(headless.Player.Space);
        Assert.Equal("Li01_01_Base", headless.Player.Base, ignoreCase: true);
    }

    /// <summary>
    /// A world whose load ends after the stop finishes at the end of its load. Its request does not give it to the
    /// caller, and the request then ends. The held resources stop the load of Li01 in its preload, after the world is
    /// made and before the provider stores it. So the stop comes during the load each time, not before the load and
    /// not after the store.
    /// </summary>
    [Fact]
    public void AWorldThatLoadsDuringTheStopFinishes()
    {
        GameDataPaths.RequireBase();
        var data = RetailGame.Instance.Data;
        var ship = data.Items.Ships.Get(HeldShip);
        Assert.NotNull(ship?.ModelFile?.ModelFile);
        using var headless = new HeadlessServer(data);
        var resources = new HeldResources(data, ship.ModelFile.ModelFile);
        headless.Server.Resources = resources;
        headless.Start(NewCharacter(headless, "Li01_01_Base", "li01"));
        headless.WaitForPacket(HeadlessServer.BaseEnterPacket, StartLimit);
        Assert.False(resources.IsHeld, "The base start read the model of the held ship.");

        var spunUp = new StrongBox<ServerWorld?>();
        var clock = Stopwatch.StartNew();
        WeakReference request;
        try
        {
            request = Request(headless.Server.Worlds, data.Items.Systems.Get("li01")!, spunUp,
                [new PreloadObject(PreloadType.Ship, ship.Nickname)]);
            Assert.True(resources.WaitUntilHeld(StartLimit), "The load of Li01 did not come to its preload.");
            output.WriteLine($"The load came to its preload after {clock.ElapsedMilliseconds} ms.");
            headless.Stop();
        }
        finally
        {
            resources.Release();
        }

        clock.Restart();
        var ended = WaitForCollection(request, HeadlessServer.WaitLimit);
        output.WriteLine(ended
            ? $"The request ended {clock.ElapsedMilliseconds} ms after the release."
            : $"The request still ran {clock.ElapsedMilliseconds} ms after the release.");
        var world = Volatile.Read(ref spunUp.Value);
        try
        {
            Assert.True(world is null, $"The request gave the world of {world?.System.Nickname} after the stop.");
            Assert.True(ended, "The request still runs after the stop.");
        }
        finally
        {
            // Without the patch, the world stays alive after the stop. Then the test finishes it, so that its finalizer
            // cannot end the test host at a later collection.
            world?.Finish();
        }
    }

    /// <summary>The multiplayer new-character save of the server, with another start place.</summary>
    private static SaveGame NewCharacter(HeadlessServer headless, string? baseNickname, string system)
    {
        var save = headless.Server.NewCharacter("OpenLancer", 0);
        save.Player!.Base = baseNickname;
        save.Player.System = system;
        return save;
    }

    /// <summary>
    /// Requests the world of <paramref name="system"/>. The world goes into <paramref name="spunUp"/>. The result is a
    /// weak reference to the callback of the request. Only the running request holds the callback, so the reference
    /// is dead when the request ended. The method is not inlined, so the frame of the test does not hold the callback.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Request(
        WorldProvider worlds, StarSystem system, StrongBox<ServerWorld?> spunUp, PreloadObject[] preloads)
    {
        Action<ServerWorld> callback = world => Volatile.Write(ref spunUp.Value, world);
        worlds.RequestWorld(system, callback, preloads);
        return new WeakReference(callback);
    }

    /// <summary>
    /// Collects the garbage until the target of <paramref name="reference"/> is dead, for at most
    /// <paramref name="limit"/>.
    /// </summary>
    private static bool WaitForCollection(WeakReference reference, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (reference.IsAlive)
        {
            if (clock.Elapsed > limit)
                return false;
            Thread.Sleep(10);
            GC.Collect();
        }
        return true;
    }

    /// <summary>
    /// The resources of a server whose world load stops after it reads one model, until the test releases it. The
    /// preload of a world reads its models after the world is made and before the provider stores the world.
    /// </summary>
    private sealed class HeldResources(GameDataManager data, string heldModel)
        : ServerResourceManager(data.Resources.ConvexCollection, data.VFS)
    {
        private readonly TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsHeld => held.Task.IsCompleted;

        public bool WaitUntilHeld(TimeSpan limit) => held.Task.Wait(limit);

        public void Release() => release.TrySetResult();

        public override ModelResource? GetDrawable(string? filename, MeshLoadMode loadMode = MeshLoadMode.GPU)
        {
            var model = base.GetDrawable(filename, loadMode);
            if (string.Equals(filename, heldModel, StringComparison.OrdinalIgnoreCase))
            {
                held.TrySetResult();
                // The limit keeps a failed test from holding the load forever.
                release.Task.Wait(HeadlessServer.WaitLimit);
            }
            return model;
        }
    }
}

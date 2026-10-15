using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Data.Schema.Save;
using LibreLancer.Net;
using LibreLancer.Server;
using OpenLancer.Runtime.Play;

namespace OpenLancer.DataTests;

/// <summary>
/// A game server without a window, with one local player on a <see cref="LocalPacketClient"/>. It builds what
/// <c>EmbeddedServer</c> builds. <c>EmbeddedServer</c> takes a <c>GameResourceManager</c>, which needs a window, so this
/// helper takes the convex shapes from the resource manager of the data. The saves and the scripts folder of the player
/// are in a temporary folder.
/// </summary>
/// <remarks>
/// No client reads the packet queue, so each packet of the server stays in it. A test waits for a packet by the name of
/// its generated class. A later rename of the class makes the wait time out, so the test fails and does not pass by
/// mistake.
/// </remarks>
internal sealed class HeadlessServer : IDisposable
{
    /// <summary>The spawn action of the server sends this packet first, then it spawns the ship.</summary>
    public const string SpawnPacket = "IClientPlayer_SpawnPlayerPacket";

    /// <summary>The server sends this packet when the player enters a base.</summary>
    public const string BaseEnterPacket = "IClientPlayer_BaseEnterPacket";

    /// <summary>The limit of a wait for one world action.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly string folder;
    private bool started;
    private bool stopped;

    public HeadlessServer(GameDataManager data)
    {
        folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Server = new GameServer(data, data.Resources.ConvexCollection)
        {
            ScriptsFolder = Path.Combine(folder, "scripts"),
            ThreadCount = 1,
        };
        Player = new Player(Client, Server, Guid.Empty) { SaveFolder = folder };
        Server.LocalPlayer = Player;
        Server.ConnectedPlayers.Add(Player);
    }

    public GameServer Server { get; }

    public LocalPacketClient Client { get; } = new();

    public Player Player { get; }

    /// <summary>Starts the server thread and queues the save, in the order of <c>EmbeddedServer.StartFromSave</c>.</summary>
    public void Start(SaveGame save)
    {
        Server.Start();
        started = true;
        Server.LoadSaveGame(save);
    }

    /// <summary>Starts the server with the autosave of a reload, as the play host does:
    /// <see cref="PlayReload.Resume"/>.</summary>
    public void Resume(PlayPlace place)
    {
        PlayReload.Resume(Server, place);
        started = true;
    }

    /// <summary>Saves the player, stops the server and gives the place, as the play host does:
    /// <see cref="PlayReload.Leave"/>. With local patch 0021, the stop frees the worlds.</summary>
    public PlayPlace Leave()
    {
        var place = PlayReload.Leave(Server);
        stopped = true;
        return place;
    }

    /// <summary>Waits until the client has a packet whose class has the name <paramref name="typeName"/>.</summary>
    /// <exception cref="TimeoutException">No such packet came in <paramref name="limit"/>.</exception>
    public void WaitForPacket(string typeName, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!Client.Packets.Any(p => p.GetType().Name == typeName))
        {
            if (clock.Elapsed > limit)
            {
                var names = Client.Packets.Select(p => p.GetType().Name).Distinct();
                throw new TimeoutException(
                    $"No packet {typeName} in {limit.TotalSeconds} s. The client has: {string.Join(", ", names)}.");
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> in the world of the player, on the thread of that world, and gives its result. The
    /// world runs its actions one at a time and in order. So an action that a test queues after the spawn packet runs
    /// after the spawn action. An exception of <paramref name="read"/> goes to the test. It must not escape on the
    /// thread of the world, because that thread has no catch and the process would end.
    /// </summary>
    /// <exception cref="TimeoutException">The world did not run the action in <see cref="WaitLimit"/>.</exception>
    public T OnWorld<T>(Func<ServerWorld, T> read)
    {
        var world = Player.Space?.World ?? throw new InvalidOperationException("The player is not in space.");
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.EnqueueAction(() =>
        {
            try
            {
                result.SetResult(read(world));
            }
            catch (Exception e)
            {
                result.SetException(e);
            }
        });
        return result.Task.WaitAsync(WaitLimit).GetAwaiter().GetResult();
    }

    /// <summary>Stops the server once. With local patch 0021, the stop frees the worlds.</summary>
    public void Stop()
    {
        if (!started || stopped)
            return;
        stopped = true;
        Server.Stop();
    }

    public void Dispose()
    {
        Stop();
        Directory.Delete(folder, true);
    }
}

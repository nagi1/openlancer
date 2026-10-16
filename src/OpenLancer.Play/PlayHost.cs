using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Threading;
using LibreLancer;
using LibreLancer.Client;
using LibreLancer.Data.IO;
using LibreLancer.Net;
using LibreLancer.Server;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Play;

/// <summary>
/// Runs the game of a play (sections 10.1 to 10.4 and 12.2 of the M4 design). The game reads its data through the
/// overlay of the play (OL-0020). After the load, it waits until the editor sends start. Then it starts the player
/// from a save in code. A reload loads the data again and continues at the same place.
/// </summary>
/// <remarks>
/// A thread reads the commands of the editor from the standard input and gives each one to the UI thread. The end of
/// the input exits the game, as stop does. The events go to the standard output.
/// </remarks>
internal sealed class PlayHost
{
    /// <summary>The component of the lines that the play process writes to the game log.</summary>
    public const string LogComponent = "Play";

    private readonly PlayArgs args;
    private readonly FreelancerGame game;

    /// <summary>The file system of the game for its whole life. The resource manager holds it, so a reload gives it the
    /// files of the new overlay.</summary>
    private readonly FileSystem files;

    // Only the UI thread uses these fields.
    private bool startReceived;
    private int exitCode = PlayExitCodes.Ok;

    /// <summary>The server of the current play: null before the start and during a reload.</summary>
    private EmbeddedServer? server;

    /// <summary>True when the player of the current play arrived in space or at a base. Only then can a reload save
    /// the player.</summary>
    private bool arrived;

    /// <summary>
    /// Makes the play folder and the game. The game uses the config of the play folder (decision D41), in a window
    /// and with the debug view.
    /// </summary>
    public PlayHost(PlayArgs args, ModProfile profile, ModOverlay overlay)
    {
        this.args = args;
        // The engine config and the save writer of the server make no folder.
        PlayFolder.Create(args.Profile);
        var config = GameConfig.Create(true, () => PlayFolder.ConfigPath(args.Profile));
        // A new config file has an empty freelancer_path, and the INI reader logs an error for that value at each
        // start. So the file keeps the base folder of the profile.
        if (config.FreelancerPath != profile.BaseFolder)
        {
            config.FreelancerPath = profile.BaseFolder;
            config.Save();
        }

        // The host does not save these values. The options of the game save the config, so the other values stay
        // between plays.
        config.Settings.FullScreen = false;
        config.Settings.Debug = true;
        // OL-0020: the game reads the base data and the mods through this one file system object.
        files = overlay.CreateFileSystem();
        config.CustomFileSystem = () => files;
        config.CustomState = FirstState;
        game = new FreelancerGame(config)
        {
            Title = args.ActiveMod == null
                ? $"OpenLancer Play - {profile.Name}"
                : $"OpenLancer Play - {profile.Name} [{args.ActiveMod}]",
        };
    }

    /// <summary>Writes an event for the editor on the standard output. <see cref="Console.Out"/> writes the line in
    /// one call, so an event line does not mix with a line of the engine log.</summary>
    public static void Send(PlayEvent e) => Console.Out.WriteLine(PlayProtocol.Write(e));

    /// <summary>Runs the game until it exits.</summary>
    /// <returns>The exit code of the process, or null when the engine returned before its first frame. The engine
    /// returns so when SDL, the game window or its graphics context does not start, and it writes the cause to the game
    /// log.</returns>
    public int? Run()
    {
        // A background thread, so that a reader that waits for a line does not keep the process after the game loop.
        var reader = new Thread(ReadCommands) { IsBackground = true, Name = "Play commands" };
        var firstFrame = false;
        // The reader starts in the first frame. Each command goes to the queue of the UI thread, and the queue sends an
        // SDL event. SDL must not get an event from another thread while it starts.
        game.QueueUIThread(() =>
        {
            firstFrame = true;
            reader.Start();
        });
        game.Run();
        return firstFrame ? exitCode : null;
    }

    /// <summary>Lets the game restore the display before the crash window shows, as lancer does.</summary>
    public void Crashed() => game.Crashed();

    /// <summary>Gives each command of the editor to the UI thread. The end of the input exits the game, so a game
    /// whose editor closed the input does not stay.</summary>
    private void ReadCommands()
    {
        while (Console.In.ReadLine() is { } line)
        {
            if (PlayProtocol.ReadCommand(line) is not { } command)
            {
                FLLog.Warning(LogComponent, $"The command '{line}' is not known.");
                continue;
            }

            game.QueueUIThread(() => Execute(command));
            // The game exits. The reader stops too, so that it queues no work while SDL stops.
            if (command.Kind == PlayCommandKind.Stop)
                return;
        }

        game.QueueUIThread(game.Exit);
    }

    private void Execute(PlayCommand command)
    {
        switch (command.Kind)
        {
            case PlayCommandKind.Start:
                // The gate starts the play at its next update. A start that comes before the gate waits for it.
                startReceived = true;
                break;
            case PlayCommandKind.Stop:
                game.Exit();
                break;
            case PlayCommandKind.Reload:
                Reload(command.Save);
                break;
        }
    }

    /// <summary>
    /// The first state after the load (<see cref="GameConfig.CustomState"/>). It does what <c>LuaMenu</c> does at its
    /// start, because a play does not open the main menu. Then it checks the start of the play and waits for the start
    /// command.
    /// </summary>
    private GameState FirstState(FreelancerGame _)
    {
        // SpaceGameplay reads the cursors and throws without them. Only RoomGameplay loads them itself.
        game.GameData.PopulateCursors();
        // The game states draw their own cursor, so the cursor of the system must not show on top of it.
        game.CursorKind = CursorKind.None;
        if (game.LoadTimer is { } timer)
        {
            timer.Stop();
            FLLog.Info("Game", $"Initial load took {timer.Elapsed.TotalSeconds} seconds");
            game.LoadTimer = null;
        }

        // The data is loaded, so the collector can use its low-latency mode now.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        var problem = PlaySave.Check(game.GameData.Items, args.Start, args.Package);
        if (problem == null)
        {
            Send(new PlayEvent.Waiting(SinceProcessStart()));
        }
        else
        {
            Send(new PlayEvent.Error(problem, Crash: false));
            exitCode = PlayExitCodes.Data;
            game.Exit();
        }

        return new PlayGateState(game, () => startReceived && problem == null, StartPlay);
    }

    /// <summary>
    /// Starts the server from the play save and waits for the spawn (section 10.3). This is the order of New Game in
    /// the main menu. The save is an object, not bytes, so the host starts the server and loads the save itself.
    /// </summary>
    private void StartPlay()
    {
        var clock = Stopwatch.StartNew();
        Begin(args.Start, gameServer =>
        {
            var save = PlaySave.Create(gameServer, args.Start, args.Package);
            gameServer.Start();
            gameServer.LoadSaveGame(save);
        }, (player, dock) => Send(Spawned(player, dock, clock.ElapsedMilliseconds)));
    }

    /// <summary>
    /// Reloads the data of the play from the disk and continues at the same place with the same heading (section 12.2).
    /// The server saves the player and stops. A task loads the new data while the game shows the splash image. Then the
    /// game gets the new data, and a new server loads the save.
    /// </summary>
    /// <remarks>
    /// <see cref="PlayReload.Apply"/> decides if the game gets the new data. When it keeps the old data, the game
    /// continues at the same place and sends reload-failed. When the game left the play, for example with Quit to
    /// menu, the host sends reload-failed and changes nothing. When the save or the stop of the server does not end in
    /// its limit, the host sends an error and ends the process with <see cref="PlayExitCodes.Timeout"/>, because a
    /// server thread that does not end cannot continue the play. Another exception of <see cref="PlayReload.Leave"/>
    /// goes to the crash report (section 6.6), because the server can be stopped then.
    /// </remarks>
    private void Reload(int save)
    {
        if (server == null || !arrived)
        {
            Send(new PlayEvent.ReloadFailed(save, "The game can reload only while the player is in space or at a base."));
            return;
        }

        if (PlayReload.CheckLeave(server.Server) is { } refusal)
        {
            Send(new PlayEvent.ReloadFailed(save, refusal));
            return;
        }

        var clock = Stopwatch.StartNew();
        PlayPlace place;
        try
        {
            place = PlayReload.Leave(server.Server);
        }
        catch (TimeoutException e)
        {
            FLLog.Error(LogComponent, e.Message);
            FLLog.Flush();
            Send(new PlayEvent.Error(e.Message, Crash: false));
            Environment.Exit(PlayExitCodes.Timeout);
            return;
        }

        // The server stopped. The host drops it, and the state change drops its session, so the old play can go.
        server = null;
        arrived = false;
        var target = new PlayGame(files, game.GameData, game.Ui.Data, game.Saves);
        game.ChangeState(new PlayReloadState(game, () => PlayReload.Load(args.Profile, args.ActiveMod),
            load => Continue(save, place, PlayReload.Apply(load, place, target), clock)));
    }

    /// <summary>Starts a new server at the place and waits for the player. Then it sends reloaded, or reload-failed when
    /// the game kept its old data.</summary>
    /// <param name="failure">The failure of <see cref="PlayReload.Apply"/>, or null when the game has the new
    /// data.</param>
    private void Continue(int save, PlayPlace place, Exception? failure, Stopwatch clock)
    {
        if (failure != null)
            FLLog.Error(LogComponent, $"The reload of save {save} failed, so the game keeps its data: {failure}");
        Begin(place.Start, gameServer => PlayReload.Resume(gameServer, place), (player, at) => Send(failure == null
            ? new PlayEvent.Reloaded(save, clock.ElapsedMilliseconds, player.System, at)
            : new PlayEvent.ReloadFailed(save, failure.Message)));
    }

    /// <summary>
    /// Makes the server and the session of a play, starts the server with a save and waits for the player. The new
    /// server becomes the server of the current play.
    /// </summary>
    /// <param name="start">The place of the save. In space, the ship gets the heading of the start after the
    /// spawn.</param>
    /// <param name="run">Starts the server and gives it the save.</param>
    /// <param name="arrival">Gets the player and the base, or null in space, when the player arrived.</param>
    private void Begin(PlayStart start, Action<GameServer> run, Action<Player, string?> arrival)
    {
        var play = new EmbeddedServer(game.GameData, game.ResourceManager, PlayFolder.SavesPath(args.Profile));
        var session = new CGameSession(game, play);
        run(play.Server);
        server = play;
        var player = play.Server.LocalPlayer!;
        game.ChangeState(new PlayWaitState(session, game, player, start, dock =>
        {
            arrived = true;
            arrival(player, dock);
        }));
    }

    /// <summary>
    /// The event of a spawn. In space, it gives the start position, not the live position of the ship, so its numbers
    /// are finite.
    /// </summary>
    /// <param name="dock">The base, or null in space.</param>
    private PlayEvent.Spawned Spawned(Player player, string? dock, long startMs)
    {
        // The server loaded the character before it sent the spawn, and PlaySave.Check made sure of a ship.
        var ship = player.Character!.Ship!;
        Vector3? position = dock == null && args.Start is PlayStart.InSpace space ? space.Position : null;
        return new PlayEvent.Spawned(player.System, dock, position, ship.Nickname,
            game.GameData.GetString(ship.IdsName), startMs);
    }

    private static long SinceProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return (long)(DateTime.Now - process.StartTime).TotalMilliseconds;
    }
}

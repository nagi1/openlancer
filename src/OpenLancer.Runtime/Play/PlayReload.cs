using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema.Save;
using LibreLancer.Interface;
using LibreLancer.Server;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// Where the player was when a reload left the play. A save does not keep the heading of the ship (section 5.2 of the
/// M4 design), so the place keeps it.
/// </summary>
/// <param name="Save">The autosave of the player, which the new server loads.</param>
/// <param name="System">The system of the player.</param>
/// <param name="Base">The base of the player, or null in space.</param>
/// <param name="Position">The position of the ship when the server stopped. The turn after the spawn also moves the
/// ship there.</param>
/// <param name="Orientation">The heading of the ship.</param>
public sealed record PlayPlace(SaveGame Save, string System, string? Base, Vector3 Position, Quaternion Orientation)
{
    /// <summary>
    /// The start of the new server at this place: docked at the base, or in space at the position with the heading.
    /// After the spawn, <see cref="PlaySpawn.Orient"/> turns the ship to the heading of this start.
    /// </summary>
    public PlayStart Start => Base is { } dock
        ? new PlayStart.AtBase(dock)
        : new PlayStart.InSpace(System, Position, Orientation);
}

/// <summary>The data of a reload: the overlay of the mod files on the disk now, the game items of that overlay, and
/// the navmap icons of those items for the UI.</summary>
public sealed record PlayData(ModOverlay Overlay, GameItemDb Items, INavmapIcons NavmapIcons);

/// <summary>The parts of the game that a reload gives the new data.</summary>
/// <param name="Files">The file system that the game got at its start. The resource manager holds it.</param>
/// <param name="Data">The game data.</param>
/// <param name="Ui">The data of the Lua UI. It keeps copies of the texts and other tables of the items.</param>
/// <param name="Saves">The save list of the main menu. It keeps a copy of the texts of the items.</param>
public sealed record PlayGame(FileSystem Files, GameDataManager Data, UiData Ui, SaveGameFolder Saves);

/// <summary>
/// The core of a hot reload (section 12.2 of the M4 design), without a window. The play host calls these steps in
/// order, and the retail tests call the same steps on a server without a window: <see cref="CheckLeave"/> and
/// <see cref="Leave"/> on the UI thread, <see cref="Load"/> on a task, then <see cref="Apply"/> and
/// <see cref="Resume"/> on the UI thread. <see cref="Apply"/> decides if the game gets the new data or keeps its old
/// data. In both cases, <see cref="Resume"/> continues at the place.
/// </summary>
/// <remarks>
/// The game keeps the file system object of its start: the resource manager holds it and cannot take another one. So
/// the reload gives that object the files of the new overlay (<see cref="ModOverlay.Replace"/>), and the game data gets
/// new items. A new server takes its base prices from the new items when it starts.
/// </remarks>
public static class PlayReload
{
    /// <summary>The limit of each blocking wait of <see cref="Leave"/> (decision D53).</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>The text id of the description of an autosave, as the engine gives it.</summary>
    private const int AutoSaveIds = 1628;

    /// <summary>Tells if <see cref="Leave"/> can save the player. <see cref="Leave"/> runs this check first.</summary>
    /// <returns>Null when the server can save the player. Else the reason: the server does not run, or the player has
    /// no character yet.</returns>
    public static string? CheckLeave(GameServer server)
    {
        // The server thread makes the database when it starts and drops it when it ends. A server that does not run never
        // writes the save. This is the case when the game left the play, for example with Quit to menu or Load game: the
        // session of the play stopped its server.
        if (server.Database is null)
            return "The server of the play does not run, because the game left the play. Stop the game and press Play.";
        if (server.LocalPlayer is not { } player)
            return "The server has no local player.";
        // The save action of the server thread needs the character. Without it, the action throws on the server thread,
        // which has no catch, and the process ends.
        return player.Character == null ? "The player has no character yet, so the server cannot save it." : null;
    }

    /// <summary>
    /// Saves <c>AutoSave.fl</c> into the save folder of the player, stops the server and gives the place of the
    /// player. With local patch 0021, the stop frees the worlds of the server.
    /// </summary>
    /// <remarks>
    /// The server thread writes the save at its next update. The stop runs on a task, so that a server thread that
    /// does not end cannot hold the caller past the limit. After the stop, no thread writes the place of the player.
    /// Another exception than the two below comes from the save, the stop or the read of the save. The server can be
    /// stopped then, so the play cannot continue.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><see cref="CheckLeave"/> gives a reason. Nothing changed.</exception>
    /// <exception cref="TimeoutException">The save or the stop did not end in <see cref="WaitLimit"/>.</exception>
    public static PlayPlace Leave(GameServer server)
    {
        if (CheckLeave(server) is { } reason)
            throw new InvalidOperationException(reason);
        var player = server.LocalPlayer!;
        var save = player.SaveSP(null, AutoSaveIds, true, DateTime.Now);
        Wait(save, "The autosave of the reload");
        Wait(Task.Run(server.Stop), "The stop of the server");
        var file = save.Result;
        return new PlayPlace(SaveGame.FromBytes(file, File.ReadAllBytes(file)), player.System, player.Base,
            player.Position, player.Orientation);
    }

    /// <summary>
    /// Loads the data that the files on the disk give now: the play view of the profile (decision D38), its game
    /// items, and the navmap icons of the items. Call it on a task.
    /// </summary>
    /// <param name="activeModId">The active mod, or null for all mods of the profile.</param>
    /// <exception cref="ModLoadException">A mod is not valid, a save of a mod is not complete, or the mods have merge
    /// errors.</exception>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    public static PlayData Load(string profilePath, string? activeModId)
    {
        var profile = ModProfile.Load(profilePath);
        var overlay = PlayView.Build(profile, activeModId);
        if (PlayView.MergeErrors(overlay) is { } errors)
            throw new ModLoadException(profile.FilePath, errors);
        var items = new GameItemDb(overlay.CreateFileSystem());
        items.LoadData();
        // The UI makes its navmap icons in the same way (UiData.cs:56-63). The constructor throws for a navmap file
        // without the icons that the navmap needs, so the load fails, and the game keeps its data.
        INavmapIcons icons = items.Ini.Navmap != null ? new IniNavmapIcons(items.Ini.Navmap) : new NavmapIcons();
        return new PlayData(overlay, items, icons);
    }

    /// <summary>
    /// Gives the game the data of a reload, or keeps the old data. Call it on the UI thread when the task of
    /// <see cref="Load"/> ended, while no server runs.
    /// </summary>
    /// <remarks>
    /// <para>The game keeps its old data, and nothing changes, in these cases:</para>
    /// <list type="bullet">
    /// <item>The load failed.</item>
    /// <item>The new data does not have the system, the base or the ship that the autosave of the place names. The new
    /// server would throw on its thread, which has no catch, and the game would end. For example, a rename in the
    /// Database gives the ship of the player a new nickname.</item>
    /// <item>The file system of the game does not show an overlay of the base folder of the new data: the profile names
    /// another base folder now.</item>
    /// </list>
    /// <para>Else the file system shows the files of the new overlay, and the game data has the new items. The UI and the
    /// save list show the texts and the other tables of the new items, because the UI copied them from the items at its
    /// start (UiData.cs:51-64). The Lua UI reads its texts through <see cref="UiData.Infocards"/>, for example the names
    /// of the goods of a shop.</para>
    /// <para>The autosave of the place then gets the lines that the new server can load (<see cref="PlaySave.Equip"/>).
    /// When the new data makes a mounted good combinable, its <c>equip</c> line becomes a <c>cargo</c> line. Else the
    /// new server would throw on its thread at the load of the save, and the game would end.</para>
    /// </remarks>
    /// <param name="load">The ended task of <see cref="Load"/>.</param>
    /// <param name="place">The place of <see cref="Leave"/>, which the new server loads.</param>
    /// <returns>Null when the game has the new data. Else the failure: the exception of the load, or an
    /// <see cref="InvalidOperationException"/> with the reason.</returns>
    public static Exception? Apply(Task<PlayData> load, PlayPlace place, PlayGame game)
    {
        if (load.Exception is { } failure)
            return failure.GetBaseException();
        var data = load.Result;
        if (PlaceProblem(data.Items, place.Save) is { } problem)
            return new InvalidOperationException($"The new data cannot load the autosave of the player. {problem}");
        try
        {
            data.Overlay.Replace(game.Files);
        }
        catch (InvalidOperationException e)
        {
            // The profile names another base folder now. Replace changed nothing.
            return e;
        }

        game.Data.Items = data.Items;
        var ini = data.Items.Ini;
        game.Ui.Infocards = ini.Infocards;
        game.Ui.DataPath = ini.Freelancer.DataPath;
        game.Ui.NavbarIcons = game.Data.GetBaseNavbarIcons();
        game.Ui.NavmapIcons = data.NavmapIcons;
        game.Ui.RolloverMap = ini.Rollover.Map;
        game.Saves.Infocards = ini.Infocards;
        // The new data can make a mounted good combinable, and the new server cannot mount such a good (decision D56).
        PlaySave.Equip(place.Save.Player!, null, data.Items.Equipment);
        return null;
    }

    /// <summary>
    /// Starts a new server and loads the autosave of the place, in the order of <c>EmbeddedServer.StartFromSave</c>.
    /// The player then spawns at the place. The engine spawns a ship from a save with the identity orientation, so turn
    /// it with <see cref="PlaySpawn.Orient"/> and <see cref="PlayPlace.Start"/> after the spawn.
    /// </summary>
    public static void Resume(GameServer server, PlayPlace place)
    {
        server.Start();
        server.LoadSaveGame(place.Save);
    }

    /// <summary>
    /// Finds what the server reads from a save and the items do not have: the system, the base and the ship
    /// (NetCharacter.cs:422, :426, Player.cs:860-861, :905).
    /// </summary>
    private static string? PlaceProblem(GameItemDb items, SaveGame save)
    {
        var player = save.Player!;
        var ship = player.ShipArchetype;
        return PlaySave.SystemProblem(items, player.System!) ??
               (player.Base is { } dock ? PlaySave.BaseProblem(items, dock) : null) ??
               (items.Ships.Get(ship) == null
                   ? $"The ship '{ship.String ?? $"{ship.Hash}"}' is not in the game data."
                   : null);
    }

    /// <exception cref="TimeoutException">The task did not end in <see cref="WaitLimit"/>.</exception>
    private static void Wait(Task task, string what)
    {
        try
        {
            task.WaitAsync(WaitLimit).GetAwaiter().GetResult();
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            throw new TimeoutException($"{what} did not end in {WaitLimit.TotalSeconds:0} s.");
        }
    }
}

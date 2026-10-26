using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor;

/// <summary>
/// Play in the editor (section 9 of the M4 design): the game of Play, its ship, and the seam of Play (decision D50). Play
/// in the game and the test server of milestone M11 use the same seam (<see cref="IGamePlayHost"/>). The members of the
/// seam delegate to the session, the edits and the Console. They are explicit, so the public surface of the window does
/// not grow. The mission play of the Mission tab (section 5 of the M6c design) reads the window through
/// <see cref="IMissionPlayEditor"/>.
/// </summary>
internal sealed partial class EditorWindow : IPlayEditor, IGamePlayHost, IMissionPlayEditor
{
    // The clock of the watchdogs of Play and of the time from Play to flight.
    private readonly Stopwatch playClock = Stopwatch.StartNew();

    /// <summary>The game of Play: its start, its state and its stop.</summary>
    public PlayController Play { get; }

    /// <summary>Play in the game and the test server (milestone M11).</summary>
    public GamePlay GamePlay { get; }

    /// <summary>
    /// Makes a ship package the ship of Play for the open profile (decision D15): the row action Use for Play of the
    /// Goods table, and Use the default ship of the Play menu. The next play uses it.
    /// </summary>
    /// <param name="package">The nickname of the package, or null for the package of a new multiplayer
    /// character.</param>
    public void UseForPlay(string? package)
    {
        if (Session is not { } session)
        {
            return;
        }

        Settings.SetPlayPackage(session.Profile.FilePath, package);
        Settings.Save();
        var ship = PlayShips.ShipName(session, package);
        Log.Add(ConsoleSeverity.Info, PlayController.Source, package == null
            ? $"Play uses the default ship of a new character{(ship == null ? "" : $": {ship}")}."
            : $"Play uses the ship package {package}{(ship == null ? "" : $": {ship}")}.");
    }

    ConsoleLog IPlayHost.Log => Log;

    PlaySession? IPlayHost.Session => Session is { GameData: not null } session
        ? PlaySession.Of(session.Edits, session.Saves, Edits.IncompleteSave, session.NoEditReason)
        : null;

    SaveOutcome IPlayHost.Save() => Edits.Save();

    ValidationState? IPlayHost.Validation => Edits.Validation;

    event Action<ValidationState>? IPlayHost.Validated
    {
        add => Edits.Validated += value;
        remove => Edits.Validated -= value;
    }

    void IPlayHost.ValidateNow() => Edits.ValidateNow();

    string IPlayEditor.SystemName(string nickname) =>
        Session?.GameData?.Items.Systems.Get(nickname) is { } system ? Session.ItemName(system) : "";

    string IPlayEditor.BaseName(string nickname) =>
        Session?.GameData?.Items.Bases.Get(nickname) is { } dock ? Session.ItemName(dock) : "";

    bool IPlayEditor.SceneBehind => Scene.IsBehind;

    IReadOnlyList<SpawnShape>? IPlayEditor.SpawnShapes(string system) => Scene.SpawnShapes(system);

    void IPlayEditor.MissionChanged(PlayEvent.MissionChanges changes) => Missions.MissionChanged(changes);

    void IGamePlayHost.Ask(string title, string message, IReadOnlyList<Choice> choices) =>
        Choice.Show(title, message, choices);

    void IGamePlayHost.ChooseFolder(Action<string> chosen) => FolderActions.Choose(this, chosen);

    IReadOnlyList<string> IGamePlayHost.ProtectedFolders() =>
        PlayCopy.ProtectedFolders(Settings.WorkspaceOrDefault, Session?.Profile);

    string? IMissionPlayEditor.ProfilePath => Session?.Profile.FilePath;

    EditorSettings IMissionPlayEditor.Settings => Settings;

    ConsoleLog IMissionPlayEditor.Log => Log;

    MissionIndex? IMissionPlayEditor.IndexOf(string script) => Missions.Analysis(script)?.Index;

    (MissionIndex Index, StorySlot? Slot)? IMissionPlayEditor.ModelOf(string script, out string? problem)
    {
        problem = null;
        if (Session?.Edits is not { } edits)
        {
            problem = "No profile is open.";
            return null;
        }

        try
        {
            var model = edits.Missions.Model(script);
            return (model.Index, model.Analysis.Script.Slot);
        }
        catch (EditException e)
        {
            problem = e.Error.Message;
            return null;
        }
    }

    StartSavePlace? IMissionPlayEditor.PlaceOf(StartPlace place, out string? problem)
    {
        problem = null;
        if (Session?.GameData?.Items is not { } items)
        {
            problem = "The game data of the profile did not load.";
            return null;
        }

        switch (place.Kind)
        {
            case StartPlaceKind.Space when items.Systems.Get(place.System!) is { } system:
                return new StartSavePlace(system.Nickname, null, place.Position, place.Orientation);
            case StartPlaceKind.Space:
                problem = $"The start place names the system '{place.System}', which is not in the game data. Pick a base " +
                          "or the Scene camera on the Play start page.";
                return null;
            case StartPlaceKind.Base when items.Bases.Get(place.Base!) is { System: { } baseSystem } dock:
                return StartSavePlace.AtBase(baseSystem, dock.Nickname);
            case StartPlaceKind.Base:
                problem = $"The start place names the base '{place.Base}', which is not in the game data. Pick another " +
                          "base on the Play start page.";
                return null;
        }

        // The Scene camera, with the start check of Play from camera (section 9.5 of the M4 design).
        if (Scene.System is not { } open || Scene.SpawnShapes(open.Nickname) is not { } shapes)
        {
            problem = "The start place is the Scene camera, and no system is open. Open a system, or pick another start " +
                      "place on the Play start page.";
            return null;
        }

        var check = SpawnPoint.FromCamera(new PlayStart.InSpace(open.Nickname, Scene.CameraPosition, Scene.CameraOrientation),
            shapes);
        foreach (var (severity, message) in check.Lines)
        {
            Log.Add(severity, PlayController.Source, message);
        }

        return new StartSavePlace(check.Start.System, null, check.Start.Position, check.Start.Orientation);
    }

    string? IMissionPlayEditor.PlayPackage => Session is { } session ? Settings.PlayPackageOf(session.Profile.FilePath) : null;

    PlayStatus IMissionPlayEditor.PlayStatus => Play.Status;

    IMissionStart? IMissionPlayEditor.RunningMission => Play.Mission;

    bool IMissionPlayEditor.GameRuns => GamePlay.GameRuns;

    Task<PlayRequestResult> IMissionPlayEditor.RequestMission(IMissionStart start, string? package) =>
        Play.RequestMission(start, package);

    string? IAgentPlayHost.UnsavedMod => Session is { Edits: { IsDirty: true, ActiveMod: { } mod } } ? mod.Id : null;

    bool IAgentPlayHost.AutoSaveAgentPlay => Settings.Agent.AutoSavePlay;

    void IAgentPlayHost.Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel) =>
        Choice.Show(title, message, choices, cancel);
}

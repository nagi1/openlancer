using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;

namespace OpenLancer.Editor;

/// <summary>
/// Play in the editor (section 9 of the M4 design): the game of Play, its ship, and the seam of Play (decision D50). Play
/// in the game and the test server of milestone M11 use the same seam (<see cref="IGamePlayHost"/>). The members of the
/// seam delegate to the session, the edits and the Console. They are explicit, so the public surface of the window does
/// not grow.
/// </summary>
internal sealed partial class EditorWindow : IPlayEditor, IGamePlayHost
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

    void IGamePlayHost.Ask(string title, string message, IReadOnlyList<Choice> choices) =>
        Choice.Show(title, message, choices);

    void IGamePlayHost.ChooseFolder(Action<string> chosen) => FolderActions.Choose(this, chosen);

    IReadOnlyList<string> IGamePlayHost.ProtectedFolders() =>
        PlayCopy.ProtectedFolders(Settings.WorkspaceOrDefault, Session?.Profile);
}

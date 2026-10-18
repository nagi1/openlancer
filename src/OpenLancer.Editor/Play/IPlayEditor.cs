using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor.Play;

/// <summary>
/// What Play needs from the editor (section 9.12 of the M4 design, decision D50). <see cref="EditorWindow"/> implements
/// it. The tests give a fake, because an editor window needs a GPU and reads and writes the real settings file.
/// </summary>
/// <remarks>Each member runs on the UI thread.</remarks>
internal interface IPlayEditor : IUIThread
{
    ConsoleLog Log { get; }

    /// <summary>The open profile, or null without an open profile with loaded game data.</summary>
    PlaySession? Session { get; }

    /// <summary>Saves the edits of the active mod for Play, with all checks of a save (section 9.2).</summary>
    SaveOutcome Save();

    /// <summary>The state of the last validation of the session, or null.</summary>
    ValidationState? Validation { get; }

    /// <summary>Runs on the UI thread when a validation of the session gives its result.</summary>
    event Action<ValidationState>? Validated;

    /// <summary>Validates the saved files as they are now, with the number of <see cref="PlaySession.Saves"/>. Play does
    /// not call it for a save that is not complete (decision D59).</summary>
    void ValidateNow();

    /// <summary>The name of a system, or an empty text when the game data has no name for it.</summary>
    string SystemName(string nickname);

    /// <summary>The name of a base, or an empty text when the game data has no name for it.</summary>
    string BaseName(string nickname);

    /// <summary>
    /// True while the Scene does not show each change of the edits. The Scene applies them at its next flush, at the
    /// start of its next frame (section 3 of the M2 design). For example, an Inspector field that a click on Play ends
    /// commits its edit after the flush of that frame.
    /// </summary>
    bool SceneBehind { get; }

    /// <summary>The shapes of a system for the start of Play from camera (see <see cref="SpawnPoint.Shapes"/>), as the
    /// Scene shows them. Null when the Scene does not show that system.</summary>
    /// <param name="system">The nickname of the system.</param>
    IReadOnlyList<SpawnShape>? SpawnShapes(string system);
}

/// <summary>What Play reads of the open profile when it starts a game.</summary>
/// <param name="ProfilePath">The full path of the profile file.</param>
/// <param name="ActiveMod">The id of the active mod, or null when no mod is active.</param>
/// <param name="Saves">The number of the last save of the session that changed files, or 0.</param>
/// <param name="IncompleteSave">The number of a save of the session that is not complete, or 0. The save journal
/// holds it, so the files of the mod are a mix of two saves (decision D59).</param>
/// <param name="NoEditReason">Why the editor takes no edits for the active mod now, or null.</param>
/// <param name="LaterMods">The ids of the mods that load after the active mod. Play does not include them (decision
/// D38).</param>
/// <param name="BaseFolder">The base folder of the profile, or null when it is not known. Play does not start when the
/// profile file is in it (decision D214).</param>
internal sealed record PlaySession(string ProfilePath, string? ActiveMod, int Saves, int IncompleteSave,
    string? NoEditReason, IReadOnlyList<string> LaterMods, string? BaseFolder = null)
{
    /// <summary>The snapshot of an open profile. The edit session gives the profile and the mods. The editor gives the
    /// other values, because the edit session does not keep them.</summary>
    public static PlaySession Of(EditSession edits, int saves, int incompleteSave, string? noEditReason) =>
        new(edits.Profile.FilePath, edits.ActiveMod?.Id, saves, incompleteSave, noEditReason,
            edits.LaterMods.Select(m => m.Id).ToList(), edits.Profile.BaseFolder);
}

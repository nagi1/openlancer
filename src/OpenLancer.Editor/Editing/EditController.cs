using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// Runs the edits of the open profile and the saves, and what follows a save: the Console report, the reload of the
/// game data and the validation. It also keeps the recovery file of the unsaved edits up to date (decision D8).
/// </summary>
/// <remarks>
/// <para>A save writes the files of the active mod at once. Then the game data loads again in the background from a
/// new file system of the edit view (review finding F5, roadmap rule R5). One reload runs at a time, and a save
/// during a reload queues one more. The validation runs in an oltool process (see <see cref="ValidationRunner"/>).</para>
/// <para>Without local patch 0017, the editor cannot stop the window from closing. So the editor writes the unsaved
/// edits to the recovery file a moment after each edit, and when the window closes.</para>
/// </remarks>
internal sealed class EditController(EditorWindow window) : IDisposable
{
    public const string Source = "Edit";
    private const string ValidationSource = "Validation";

    /// <summary>The time without edits before the editor writes the recovery file.</summary>
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(2);

    private readonly ValidationRunner validation = new();
    private bool reloadQueued;

    // The number of a save of the session that is not complete, or 0. The next save completes it first.
    private int incompleteSave;

    private ConsoleLog Log => window.Log;

    /// <summary>
    /// Runs an edit as one entry of the edit history. When the editor takes no edits, or the edit does not apply,
    /// the Console tells why and the result is false.
    /// </summary>
    public bool Execute(EditCommand command)
    {
        if (window.Session is not { } session)
        {
            return false;
        }

        if (session.NoEditReason is { } reason)
        {
            Log.Add(ConsoleSeverity.Warning, Source, reason);
            return false;
        }

        try
        {
            session.Edits.Execute(command);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Log.Add(ConsoleSeverity.Error, Source, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Makes an edit and runs it, for an edit that reads the edit view when it is made, for example a new item from a
    /// template. When the edit cannot be made, the Console tells why and the result is false.
    /// </summary>
    public bool Execute(Func<EditCommand> make)
    {
        if (window.Session?.NoEditReason is { } reason)
        {
            Log.Add(ConsoleSeverity.Warning, Source, reason);
            return false;
        }

        EditCommand command;
        try
        {
            command = make();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Log.Add(ConsoleSeverity.Error, Source, e.Message);
            return false;
        }

        return Execute(command);
    }

    public void Undo() => Step(session => session.Edits.Undo());

    public void Redo() => Step(session => session.Edits.Redo());

    private void Step(Action<EditorSession> step)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        if (session.NoEditReason is { } reason)
        {
            Log.Add(ConsoleSeverity.Warning, Source, reason);
            return;
        }

        try
        {
            step(session);
        }
        catch (InvalidOperationException e)
        {
            Log.Add(ConsoleSeverity.Warning, Source, e.Message);
        }
    }

    /// <summary>
    /// Saves the edits of the active mod and writes the report to the Console. A save that changed files starts the
    /// reload of the game data and the validation. When files of the mod changed outside the editor, a dialog asks to
    /// overwrite them.
    /// </summary>
    public void Save() => Save(overwrite: false, leave: null);

    /// <param name="overwrite">True to replace the files of the active mod that changed outside the editor.</param>
    /// <param name="leave">The action that ends the session after the save, for example a close of the profile, or
    /// null. It runs when the save leaves no unsaved edits, also after the Overwrite dialog. The game data then does
    /// not load again, and no validation runs.</param>
    private void Save(bool overwrite, Action? leave)
    {
        if (window.Session is not { Edits.ActiveMod: { } mod } session)
        {
            return;
        }

        // A mod in the OpenLancer repo needs the confirmation of the session before a save (decision D5).
        if (session.NoEditReason is { } reason)
        {
            Log.Add(ConsoleSeverity.Warning, Source, reason);
            return;
        }

        // The oltool process of an earlier validation reads the files of the mod. So it stops before a save of the edits
        // moves files, because a file that another process has open cannot move (review finding F16).
        var stopped = session.Edits.IsDirty ? StopValidation() : null;
        SaveResult result;
        try
        {
            result = session.Edits.Save(overwrite);
        }
        catch (InvalidOperationException e)
        {
            Log.Add(ConsoleSeverity.Warning, Source, e.Message);
            return;
        }

        var changedFiles = result.Saved && result.Written.Count + result.Deleted.Count > 0;
        var number = result.Saved ? session.Saved(changedFiles) : 0;
        foreach (var line in SaveReport.Lines(result, mod.Id, mod.Folder, number))
        {
            Log.Add(line.Severity, line.Source, line.Message, line.Target);
        }

        if (!result.Saved && result.ChangedOnDisk.Count > 0)
        {
            // Cancel is the first choice, so Enter does not discard the changes of the files.
            var files = string.Join("\n", result.ChangedOnDisk.Select(f => Path.GetRelativePath(mod.Folder, f)));
            window.Choice.Show("Files changed outside the editor",
                $"Something outside the editor changed these files of mod '{mod.Id}' after the editor read them:\n\n" +
                $"{files}\n\nReplace them with the edits? Their changes are then lost.",
                [new Choice("Cancel", () => { }), new Choice("Overwrite", () => Save(overwrite: true, leave))]);
        }

        if (result.Saved && leave != null)
        {
            leave();
            return;
        }

        FollowUp(session, result, number, stopped);
    }

    /// <summary>
    /// Starts the reload of the game data and the validation after a save that changed files. A save that is not
    /// complete starts neither, because the files of the mod are not all in place. The next save completes it first,
    /// and then they start.
    /// </summary>
    /// <param name="number">The number of the save, or 0 when it changed no file.</param>
    /// <param name="stopped">The save of the validation that stopped before the save, or null.</param>
    private void FollowUp(EditorSession session, SaveResult result, int number, int? stopped)
    {
        if (result.Incomplete)
        {
            incompleteSave = number;
            Log.Add(ConsoleSeverity.Warning, Source,
                $"Save {number} is not complete. The game data does not load again, and the validation does not run. " +
                "Correct the problem of the file, then save again.");
            return;
        }

        // A save after an incomplete save completed the files of that save first.
        var completed = result.Saved ? incompleteSave : 0;
        if (result.Saved)
        {
            incompleteSave = 0;
        }

        if (number == 0 && completed > 0)
        {
            Log.Add(ConsoleSeverity.Info, Source, $"Save {completed} is complete now.");
        }

        var changed = number > 0 ? number : completed;
        if (changed > 0)
        {
            Reload(session);
            Validate(session, changed);
        }
        else if (stopped is { } save)
        {
            // The save changed no file, so the result of the stopped validation is not old.
            Validate(session, save);
        }
    }

    /// <summary>
    /// Runs an action that ends the edits of the session, for example a close of the profile. With unsaved edits, a
    /// dialog asks first: Save, Discard or Cancel.
    /// </summary>
    /// <param name="what">The action in the question, for example "closing the profile".</param>
    public void Leave(string what, Action action)
    {
        if (window.Session is not { Edits: { IsDirty: true, ActiveMod: { } mod } } session)
        {
            action();
            return;
        }

        window.Choice.Show("Unsaved edits", $"The mod '{mod.Id}' has unsaved edits. Save them before {what}?",
        [
            new Choice("Save", () => Save(overwrite: false, leave: action)),
            new Choice("Discard", () =>
            {
                Discard(session);
                action();
            }),
            new Choice("Cancel", () => { }),
        ]);
    }

    /// <summary>Shows the dialog for the recovery file of an earlier session, when the active mod has one.</summary>
    public void OfferRecovery()
    {
        if (window.Session is not { Edits: { HasRecovery: true, ActiveMod: { } mod } } session)
        {
            return;
        }

        // A restore is an edit of the mod. For a mod in the OpenLancer repo, the restore is also the one confirmation
        // that each session needs before an edit (decision D5).
        var repo = session.NeedsRepoConfirmation
            ? " The mod is part of the OpenLancer repo, so its files are tracked files. A restore also allows the edits " +
              "of the mod for this session."
            : "";
        window.Choice.Show("Unsaved edits of an earlier session",
            $"An earlier session of the editor closed with unsaved edits of mod '{mod.Id}'. Restore them? Until you " +
            "restore or discard them, the editor takes no edits for the mod." + repo,
        [
            new Choice("Restore", () => Restore(session)),
            new Choice("Discard", () => Discard(session)),
            new Choice("Later", () => { }),
        ]);
    }

    private void Restore(EditorSession session)
    {
        var problems = session.Edits.RestoreRecovery();
        foreach (var problem in problems)
        {
            Log.Add(problem);
        }

        if (problems.Count == 0)
        {
            Log.Add(ConsoleSeverity.Info, Source, "Restored the unsaved edits of the earlier session. Save them to keep them.");
            if (session.NeedsRepoConfirmation)
            {
                session.RepoEditsAllowed = true;
                Log.Add(ConsoleSeverity.Warning, Source,
                    $"The restore allowed the edits of the repo mod '{session.Edits.ActiveMod!.Id}' for this session.");
            }

            return;
        }

        window.Choice.Show("The unsaved edits do not apply",
            "The files of the mod changed after the earlier session wrote its unsaved edits, so the edits do not apply. " +
            "The Console shows the files. Discard the edits?",
            [new Choice("Discard", () => Discard(session)), new Choice("Later", () => { })]);
    }

    /// <summary>Deletes the recovery file: the unsaved edits of an earlier session, or of this session.</summary>
    private void Discard(EditorSession session)
    {
        try
        {
            session.Edits.DiscardRecovery();
            session.RecoveryWritten();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Add(ConsoleSeverity.Error, Source, "The editor could not delete the recovery file: " + e.Message);
        }
    }

    /// <summary>
    /// Writes the recovery file a moment after the last edit. Call it each frame.
    /// </summary>
    /// <param name="editing">True while a field edit is open. The file waits for its end.</param>
    public void Update(bool editing)
    {
        if (window.Session is { RecoveryOutdated: true } session && !editing &&
            DateTime.UtcNow - session.LastChange >= RecoveryDelay)
        {
            WriteRecovery(session);
        }
    }

    /// <summary>Writes the unsaved edits to the recovery file now, for example when the window closes.</summary>
    public void WriteRecovery(EditorSession session)
    {
        foreach (var problem in session.Edits.WriteRecovery())
        {
            Log.Add(problem, "The recovery file does not keep this edit: ");
        }

        session.RecoveryWritten();
    }

    /// <summary>Loads the game data again from the edit view, or queues one more reload while one runs.</summary>
    private void Reload(EditorSession session)
    {
        if (session.IsReloading)
        {
            reloadQueued = true;
            return;
        }

        session.ReloadStarted();
        Log.SkipKnownEngineLines = true;
        // Each reload reads its own file system of the edit view, which no later save changes.
        var fs = session.Edits.EditView.CreateFileSystem();
        var saves = session.Saves;
        Task.Run(() =>
        {
            GameDataManager? data = null;
            string? error = null;
            try
            {
                data = new GameDataManager(new GameItemDb(fs), session.Resources);
                // Without a UI thread, the load adds no shapes to the resource manager. It has them from the first
                // load, and a second add throws.
                data.LoadData(null);
            }
            catch (Exception e)
            {
                (data, error) = (null, e.Message);
            }

            // FLLog gives its lines to the UI thread in order. So after the flush, the lines of the load come first.
            FLLog.Flush();
            window.QueueUIThread(() => Reloaded(session, data, error, saves));
        });
    }

    private void Reloaded(EditorSession session, GameDataManager? data, string? error, int saves)
    {
        session.ReloadEnded(data);
        // The last line of FLLog can follow the flush by a moment, so the log adds all engine lines again one step later.
        window.QueueUIThread(() => Log.SkipKnownEngineLines = window.Session?.IsReloading == true);
        if (window.Session != session)
        {
            return;
        }

        if (data != null)
        {
            window.GameDataReplaced(data);
            Log.Add(ConsoleSeverity.Info, Source, $"The game data loaded again after save {saves}.");
        }
        else
        {
            Log.Add(ConsoleSeverity.Error, Source,
                $"The game data did not load again after save {saves}: {error} The editor shows the data before the save.");
        }

        if (reloadQueued)
        {
            reloadQueued = false;
            Reload(session);
        }
    }

    private void Validate(EditorSession session, int save)
    {
        if (validation.RunningSave is { } running)
        {
            Log.Add(ConsoleSeverity.Info, ValidationSource, $"The validation of save {running} stopped. Save {save} replaces it.");
        }

        Log.Add(ConsoleSeverity.Info, ValidationSource, $"Validating save {save}...");
        validation.Start(session.Profile.FilePath, session.Edits.ActiveMod!.Id, save, window,
            result => Validated(session, result));
    }

    private void Validated(EditorSession session, ValidationResult result)
    {
        if (window.Session != session)
        {
            return;
        }

        if (result.Problems is not { } problems)
        {
            Log.Add(ConsoleSeverity.Error, ValidationSource, $"The validation of save {result.Save} could not run: {result.Failure}");
            return;
        }

        var errors = problems.Count(p => p.Severity == DiagnosticSeverity.Error);
        var warnings = problems.Count - errors;
        Log.Add(errors > 0 ? ConsoleSeverity.Error : ConsoleSeverity.Info, ValidationSource, errors > 0
            ? $"Validation of save {result.Save} failed: {errors} errors and {warnings} warnings that the mod adds."
            : $"Validation OK for save {result.Save}: no errors, {warnings} warnings that the mod adds.");
        foreach (var problem in problems)
        {
            Log.Add(problem, $"Save {result.Save}: ");
        }
    }

    /// <summary>Stops the running validation before a save. Returns the number of its save, or null when none
    /// ran.</summary>
    private int? StopValidation()
    {
        if (validation.RunningSave is not { } running)
        {
            return null;
        }

        validation.Stop();
        Log.Add(ConsoleSeverity.Info, ValidationSource, $"The validation of save {running} stopped before the next save.");
        return running;
    }

    /// <summary>Stops the validation of the session, for example when the profile closes.</summary>
    public void Stop()
    {
        validation.Stop();
        reloadQueued = false;
        incompleteSave = 0;
    }

    public void Dispose() => validation.Dispose();
}

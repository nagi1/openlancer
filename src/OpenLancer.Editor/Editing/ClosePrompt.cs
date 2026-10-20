using System;
using System.Collections.Generic;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// What the close prompt needs from the editor. <see cref="EditorWindow"/> implements it. The tests give a fake,
/// because an editor window needs a GPU.
/// </summary>
internal interface IClosePromptHost
{
    /// <summary>Ends an open field edit or drag and keeps its change. The end asks no question (see
    /// <see cref="FieldEditRouter.EndAtClose"/>).</summary>
    void EndEdits();

    /// <summary>The name of the active mod while the session has unsaved edits, or null.</summary>
    string? UnsavedMod { get; }

    /// <summary>The display name of the mod whose publish draft in the Mods window has unapplied changes, or
    /// null.</summary>
    string? UnappliedDraft { get; }

    /// <summary>
    /// Applies the publish draft of the Mods window, with its questions. Runs <paramref name="applied"/> when Apply
    /// wrote the files. When Apply stops, the Mods window tells why, and <paramref name="applied"/> does not run.
    /// </summary>
    void ApplyDraft(Action applied);

    /// <summary>Drops the publish draft of the Mods window.</summary>
    void RevertDraft();

    /// <summary>
    /// Saves the edits as Ctrl+S does, with the checks before a save. Runs <paramref name="saved"/> when the save
    /// saved, also after the Overwrite dialog. When the save fails or a check stops it, the Console tells why, and
    /// <paramref name="saved"/> does not run.
    /// </summary>
    void Save(Action saved);

    /// <summary>Deletes the recovery file of the session, so that the next open does not offer the edits.</summary>
    void DiscardRecovery();

    /// <summary>The loads of the game data of the open session, or null without a session.</summary>
    DataLoads? Loads { get; }

    /// <summary>Ends the window now.</summary>
    void Exit();

    /// <summary>Shows a dialog: its title, its message and its choices. <paramref name="cancel"/> runs when Escape or
    /// the close button of the title bar closes the dialog.</summary>
    void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel);

    /// <summary>Shows a dialog that the user cannot cancel. It blocks the window, so the user cannot edit, save or start
    /// Play until a choice closes it or the window closes.</summary>
    void Wait(string title, string message, IReadOnlyList<Choice> choices);
}

/// <summary>
/// The save prompt when the user closes the editor window: the close button, Alt+F4, the taskbar or File > Exit
/// (roadmap decision D78). With unsaved edits, the window stays open, and a dialog asks: Save, Don't Save or Cancel.
/// A publish draft of the Mods window with unapplied changes asks first: Apply, Revert or Cancel (section 10.7 of the
/// M11c design).
/// </summary>
/// <remarks>
/// <para>While the game data loads, the window closes when the load ends, as Unity ends a running import before it
/// quits. Each load fills the resources of the session, so the session must not be disposed while a load runs (finding
/// 9 of the S1 review). Until the load ends, a dialog that the user cannot cancel blocks the window, as the progress bar
/// of a Unity import does. So no edit comes after the answer of the user. Its button "Close now" closes the window at
/// once, for a load that does not end. The window then leaves the busy session alone.</para>
/// <para>A crash or a kill of the editor cannot ask. For these, the recovery file keeps the unsaved edits (decision
/// D8).</para>
/// </remarks>
internal sealed class ClosePrompt(IClosePromptHost host)
{
    /// <summary>The title of the dialog. ImGui knows a popup by its title, so no other dialog of the editor uses
    /// it.</summary>
    public const string Title = "Close the editor";

    /// <summary>The message of the dialog while the close waits for the running load of the game data.</summary>
    public const string WaitMessage = "The game data loads now. The editor closes when the load ends.";

    /// <summary>The button of the dialog while the close waits, for a load that does not end.</summary>
    public const string CloseNow = "Close now";

    /// <summary>The tooltip of <see cref="CloseNow"/>.</summary>
    public const string CloseNowTooltip =
        "Closes the editor now. The load of the game data stops. The saved files do not change.";

    // True from the request that shows the dialog until the dialog closes.
    private bool asking;

    /// <summary>True while the close waits for the end of the running load of the game data.</summary>
    public bool Waits => host.Loads?.CloseWaits == true;

    /// <summary>
    /// Runs a close request. An open field edit or drag ends first. Without a publish draft and without unsaved edits,
    /// the window closes now, and the result is true. Else a dialog shows and the result is false: first the question
    /// about the draft, then the question about the unsaved edits. The choices of the user then close the window or
    /// keep it open. While the game data loads, the close waits for the end of the load, and the result is false. A
    /// request while a dialog shows or while the close waits changes nothing and gives false.
    /// </summary>
    public bool Request()
    {
        if (asking || Waits)
        {
            return false;
        }

        host.EndEdits();
        if (host.UnappliedDraft is not { } draft)
        {
            return AskUnsaved();
        }

        asking = true;
        host.Ask(Title, PublishMessages.DraftQuestion(draft), DraftChoices(ApplyDraft, RevertDraft, Answered), Answered);
        return false;
    }

    /// <summary>The choices of the dialog. Save is the first choice, so Enter keeps the edits.</summary>
    internal static IReadOnlyList<Choice> Choices(Action save, Action dontSave, Action cancel) =>
        [new Choice("Save", save), new Choice("Don't Save", dontSave), new Choice("Cancel", cancel)];

    /// <summary>The choices of the question about a publish draft. Apply is the first choice, so Enter keeps the
    /// changes.</summary>
    internal static IReadOnlyList<Choice> DraftChoices(Action apply, Action revert, Action cancel) =>
        [new Choice("Apply", apply), new Choice("Revert", revert), new Choice("Cancel", cancel)];

    /// <summary>Asks about the unsaved edits. Without unsaved edits, it closes the window (see <see cref="Close"/>) and
    /// gives its result.</summary>
    private bool AskUnsaved()
    {
        if (host.UnsavedMod is not { } mod)
        {
            return Close();
        }

        asking = true;
        host.Ask(Title, $"Save changes to {mod} before you close?", Choices(Save, DontSave, Answered), Answered);
        return false;
    }

    /// <summary>After Apply wrote the files, the question about the unsaved edits follows. Apply that stops keeps the
    /// window open.</summary>
    private void ApplyDraft()
    {
        asking = false;
        host.ApplyDraft(CloseAfterDraft);
    }

    private void RevertDraft()
    {
        asking = false;
        host.RevertDraft();
        CloseAfterDraft();
    }

    private void CloseAfterDraft() => AskUnsaved();

    /// <summary>The window closes after a save. A save that fails keeps it open.</summary>
    private void Save()
    {
        asking = false;
        host.Save(() => Close());
    }

    /// <summary>The window closes, and the next open does not offer the edits.</summary>
    private void DontSave()
    {
        asking = false;
        host.DiscardRecovery();
        Close();
    }

    private void Answered() => asking = false;

    /// <summary>
    /// Closes the window now, or when the running load of the game data ends (see <see cref="DataLoads.Close"/>). While
    /// the close waits, a dialog that the user cannot cancel blocks the window. Its only button is
    /// <see cref="CloseNow"/>.
    /// </summary>
    /// <returns>True when the window closes now. False when the close waits for the load.</returns>
    private bool Close()
    {
        if (host.Loads is not { } loads)
        {
            host.Exit();
            return true;
        }

        if (loads.Close(host.Exit))
        {
            return true;
        }

        host.Wait(Title, WaitMessage, [new Choice(CloseNow, host.Exit) { Tooltip = CloseNowTooltip }]);
        return false;
    }
}

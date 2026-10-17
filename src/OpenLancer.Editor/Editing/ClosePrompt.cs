using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Saves the edits as Ctrl+S does, with the checks before a save. Runs <paramref name="saved"/> when the save
    /// saved, also after the Overwrite dialog. When the save fails or a check stops it, the Console tells why, and
    /// <paramref name="saved"/> does not run.
    /// </summary>
    void Save(Action saved);

    /// <summary>Deletes the recovery file of the session, so that the next open does not offer the edits.</summary>
    void DiscardRecovery();

    /// <summary>Closes the window.</summary>
    void Close();

    /// <summary>Shows a dialog: its title, its message and its choices. <paramref name="cancel"/> runs when Escape or
    /// the close button of the title bar closes the dialog.</summary>
    void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel);
}

/// <summary>
/// The save prompt when the user closes the editor window: the close button, Alt+F4, the taskbar or File > Exit
/// (roadmap decision D78). With unsaved edits, the window stays open, and a dialog asks: Save, Don't Save or Cancel.
/// </summary>
/// <remarks>A crash or a kill of the editor cannot ask. For these, the recovery file keeps the unsaved edits (decision
/// D8).</remarks>
internal sealed class ClosePrompt(IClosePromptHost host)
{
    /// <summary>The title of the dialog. ImGui knows a popup by its title, so no other dialog of the editor uses
    /// it.</summary>
    public const string Title = "Close the editor";

    // True from the request that shows the dialog until the dialog closes.
    private bool asking;

    /// <summary>
    /// Runs a close request. An open field edit or drag ends first. Without unsaved edits, the result is true: the
    /// window closes now. With unsaved edits, the dialog shows and the result is false. The choice of the user then
    /// closes the window or keeps it open. A request while the dialog shows changes nothing and gives false.
    /// </summary>
    public bool Request()
    {
        if (asking)
        {
            return false;
        }

        host.EndEdits();
        if (host.UnsavedMod is not { } mod)
        {
            return true;
        }

        asking = true;
        host.Ask(Title, $"Save changes to {mod} before you close?", Choices(Save, DontSave, Answered), Answered);
        return false;
    }

    /// <summary>The choices of the dialog. Save is the first choice, so Enter keeps the edits.</summary>
    internal static IReadOnlyList<Choice> Choices(Action save, Action dontSave, Action cancel) =>
        [new Choice("Save", save), new Choice("Don't Save", dontSave), new Choice("Cancel", cancel)];

    /// <summary>The window closes after a save. A save that fails keeps it open.</summary>
    private void Save()
    {
        asking = false;
        host.Save(host.Close);
    }

    /// <summary>The window closes, and the next open does not offer the edits.</summary>
    private void DontSave()
    {
        asking = false;
        host.DiscardRecovery();
        host.Close();
    }

    private void Answered() => asking = false;
}

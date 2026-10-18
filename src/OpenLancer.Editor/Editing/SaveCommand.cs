namespace OpenLancer.Editor.Editing;

/// <summary>
/// What the save of Ctrl+S and File > Save needs from the editor. <see cref="EditorWindow"/> implements it. The tests
/// give a fake, because an editor window needs a GPU.
/// </summary>
internal interface ISaveCommandHost
{
    /// <summary>True while a dialog of the editor shows. The dialog has the keyboard.</summary>
    bool DialogOpen { get; }

    /// <summary>True while a field edit or a drag is open. Its transaction stops a save until it ends.</summary>
    bool EditOpen { get; }

    /// <summary>
    /// Ends the open field edit or drag and keeps its change, as the close prompt does. The end asks no question (see
    /// <see cref="FieldEditRouter.EndAtClose"/>). The text field of the edit then gives back the keyboard, as after a
    /// click outside the field.
    /// </summary>
    void EndEdits();

    /// <summary>Saves the edits of the active mod, with the checks before a save. The status bar and the Console show
    /// the result, also when the save does not start (see <see cref="EditController.Save()"/>).</summary>
    void Save();
}

/// <summary>
/// The save of Ctrl+S and File > Save, as in Unity: it also works while a text field has the keyboard. An open field
/// edit or drag ends first and keeps its change, then the edits save. Only a dialog stops the save.
/// </summary>
/// <remarks>ImGui types no character while Ctrl is down, so Ctrl+S adds no "s" to the text field. A text field
/// without a field edit, for example the search field of the Hierarchy, keeps the keyboard.</remarks>
internal static class SaveCommand
{
    /// <summary>Runs the save. Returns false when a dialog shows, and then nothing changes.</summary>
    public static bool Run(ISaveCommandHost host)
    {
        if (host.DialogOpen)
        {
            return false;
        }

        if (host.EditOpen)
        {
            host.EndEdits();
        }

        host.Save();
        return true;
    }
}

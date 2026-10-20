using System;
using System.Collections.Generic;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Editor.Ui.Fields;

/// <summary>
/// The marks of the text of an editable raw INI editor while the user types (UI rule 11 of AGENTS.md). They show the
/// problems of the text and the problems of the checks of the parsed text. The checks can run on a worker, so the marks
/// can come some frames after the change.
/// </summary>
internal interface IRawDraft
{
    /// <summary>Takes the text of the editor after a change.</summary>
    void Changed(string text);

    /// <summary>The marks of the last text. The list is a new list each time the marks change.</summary>
    IReadOnlyList<RawMark> Marks { get; }
}

/// <summary>
/// The open edit of an editable raw INI editor (section 8.8 of the M6b design): the shown text at the start of the
/// edit. The edit starts when the text of the editor differs from the shown text. It ends when the editor loses the
/// focus, or when the window ends the open edits, for example before a save. Then the owner plans the edit from the
/// start text and the end text.
/// </summary>
internal sealed class RawIniEdit
{
    private string? start;

    /// <summary>True while an edit is open.</summary>
    public bool IsOpen => start != null;

    /// <summary>
    /// Takes one frame of the editor. Returns the start text and the end text of an edit that ends in this frame, or
    /// null.
    /// </summary>
    /// <param name="shown">The text that the editor shows when no edit is open.</param>
    /// <param name="changed">True when the editor reports a change of its text in this frame.</param>
    /// <param name="text">Gives the text of the editor. The edit reads it only after a change and at the end.</param>
    /// <param name="focused">True while the editor has the focus.</param>
    public (string Start, string End)? Frame(string shown, bool changed, Func<string> text, bool focused)
    {
        // A new text of the editor also reports a change, so only a text that differs starts an edit.
        if (start == null && changed && !Same(text(), shown))
            start = shown;
        return start != null && !focused ? End(text) : null;
    }

    /// <summary>Ends the open edit now. Returns its start text and end text, or null without an open edit.</summary>
    public (string Start, string End)? End(Func<string> text)
    {
        if (start is not { } open)
            return null;
        start = null;
        return (open, text());
    }

    /// <summary>Forgets the open edit without an end, for example when the profile closes.</summary>
    public void Reset() => start = null;

    /// <summary>True for two texts with the same lines. The editor can change the line ends and the empty last
    /// line.</summary>
    internal static bool Same(string a, string b) =>
        string.Equals(Normal(a), Normal(b), StringComparison.Ordinal);

    private static string Normal(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}

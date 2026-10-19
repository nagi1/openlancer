using System;
using System.Collections.Generic;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Inspector;

namespace OpenLancer.Editor.Ui.Fields;

/// <summary>
/// The list picker of M21 for one value outside the field edits of the Inspector, for example a cell of a list field or
/// the system filter of the Missions tab. One opening is one change: each pick sets the value, and Esc puts back the value
/// of the opening, as in the Inspector.
/// </summary>
internal sealed class ValuePicker
{
    private readonly ListPicker picker = new();
    private bool open;
    private string start = "";

    /// <summary>
    /// Draws the popup of the picker. Call it in each frame after the field draws, in the ImGui id scope of the field.
    /// Returns the value of a pick in this frame, the value of the opening after Esc, or null without a change.
    /// </summary>
    /// <param name="id">The id of the popup. Two fields in one id scope need different ids.</param>
    /// <param name="openNow">Opens the popup in this frame, for example after a click on the field.</param>
    /// <param name="current">The value of the field now. A value outside the list gets the "Missing" row.</param>
    /// <param name="entries">Gives the entries of the list and their display names when the popup opens.</param>
    /// <param name="none">True when the picker offers "None": the value can be empty.</param>
    /// <param name="kind">The kind of the entries, for their glyph.</param>
    public string? Draw(string id, bool openNow, string current,
        Func<IReadOnlyList<(ListEntry Entry, string Name)>> entries, bool none, ObjectKind kind)
    {
        if (openNow)
        {
            start = current;
        }

        string? result = null;
        picker.Draw(id, openNow, () => new ListPickerModel(entries(), none, current), kind, _ => 0, _ => open,
            (_, events, value) =>
            {
                if (events.Activated)
                {
                    open = true;
                }

                if (events.Changed)
                {
                    result = value;
                }

                if (events.Cancelled)
                {
                    result = start;
                }

                if (events.Deactivated)
                {
                    open = false;
                }
            });
        return result;
    }
}

using System;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor.Editing;

/// <summary>What ImGui tells about one bound text field in one frame, after the field draws.</summary>
/// <param name="Activated">The field became active in this frame.</param>
/// <param name="Active">The field is active.</param>
/// <param name="Changed">The text of the field changed in this frame.</param>
/// <param name="Deactivated">The field stopped being active. When the user moves directly to the next field, ImGui
/// tells this one frame late, after the next field became active.</param>
/// <param name="Cancelled">The user pressed Escape in this frame. Escape ends the field and puts back its text.</param>
internal readonly record struct FieldEvents(bool Activated, bool Active, bool Changed, bool Deactivated, bool Cancelled);

/// <summary>
/// Runs the field edits of the bound text fields from the events of their widgets. The activation of a field starts an
/// edit, each change of its text shows at once, and the end of the field ends the edit. One edit is open at a time.
/// </summary>
/// <remarks>
/// <para>An edit belongs to one widget, by its ImGui id, and to one key. More than one widget can edit a key, for
/// example the X, Y and Z fields of a vector. ImGui tells the end of a field one frame late when the user moves
/// directly to the next field. So only the widget of the open edit can change or end it.</para>
/// <para>Escape ends the edit and puts back the state before it. Every other end keeps the text of the field when it
/// is valid. When it is not valid, the end also puts back the state before the edit, and the warning tells why.</para>
/// </remarks>
/// <param name="warn">Gets the message when an edit cannot start, or when it ends with an error.</param>
internal sealed class FieldEditRouter(Action<string> warn)
{
    // The ImGui id of the widget of the open edit, and the last frame in which that widget was active.
    private uint widget;
    private int lastFrame;

    /// <summary>The open edit, or null.</summary>
    public FieldGesture? Open { get; private set; }

    /// <summary>Runs the events of one field widget in one frame.</summary>
    /// <param name="id">The ImGui id of the widget.</param>
    /// <param name="field">The key that the widget edits, as the frame shows it.</param>
    /// <param name="text">Gives the text of the key after a change. Its argument is the key as it was at the start of
    /// the edit.</param>
    /// <param name="frame">The number of the frame.</param>
    public void Route(EditSession session, uint id, FieldBinding field, FieldEvents events,
        Func<FieldBinding, string> text, int frame)
    {
        if (events.Activated)
        {
            End(keep: true);
            Begin(session, id, field, frame);
        }

        if (Open is not { } edit || id != widget || !SameKey(edit.Binding, field))
        {
            return;
        }

        if (events.Active)
        {
            lastFrame = frame;
            if (events.Changed)
            {
                edit.Change(text(edit.Binding));
            }
        }

        if (events.Deactivated)
        {
            End(keep: !events.Cancelled);
        }
    }

    /// <summary>
    /// Ends an edit whose widget was not active in the last frame, for example because the selection changed. Call it
    /// once each frame before the fields draw.
    /// </summary>
    public void EndLost(int frame)
    {
        if (Open != null && lastFrame < frame - 1)
        {
            End(keep: true);
        }
    }

    /// <summary>Ends the open edit, if there is one.</summary>
    /// <param name="keep">False to put back the state before the edit.</param>
    public void End(bool keep)
    {
        if (Open is not { } edit)
        {
            return;
        }

        Open = null;
        if (edit.End(keep) is { } error)
        {
            warn($"'{edit.Binding.Name}' did not change: {error}");
        }
    }

    /// <summary>The error of the open edit when it edits the key, or null.</summary>
    public string? ErrorOf(FieldBinding field) =>
        Open is { Error: { } error } edit && SameKey(edit.Binding, field) ? error : null;

    /// <summary>The key of the open edit as it was at the start of the edit, when the edit is of the same key as
    /// <paramref name="field"/>. Otherwise null.</summary>
    public FieldBinding? StartOf(FieldBinding field) =>
        Open is { } edit && SameKey(edit.Binding, field) ? edit.Binding : null;

    /// <summary>True when the two bindings are the same key of the same section.</summary>
    public static bool SameKey(FieldBinding a, FieldBinding b) =>
        a.Section.Equals(b.Section) && a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);

    private void Begin(EditSession session, uint id, FieldBinding field, int frame)
    {
        try
        {
            Open = new FieldGesture(session, field);
            widget = id;
            lastFrame = frame;
        }
        catch (InvalidOperationException e)
        {
            warn(e.Message);
        }
    }
}

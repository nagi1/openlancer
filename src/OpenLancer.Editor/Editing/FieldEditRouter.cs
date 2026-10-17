using System;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Editing;

/// <summary>What ImGui tells about one text field in one frame, after the field draws.</summary>
/// <param name="Activated">The field became active in this frame.</param>
/// <param name="Active">The field is active.</param>
/// <param name="Changed">The text of the field changed in this frame.</param>
/// <param name="Deactivated">The field stopped being active. When the user moves directly to the next field, ImGui
/// tells this one frame late, after the next field became active.</param>
/// <param name="Cancelled">The user pressed Escape in this frame. Escape ends the field and puts back its text.</param>
internal readonly record struct FieldEvents(bool Activated, bool Active, bool Changed, bool Deactivated, bool Cancelled);

/// <summary>
/// The key that a text field edits: a key of a section and one of its lines or all of them, or a path of an element of
/// a cutscene.
/// </summary>
/// <param name="Owner">The section (<see cref="SectionRef"/>) or the element of a cutscene (<see cref="ThnRef"/>).</param>
/// <param name="Name">The INI key, or the path in the element.</param>
/// <param name="Line">The index of the line that the field edits, or <see cref="AllLines"/>.</param>
/// <remarks>Two keys are equal when they have the same owner, line and name. The name of an INI key ignores letter
/// case, as INI keys do. A path of a cutscene does not, because the record keys of a cutscene are case-sensitive.</remarks>
internal readonly record struct FieldKey(object Owner, string Name, int Line)
{
    /// <summary>The line value of a field that edits all lines of its key.</summary>
    public const int AllLines = -1;

    private StringComparison NameComparison =>
        Owner is SectionRef ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public bool Equals(FieldKey other) =>
        Owner.Equals(other.Owner) && Line == other.Line && Name.Equals(other.Name, NameComparison);

    public override int GetHashCode() =>
        HashCode.Combine(Owner, Line, Owner is SectionRef ? StringComparer.OrdinalIgnoreCase.GetHashCode(Name) : Name.GetHashCode());
}

/// <summary>
/// Runs the edits of the text fields from the events of their widgets. The activation of a field starts an edit, each
/// change of its text goes to the edit, and the end of the field ends the edit. One edit is open at a time.
/// </summary>
/// <remarks>
/// <para>The field gives the gesture of its edit: a <see cref="LineGesture"/> shows each change at once, and so does a
/// <see cref="NumberLineGesture"/> of a label drag or a color picker. A <see cref="CommitGesture"/> runs its edit when
/// the field ends. While an edit of any kind is open, the edit history, the save and the recovery file wait.</para>
/// <para>An edit belongs to one widget, by its ImGui id, and to one key. More than one widget can edit a key, for
/// example the X, Y and Z fields of a vector. ImGui tells the end of a field one frame late when the user moves
/// directly to the next field. So only the widget of the open edit can change or end it.</para>
/// <para>Escape ends the edit and discards it. Every other end keeps the text of the field when it is valid, also a
/// change of the text in the frame of the end. When the text is not valid, the end also puts back the state before
/// the edit, and the warning tells why.</para>
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
    /// <param name="key">The key that the widget edits, as the frame shows it.</param>
    /// <param name="begin">Starts the edit when the widget becomes active. It can throw
    /// <see cref="InvalidOperationException"/>, for example when the session takes no edits.</param>
    /// <param name="text">Gives the text of the key after a change. Its argument is the text at the start of the
    /// edit.</param>
    /// <param name="frame">The number of the frame.</param>
    public void Route(uint id, FieldKey key, FieldEvents events, Func<FieldGesture> begin, Func<string, string> text,
        int frame)
    {
        if (events.Activated)
        {
            End(keep: true);
            Begin(id, begin, frame);
        }

        if (Open is not { } edit || id != widget || !edit.Key.Equals(key))
        {
            return;
        }

        if (events.Active)
        {
            lastFrame = frame;
        }

        // ImGui can still change the text in the frame in which the field ends, for example when a key and a click
        // outside the field come in one frame. The change of an Escape is not a step of the edit, because ImGui puts
        // back the start text.
        if (events.Changed && (events.Active || events.Deactivated && !events.Cancelled))
        {
            edit.Change(text(edit.StartText));
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
    /// <param name="keep">False to discard the edit.</param>
    public void End(bool keep) => End(keep, interactive: true);

    /// <summary>
    /// Ends the open edit when the window closes, and keeps its text, so that the close prompt and the recovery file
    /// see the edit. The close prompt is the only question then, so the edit takes the choice that changes the least.
    /// </summary>
    public void EndAtClose() => End(keep: true, interactive: false);

    private void End(bool keep, bool interactive)
    {
        if (Open is not { } edit)
        {
            return;
        }

        Open = null;
        if (edit.End(keep, interactive) is { } error)
        {
            warn($"'{edit.Key.Name}' did not change: {error}");
        }
    }

    /// <summary>The error of the open edit when it edits the key, or null.</summary>
    public string? ErrorOf(FieldKey key) => Open is { Error: { } error } edit && edit.Key.Equals(key) ? error : null;

    /// <summary>The key of the open edit as it was at the start of the edit, when the open edit changes the lines of
    /// the same key as <paramref name="field"/>: a text, a label drag or a color picker. Otherwise null.</summary>
    public FieldBinding? StartOf(FieldBinding field) =>
        Open is LineEditGesture edit && edit.Key.Equals(field.FieldKey) ? edit.Binding : null;

    /// <summary>True when the open edit belongs to the widget with the ImGui id <paramref name="id"/>.</summary>
    public bool IsOpenFor(uint id) => Open != null && widget == id;

    private void Begin(uint id, Func<FieldGesture> begin, int frame)
    {
        try
        {
            Open = begin();
            widget = id;
            lastFrame = frame;
        }
        catch (InvalidOperationException e)
        {
            warn(e.Message);
        }
    }
}

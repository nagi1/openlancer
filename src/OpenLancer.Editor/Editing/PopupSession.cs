namespace OpenLancer.Editor.Editing;

/// <summary>
/// The state of one kind of popup of the Inspector: one opening of the popup is one field edit. The color picker (section
/// 8.4 of the M20 design) and the list picker (section 12.2 of the M21 design) each have one. It gives the events of
/// the field edit (see <see cref="FieldEditRouter"/>).
/// </summary>
/// <remarks>
/// <para>The editor reads Escape itself, because ImGui does not close a popup on Escape without keyboard navigation.
/// Escape always puts back the value of the opening, also while an input of the popup is active, and it wins over a
/// close in the same frame. A click outside the popup closes it and keeps the value.</para>
/// <para>Every row of the grid with such a popup calls <see cref="Frame"/> in each frame. A call of another row changes
/// nothing, unless the popup of that row is open. A row that did not draw in the last frame lost its session.</para>
/// </remarks>
internal sealed class PopupSession
{
    private static readonly FieldEvents None = default;

    private bool isOpen;
    private uint id;
    private int lastFrame;
    private int openFrame;

    /// <summary>True while the session of the row with this popup id is open.</summary>
    public bool IsOpenFor(uint popup) => isOpen && id == popup;

    /// <summary>Takes the state of the popup of one row in one frame. Returns the events for the field edits.</summary>
    /// <param name="popup">The ImGui id of the popup of the row.</param>
    /// <param name="frame">The number of the frame.</param>
    /// <param name="open">The popup is open in this frame (<c>BeginPopup</c> returned true).</param>
    /// <param name="escape">The user pressed Escape in this frame.</param>
    /// <param name="changed">The popup changed the value in this frame.</param>
    /// <param name="owned">The open field edit belongs to this popup, before the routing of this frame.</param>
    public FieldEvents Frame(uint popup, int frame, bool open, bool escape, bool changed, bool owned)
    {
        if (isOpen && popup != id)
        {
            if (!open)
                return None;
            isOpen = false;
        }

        if (isOpen && frame > lastFrame + 1)
            isOpen = false;

        id = popup;
        lastFrame = frame;
        if (!isOpen)
        {
            if (!open)
                return None;
            isOpen = true;
            openFrame = frame;
            return new FieldEvents(Activated: true, Active: true, Changed: false, Deactivated: false, Cancelled: false);
        }

        if (frame > openFrame && !owned)
        {
            // The edit ended from outside, for example at the close prompt, or it did not start.
            isOpen = false;
            return None;
        }

        if (escape)
        {
            isOpen = false;
            return new FieldEvents(Activated: false, Active: false, Changed: false, Deactivated: true, Cancelled: true);
        }

        if (!open)
        {
            isOpen = false;
            return new FieldEvents(Activated: false, Active: false, Changed: false, Deactivated: true, Cancelled: false);
        }

        return new FieldEvents(Activated: false, Active: true, Changed: changed, Deactivated: false, Cancelled: false);
    }
}

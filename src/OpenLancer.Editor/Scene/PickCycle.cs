using System.Collections.Generic;
using System.Numerics;

namespace OpenLancer.Editor.Scene;

/// <summary>The place and the turn of the Scene camera.</summary>
internal readonly record struct CameraPose(Vector3 Position, Quaternion Orientation);

/// <summary>
/// The click cycle of the Scene (section 6.4 of the M22 design). A click again at the same place, or an Alt+click
/// anywhere, selects the next item of the pick stack (see <see cref="ScenePicker"/>). Unity selects the next object
/// under the mouse when the user clicks again at the same place. Alt+click also reaches the zone behind an object from
/// another place. The cycle keeps the place, the camera pose, the stack and the item of the last pick. This class has
/// no ImGui call.
/// </summary>
internal sealed class PickCycle
{
    /// <summary>A click this near to the last pick, in pixels, is at the same place. It is also the drag limit of a
    /// click.</summary>
    public const float SamePlace = 4;

    private Vector2? place;
    private CameraPose pose;
    private IReadOnlyList<object> stack = [];
    private object? picked;

    /// <summary>
    /// The selection after a click on the Scene. A plain click selects the next item of the stack under three
    /// conditions. The click is at the same place as the last pick. The camera did not move. The selection is still the
    /// item of the last pick. Else it selects the first item, and a click on empty space clears the selection. An
    /// Alt+click selects the item after the selection, or the first item when the selection is not in the stack. An
    /// Alt+click on empty space keeps the selection, because Alt is also the orbit key. After the last item, the first
    /// item comes again.
    /// </summary>
    /// <param name="items">The pick stack under the mouse.</param>
    /// <returns>True and the new selection, or false when the selection stays.</returns>
    public (bool Changes, object? Selection) Click(IReadOnlyList<object> items, Vector2 mouse, CameraPose cameraPose,
        object? selection, bool alt)
    {
        if (items.Count == 0)
        {
            Reset();
            return alt ? (false, selection) : (true, null);
        }

        var next = alt || IsSamePlace(mouse) && cameraPose == pose && ReferenceEquals(selection, picked);
        var at = next ? IndexOf(items, selection) : -1;
        var item = items[(at + 1) % items.Count];
        place = mouse;
        pose = cameraPose;
        stack = items;
        picked = item;
        return (true, item);
    }

    /// <summary>
    /// The caption of the stack (decision D162 of the M22 design), for example "Item 2 of 4 under the mouse. Click again,
    /// or Alt+click, for the next item." It shows while three conditions apply. The mouse stays at the place of the last
    /// pick. The selection is the item of that pick. The stack has two or more items. Null in all other cases.
    /// </summary>
    public string? Hint(Vector2 mouse, object? selection)
    {
        var at = IndexOf(stack, picked);
        if (stack.Count < 2 || at < 0 || !IsSamePlace(mouse) || !ReferenceEquals(selection, picked))
        {
            return null;
        }

        return $"Item {at + 1} of {stack.Count} under the mouse. Click again, or Alt+click, for the next item.";
    }

    /// <summary>Forgets the last pick. Call it when a system opens or closes.</summary>
    public void Reset()
    {
        place = null;
        pose = default;
        stack = [];
        picked = null;
    }

    private bool IsSamePlace(Vector2 mouse) => place is { } last && Vector2.Distance(last, mouse) <= SamePlace;

    private static int IndexOf(IReadOnlyList<object> items, object? item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}

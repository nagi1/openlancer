using System;
using System.Collections.Generic;

namespace OpenLancer.Editor.Ui;

/// <summary>The tab that shows in the center group (decision D550 of the M6a design).</summary>
internal enum CenterMode
{
    Scene,
    Cutscene,
    Mission,
}

/// <summary>
/// One kept selection for each tab of the center group (section 4.2 of the M6a design, decision D550). When the shown
/// center tab changes, the selection of the old tab goes into its slot, and the selection of the new tab comes out of
/// its slot. So each of the Scene, Cutscene and Mission tabs gets back its own selection, as each Unity window keeps
/// its own state.
/// </summary>
/// <remarks>
/// <para>Each tab owns a test for its own items: the Cutscene tab owns the elements of a cutscene, and the Mission tab
/// owns the items of a mission. Each other selection belongs to the tab that shows when the user makes it. Examples are
/// a section in the Database and an object in the Scene part of the Mission tab. That tab gets it back when it shows
/// again.</para>
/// <para>Each tab validates its kept selection when it comes back. A selection that is not valid any more becomes the
/// root item of the tab, or null.</para>
/// </remarks>
internal sealed class CenterSelections
{
    private readonly Dictionary<CenterMode, object?> slots = new()
    {
        [CenterMode.Scene] = null, [CenterMode.Cutscene] = null, [CenterMode.Mission] = null,
    };

    private readonly Func<object?, CenterMode?> owner;

    /// <param name="owner">Gives the tab that owns an item, or null for an item that no tab owns, for example a
    /// section or an object of a system.</param>
    public CenterSelections(Func<object?, CenterMode?> owner) => this.owner = owner;

    /// <summary>The tab that showed last.</summary>
    public CenterMode Shown { get; private set; } = CenterMode.Scene;

    /// <summary>The kept selection of a tab that does not show.</summary>
    public object? Kept(CenterMode mode) => slots[mode];

    /// <summary>
    /// Follows the shown tab. Returns the selection of the new tab when the tab changed, else the current selection.
    /// </summary>
    /// <param name="mode">The tab that shows now.</param>
    /// <param name="current">The selection before the change.</param>
    /// <param name="valid">Gives the item that a tab can show for a kept item: the item, another item such as the root
    /// of the tab, or null.</param>
    public object? Follow(CenterMode mode, object? current, Func<CenterMode, object?, object?> valid)
    {
        if (mode == Shown)
            return current;
        var from = Shown;
        Shown = mode;
        var currentOwner = owner(current);
        if (currentOwner == mode)
            return current;
        // An item of the old tab or an item that no tab owns stays with the old tab, because the user made it there. An
        // item of a third tab goes into the slot of that tab, for example an element that a mission link selected.
        slots[currentOwner ?? from] = current;
        return valid(mode, slots[mode]);
    }

    /// <summary>Keeps an item in the slot of a tab, for example the root of a cutscene that opens while the Scene tab
    /// shows. The tab shows the item when it shows next.</summary>
    public void Park(CenterMode mode, object? item) => slots[mode] = item;

    /// <summary>The tab that owns an item, or null.</summary>
    public CenterMode? OwnerOf(object? item) => owner(item);

    /// <summary>Forgets the kept selections, for example when the profile closes.</summary>
    public void Reset()
    {
        foreach (var mode in Enum.GetValues<CenterMode>())
            slots[mode] = null;
        Shown = CenterMode.Scene;
    }
}

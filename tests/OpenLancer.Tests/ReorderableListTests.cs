using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The model part of the list widget with Add, Remove and reorder (decision D579 of the M11c design).</summary>
public sealed class ReorderableListTests
{
    [Fact]
    public void AddRemoveAndMoveChangeTheList()
    {
        string[] list = ["a", "b", "c"];

        Assert.Equal(["a", "b", "c", "new"], ReorderableList.Apply(list, ListEdit.Add, () => "new"));
        Assert.Equal(["a", "c"], ReorderableList.Apply(list, ListEdit.Remove(1), () => "new"));
        // Move up and Move down move an entry one place.
        Assert.Equal(["b", "a", "c"], ReorderableList.Apply(list, ListEdit.Move(1, 0), () => "new"));
        Assert.Equal(["a", "c", "b"], ReorderableList.Apply(list, ListEdit.Move(1, 2), () => "new"));
        // A drag moves an entry to the place of the row that gets the drop.
        Assert.Equal(["c", "a", "b"], ReorderableList.Apply(list, ListEdit.Move(2, 0), () => "new"));
        Assert.Equal(["b", "c", "a"], ReorderableList.Apply(list, ListEdit.Move(0, 2), () => "new"));
        // The edit gives a new list. The old list does not change.
        Assert.Equal(["a", "b", "c"], list);
    }

    /// <summary>A locked entry, for example the changelog entry of a published version, does not move or go. The
    /// other entries still move (review of M11c Build 2).</summary>
    [Fact]
    public void ALockedEntryDoesNotMoveOrGo()
    {
        string[] list = ["a", "published", "c"];
        static string? Locked(string entry) => entry == "published" ? "Version 1.0.0 is on Nexus." : null;

        Assert.Equal(list, ReorderableList.Apply(list, ListEdit.Remove(1), () => "new", Locked));
        Assert.Equal(list, ReorderableList.Apply(list, ListEdit.Move(1, 0), () => "new", Locked));
        Assert.Equal(["a", "c"], ReorderableList.Apply(["a", "b", "c"], ListEdit.Remove(1), () => "new", Locked));
        Assert.Equal(["c", "a", "published"], ReorderableList.Apply(list, ListEdit.Move(2, 0), () => "new", Locked));
        Assert.Equal(["a", "published", "c", "new"], ReorderableList.Apply(list, ListEdit.Add, () => "new", Locked));
    }

    [Fact]
    public void AnEditOutsideTheListChangesNothing()
    {
        string[] list = ["a", "b"];

        Assert.Equal(list, ReorderableList.Apply(list, ListEdit.Remove(2), () => "new"));
        Assert.Equal(list, ReorderableList.Apply(list, ListEdit.Move(0, 0), () => "new"));
        Assert.Equal(list, ReorderableList.Apply(list, ListEdit.Move(0, 5), () => "new"));
        Assert.Equal(list, ReorderableList.Apply(list, ListEdit.Move(-1, 0), () => "new"));
    }
}

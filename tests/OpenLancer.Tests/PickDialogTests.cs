using System.Reflection;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The picker of a spawn of a ship (decision D278 of the M23 design).</summary>
public sealed class PickDialogTests
{
    /// <summary>Esc runs the cancel action only. It never runs the second button "New static prop archetype", so Esc
    /// makes nothing.</summary>
    [Fact]
    public void EscNeverRunsTheSecondButton()
    {
        var dialog = new PickDialog();
        var (cancelled, second, picked) = (0, 0, 0);
        var entry = new ListEntry("wreck", null, ValueOrigin.Base, null, "");
        dialog.Show("Create: prop of Gunboat", "Pick.", ObjectKinds.OtherObject, [(entry, "Wreck", "mission_satellite")],
            _ => picked++, "Cancel", () => cancelled++, "New static prop archetype", () => second++);
        Assert.True(dialog.IsOpen);

        typeof(PickDialog).GetMethod("Cancelled", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);

        Assert.Equal((1, 0, 0), (cancelled, second, picked));
    }
}

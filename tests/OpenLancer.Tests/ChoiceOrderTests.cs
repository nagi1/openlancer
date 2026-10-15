using System.Collections.Generic;
using System.Linq;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The first choice of a dialog is its default button, so Enter clicks it. Each dialog of the edits puts the choice
/// that loses no work first.
/// </summary>
public sealed class ChoiceOrderTests
{
    [Fact]
    public void TheDialogOfEditsThatDoNotApplyListsLaterFirst()
    {
        var discarded = 0;

        var choices = EditController.NotAppliedChoices(() => discarded++);

        Assert.Equal(["Later", "Discard"], Labels(choices));
        choices[0].Action();
        Assert.Equal(0, discarded);
        choices[1].Action();
        Assert.Equal(1, discarded);
    }

    [Fact]
    public void TheOverwriteDialogListsCancelFirst()
    {
        var overwritten = 0;

        var choices = EditController.OverwriteChoices(() => overwritten++);

        Assert.Equal(["Cancel", "Overwrite"], Labels(choices));
        choices[0].Action();
        Assert.Equal(0, overwritten);
        choices[1].Action();
        Assert.Equal(1, overwritten);
    }

    [Fact]
    public void TheOtherDialogsOfTheEditsListTheChoiceThatKeepsTheEditsFirst()
    {
        var calls = new List<string>();

        var leave = EditController.LeaveChoices(() => calls.Add("save"), () => calls.Add("discard"));
        var recovery = EditController.RecoveryChoices(() => calls.Add("restore"), () => calls.Add("discard"));

        Assert.Equal(["Save", "Discard", "Cancel"], Labels(leave));
        Assert.Equal(["Restore", "Discard", "Later"], Labels(recovery));
        foreach (var choice in leave.Concat(recovery))
            choice.Action();
        Assert.Equal(["save", "discard", "restore", "discard"], calls);
    }

    private static List<string> Labels(IReadOnlyList<Choice> choices) => choices.Select(c => c.Label).ToList();
}

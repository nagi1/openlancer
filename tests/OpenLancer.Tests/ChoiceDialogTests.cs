using System.Collections.Generic;
using OpenLancer.Editor.Dialogs;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The question dialog of the editor. A question that takes the place of an open question runs the cancel action of the
/// open question, so a caller that waits for an answer always gets one, for example the agent of an agent play request
/// (review fix of M6c-2). The tests do not draw the dialog.
/// </summary>
public sealed class ChoiceDialogTests
{
    [Fact]
    public void ANewQuestionCancelsTheOpenQuestion()
    {
        var dialog = new ChoiceDialog();
        var calls = new List<string>();
        dialog.Show("First", "The first question?", [new Choice("OK", () => calls.Add("ok"))], () => calls.Add("first"));

        dialog.Show("Second", "The second question?", [new Choice("OK", () => calls.Add("ok"))], () => calls.Add("second"));

        Assert.Equal(["first"], calls);
        Assert.True(dialog.IsOpen);
    }

    /// <summary>A question that shows while no question is open cancels nothing.</summary>
    [Fact]
    public void AQuestionWithoutAnOpenQuestionCancelsNothing()
    {
        var dialog = new ChoiceDialog();
        var calls = new List<string>();

        dialog.Show("First", "The first question?", [new Choice("OK", () => { })], () => calls.Add("first"));

        Assert.Empty(calls);
    }
}

using OpenLancer.Editor.Ui.Fields;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The edit state of an editable raw INI editor (section 8.8 of the M6b design).</summary>
public sealed class RawIniEditTests
{
    private const string Shown = "[Trigger]\nnickname = ol_a\nCnd_True = no_params";

    [Fact]
    public void ANewTextOfTheEditorStartsNoEdit()
    {
        var edit = new RawIniEdit();

        // The editor reports a change when it gets a new text, also with other line ends.
        Assert.Null(edit.Frame(Shown, changed: true, () => Shown.Replace("\n", "\r\n") + "\n", focused: false));

        Assert.False(edit.IsOpen);
    }

    [Fact]
    public void AnEditStartsAtAChangeAndEndsWhenTheEditorLosesTheFocus()
    {
        var edit = new RawIniEdit();
        var typed = Shown.Replace("no_params", "no_params\nAct_ActTrig = ol_b");

        Assert.Null(edit.Frame(Shown, changed: true, () => typed, focused: true));
        Assert.True(edit.IsOpen);
        // A newer shown text, for example after an edit of another view, does not change the start of the edit.
        Assert.Null(edit.Frame("[Trigger]\nnickname = ol_a", changed: false, () => typed, focused: true));
        var ended = edit.Frame("[Trigger]\nnickname = ol_a", changed: false, () => typed, focused: false);

        Assert.Equal((Shown, typed), ended);
        Assert.False(edit.IsOpen);
    }

    [Fact]
    public void TheWindowEndsAnOpenEditBeforeASave()
    {
        var edit = new RawIniEdit();
        edit.Frame(Shown, changed: true, () => Shown + "\nrepeatable = true", focused: true);

        Assert.Equal((Shown, Shown + "\nrepeatable = true"), edit.End(() => Shown + "\nrepeatable = true"));
        Assert.Null(edit.End(() => Shown));
    }
}

using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// One opening of the color picker as one field edit, without ImGui (section 8.4 of the M20 design). Each test scripts
/// the frames of the popup of one or two color rows.
/// </summary>
public sealed class PickerSessionTests
{
    // The ImGui ids of the popups of two color rows.
    private const uint Light = 31;
    private const uint Sun = 32;

    [Fact]
    public void AnOpeningOfThePickerIsOneFieldEdit()
    {
        var session = new PickerSession();

        Assert.Equal(default, session.Frame(Light, 1, open: false, escape: false, changed: false, owned: false));
        Assert.Equal(new FieldEvents(true, true, false, false, false), session.Frame(Light, 2, true, false, false, false));
        Assert.True(session.IsOpenFor(Light));
        Assert.Equal(new FieldEvents(false, true, false, false, false), session.Frame(Light, 3, true, false, false, true));
        Assert.Equal(new FieldEvents(false, true, true, false, false), session.Frame(Light, 4, true, false, true, true));
        // A click outside the popup closes it and keeps the color.
        Assert.Equal(new FieldEvents(false, false, false, true, false), session.Frame(Light, 5, false, false, false, true));
        Assert.False(session.IsOpenFor(Light));
        Assert.Equal(default, session.Frame(Light, 6, false, false, false, false));
    }

    [Fact]
    public void EscapeCancelsTheOpeningAlsoInTheFrameThatThePopupCloses()
    {
        var session = new PickerSession();
        session.Frame(Light, 1, true, false, false, false);
        session.Frame(Light, 2, true, false, true, true);

        Assert.Equal(new FieldEvents(false, false, false, true, true), session.Frame(Light, 3, true, true, false, true));
        Assert.False(session.IsOpenFor(Light));

        session.Frame(Light, 4, true, false, false, false);
        Assert.Equal(new FieldEvents(false, false, false, true, true), session.Frame(Light, 5, false, true, false, true));
    }

    /// <summary>The close prompt can end the edit of the picker, and an edit can fail to start. The session then
    /// closes without events, and the row closes the popup.</summary>
    [Fact]
    public void ASessionThatDoesNotOwnTheOpenEditCloses()
    {
        var session = new PickerSession();
        session.Frame(Light, 1, true, false, false, false);

        Assert.Equal(default, session.Frame(Light, 2, true, false, true, false));
        Assert.False(session.IsOpenFor(Light));
    }

    /// <summary>A row that does not draw for a frame lost its session. The other color rows draw in the same frames
    /// and change nothing, until the popup of one of them opens.</summary>
    [Fact]
    public void AGapOfFramesOrTheOpeningOfAnotherRowResetsTheSession()
    {
        var session = new PickerSession();
        session.Frame(Light, 1, true, false, false, false);
        Assert.Equal(default, session.Frame(Sun, 1, false, false, false, false));
        Assert.True(session.IsOpenFor(Light));

        Assert.Equal(new FieldEvents(true, true, false, false, false), session.Frame(Light, 3, true, false, false, true));

        Assert.Equal(new FieldEvents(true, true, false, false, false), session.Frame(Sun, 4, true, false, false, true));
        Assert.True(session.IsOpenFor(Sun));
        Assert.False(session.IsOpenFor(Light));
    }
}

using System.Collections.Generic;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using Xunit;
using static OpenLancer.Tests.FieldBindingTests;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The field edits of the bound text fields without the GPU. Each test replays, frame by frame, the events that ImGui
/// gives for the widgets. The Inspector rows also keep their places while an edit is open.
/// </summary>
public sealed class FieldEditRouterTests
{
    // The ImGui ids of two widgets.
    private const uint X = 11;
    private const uint Y = 12;

    private static readonly FieldEvents Activated = new(Activated: true, Active: true, Changed: false,
        Deactivated: false, Cancelled: false);

    private static readonly FieldEvents Active = new(Activated: false, Active: true, Changed: false,
        Deactivated: false, Cancelled: false);

    private static readonly FieldEvents Typed = new(Activated: false, Active: true, Changed: true,
        Deactivated: false, Cancelled: false);

    private static readonly FieldEvents Ended = new(Activated: false, Active: false, Changed: false,
        Deactivated: true, Cancelled: false);

    // Escape puts back the text of the widget, so the text changes in the frame in which the widget ends.
    private static readonly FieldEvents Escaped = new(Activated: false, Active: false, Changed: true,
        Deactivated: true, Cancelled: true);

    [Fact]
    public void EachAxisOfAVectorKeepsItsTextWhenTheUserMovesDirectlyToTheNextAxis()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        session.Execute(Bind(session, Ship("s1"), "steering_torque").Edit("1, 2, 3"));
        var warnings = new List<string>();
        var router = new FieldEditRouter(warnings.Add);
        FieldBinding Torque() => Bind(session, Ship("s1"), "steering_torque");
        void Axis(uint id, int index, FieldEvents events, string value, int frame) =>
            router.Route(session, id, Torque(), events, start => FieldEdit.WithValue(start.Text, index, value), frame);

        // Frame 1: the user clicks X. Frame 2: the user types 5.
        Axis(X, 0, Activated, "1", 1);
        Axis(X, 0, Typed, "5", 2);
        // Frame 3: X draws while it is active. Then the user clicks Y, and Y becomes active.
        Axis(X, 0, Active, "5", 3);
        Axis(Y, 1, Activated, "2", 3);
        // Frame 4: ImGui tells the end of X one frame late. The user types 7 in Y.
        Axis(X, 0, Ended, "5", 4);
        Axis(Y, 1, Typed, "7", 4);
        Assert.Equal("5, 7, 3", Torque().Text);
        // Frame 5: Y ends.
        Axis(Y, 1, Ended, "7", 5);

        Assert.Null(router.Open);
        Assert.Equal("5, 7, 3", Torque().Text);
        Assert.Empty(warnings);
        // Each axis is one entry of the history.
        session.Undo();
        Assert.Equal("5, 2, 3", Torque().Text);
    }

    [Fact]
    public void EscapeCancelsTheEditAndTheOtherEndsKeepIt()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        FieldBinding Mass() => Bind(session, Ship("s1"), "mass");
        void Field(FieldEvents events, string text, int frame) => router.Route(session, X, Mass(), events, _ => text, frame);

        Field(Activated, "120", 1);
        Field(Typed, "130", 2);
        Assert.Equal("130", Mass().Text);
        Field(Escaped, "120", 3);

        Assert.Null(router.Open);
        Assert.Equal("120", Mass().Text);
        Assert.False(session.CanUndo);

        // Enter, Tab or a click outside the field.
        Field(Activated, "120", 4);
        Field(Typed, "140", 5);
        Field(Ended, "140", 6);

        Assert.Equal("140", Mass().Text);
        Assert.Equal("Set mass", session.UndoLabel);
    }

    [Fact]
    public void AnEditWhoseWidgetDoesNotDrawEndsOneFrameLaterAndKeepsItsText()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        router.Route(session, X, Bind(session, Ship("s1"), "mass"), Activated, _ => "", 1);
        router.Route(session, X, Bind(session, Ship("s1"), "mass"), Typed, _ => "150", 2);

        router.EndLost(3);
        Assert.NotNull(router.Open);
        router.EndLost(4);

        Assert.Null(router.Open);
        Assert.Equal("150", Bind(session, Ship("s1"), "mass").Text);
        Assert.Equal("Set mass", session.UndoLabel);
    }

    [Fact]
    public void AnEditWithTextThatIsNotValidEndsWithAWarningWhenTheNextFieldStarts()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var warnings = new List<string>();
        var router = new FieldEditRouter(warnings.Add);
        router.Route(session, X, Bind(session, Ship("s1"), "mass"), Activated, _ => "", 1);
        router.Route(session, X, Bind(session, Ship("s1"), "mass"), Typed, _ => "13x", 2);
        Assert.Contains("is not a number", router.ErrorOf(Bind(session, Ship("s1"), "mass")));
        Assert.Null(router.ErrorOf(Bind(session, Ship("s1"), "hold_size")));

        router.Route(session, Y, Bind(session, Ship("s1"), "hold_size"), Activated, _ => "", 3);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("'mass' did not change:", warning);
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.Equal("hold_size", router.Open!.Binding.Name);
    }

    [Fact]
    public void TheWidgetOfAnEditDoesNotChangeAnotherKey()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        router.Route(session, X, Bind(session, Ship("s1"), "mass"), Activated, _ => "", 1);

        // The rows moved, and the active widget now draws the row of another key.
        router.Route(session, X, Bind(session, Ship("s1"), "hold_size"), Typed, _ => "9", 2);

        // The text of that widget goes to neither key.
        Assert.Empty(Bind(session, Ship("s1"), "hold_size").Lines);
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.Equal("mass", router.Open!.Binding.Name);
    }

    [Fact]
    public void TheKeyListsOfASectionKeepTheKeyOfAnOpenEditInItsPlace()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var ship = Ship("s1");
        session.Execute(EditCommand.SetLinesFromText(ship, "custom_key", ["5"]));
        var before = SchemaPage.Layout(session, ship, null);
        Assert.Contains("hold_size", before.Unset);
        Assert.Contains("mass", before.Set);
        Assert.Equal(["custom_key"], before.Unknown);

        // An unset key gets a value. The first character adds the key.
        using (var gesture = new FieldGesture(session, Bind(session, ship, "hold_size")))
        {
            gesture.Change("1");

            Assert.Equal(Lists(before), Lists(SchemaPage.Layout(session, ship, gesture)));
            Assert.Contains("hold_size", SchemaPage.Layout(session, ship, null).Set);
            // The lists of another section do not wait for the edit.
            var good = Good(GoodsPath, "s1_hull");
            Assert.Equal(Lists(SchemaPage.Layout(session, good, null)), Lists(SchemaPage.Layout(session, good, gesture)));
            Assert.Null(gesture.End(keep: true));
        }

        // A set key and a key that the engine does not read become empty before the user types the new value.
        foreach (var name in (string[])["mass", "custom_key"])
        {
            var start = SchemaPage.Layout(session, ship, null);
            using var gesture = new FieldGesture(session, Bind(session, ship, name));
            gesture.Change("");
            Assert.Empty(session.Get(ship)!.Lines(name));

            Assert.Equal(Lists(start), Lists(SchemaPage.Layout(session, ship, gesture)));
            var after = SchemaPage.Layout(session, ship, null);
            Assert.DoesNotContain(name, after.Set);
            Assert.DoesNotContain(name, after.Unknown);
        }
    }

    [Fact]
    public void AKeyThatTheOpenEditClearsStaysBoundUntilTheEditEnds()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var ship = Ship("s1");
        session.Execute(EditCommand.SetLinesFromText(ship, "custom_key", ["5"]));
        using var gesture = new FieldGesture(session, Bind(session, ship, "custom_key"));

        gesture.Change("");

        var view = session.Get(ship)!;
        Assert.NotNull(new PropertyGrid.BoundSection(session, ship, view, "custom_key").Field("custom_key"));
        // Without the edit, a key that neither the schema nor the section has is not bound.
        Assert.Null(new PropertyGrid.BoundSection(session, ship, view, null).Field("custom_key"));
    }

    /// <summary>The key lists as one text, so that a failed test shows the lists.</summary>
    private static string Lists(SchemaPage.KeyLists lists) =>
        $"Set: {string.Join(", ", lists.Set)}. Unknown: {string.Join(", ", lists.Unknown)}. " +
        $"Unset: {string.Join(", ", lists.Unset)}.";
}

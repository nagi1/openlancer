using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using Xunit;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The bound rows of the Inspector without the GPU: the state of a key (its lines, the layer that sets them, the later
/// mods, the lock), the edits of a row, and one field edit from its start to its end.
/// </summary>
public sealed class FieldBindingTests
{
    /// <summary>
    /// The item workspace with three mods: "low" loads before the active mod "act" and sets the price of the hull
    /// good, "act" sets the mass of the ship, and "late" loads after it and changes the mass too.
    /// </summary>
    internal static TestWorkspace Layers()
    {
        var ws = Create();
        ws.Mod("low", new()
        {
            [@"patches\" + GoodsPath] = """
                [Good]
                nickname = s1_hull
                price = 900
                """,
        });
        ws.Mod("act", new()
        {
            [@"patches\" + ShipsPath] = """
                [Ship]
                nickname = s1
                mass = 120
                """,
        });
        ws.Mod("late", new()
        {
            [@"patches\" + ShipsPath] = """
                [Ship]
                nickname = s1
                mass = 140
                """,
        });
        ws.WriteProfile("low", "act", "late");
        return ws;
    }

    internal static FieldBinding Bind(EditSession session, SectionRef section, string name) =>
        FieldBinding.Create(session, section, session.Get(section)!, name);

    [Fact]
    public void ABoundKeyShowsItsLinesAndTheLayerThatSetsThem()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var mass = Bind(session, Ship("s1"), "mass");
        Assert.Equal("120", mass.Text);
        Assert.Equal(ValueOrigin.ActiveMod, mass.Origin);
        Assert.Equal("act", mass.OriginMod);
        Assert.Equal(["late"], mass.LaterMods);
        Assert.Equal(typeof(float), mass.Key!.ClrType);
        // The lower layer has the ship, so a revert can put back its value.
        Assert.True(mass.CanRevert);
        Assert.Null(mass.ReadOnlyReason);

        var price = Bind(session, Good(GoodsPath, "s1_hull"), "price");
        Assert.Equal((ValueOrigin.LowerMod, "low"), (price.Origin, price.OriginMod));
        Assert.False(price.CanRevert);
        Assert.Contains("'low'", price.OriginText);

        var ids = Bind(session, Ship("s1"), "ids_name");
        Assert.Equal(ValueOrigin.Base, ids.Origin);
        Assert.Empty(ids.LaterMods);

        // A key of the schema that the section does not set has no lines.
        var hold = Bind(session, Ship("s1"), "hold_size");
        Assert.Empty(hold.Lines);
        Assert.Contains("default", hold.OriginText);

        Assert.NotNull(Bind(session, Ship("s1"), "nickname").ReadOnlyReason);
    }

    [Fact]
    public void AnEditOfTheFieldSetsTheKeyAndARevertPutsBackTheLowerValue()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        session.Execute(Bind(session, Good(GoodsPath, "s1_hull"), "price").Edit(" 1200 "));

        var price = Bind(session, Good(GoodsPath, "s1_hull"), "price");
        Assert.Equal("1200", price.Text);
        Assert.Equal(ValueOrigin.ActiveMod, price.Origin);
        Assert.True(price.CanRevert);
        session.Execute(price.Revert());
        Assert.Equal(("900", ValueOrigin.LowerMod), (Bind(session, Good(GoodsPath, "s1_hull"), "price").Text,
            Bind(session, Good(GoodsPath, "s1_hull"), "price").Origin));
    }

    [Fact]
    public void AKeyOfASectionThatTheActiveModAddsHasNoLowerValue()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));

        var link = Bind(session, Gun("gun2"), "projectile_archetype");

        Assert.Equal(ValueOrigin.ActiveMod, link.Origin);
        Assert.False(link.CanRevert);
        Assert.True(session.Get(Gun("gun2"))!.IsNew);
    }

    [Fact]
    public void AnEmptyFieldRemovesTheKey()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        session.Execute(Bind(session, Ship("s1"), "mass").Edit(""));

        Assert.Empty(session.Get(Ship("s1"))!.Lines("mass"));
        Assert.Equal("Remove mass", session.UndoLabel);
    }

    [Fact]
    public void AFieldEditRefusesTextThatTheSchemaDoesNotAllow()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var error = Assert.Throws<ArgumentException>(() => Bind(session, Ship("s1"), "mass").Edit("heavy"));

        Assert.Contains("is not a number", error.Message);
    }

    [Fact]
    public void AGestureShowsEachChangeAndBecomesOneEntryOfTheHistory()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var changes = 0;
        session.Changed += _ => changes++;

        using (var gesture = new FieldGesture(session, Bind(session, Ship("s1"), "mass")))
        {
            gesture.Change("1");
            Assert.Equal("1", Bind(session, Ship("s1"), "mass").Text);
            gesture.Change("15");
            gesture.Change("150");
            Assert.Null(gesture.End(keep: true));
        }

        Assert.Equal("150", Bind(session, Ship("s1"), "mass").Text);
        Assert.Equal("Set mass", session.UndoLabel);
        Assert.True(changes >= 4);
        session.Undo();
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AGestureThatEndsWithTheStartTextChangesNothing()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var gesture = new FieldGesture(session, Bind(session, Ship("s1"), "mass"));
        gesture.Change("200");
        gesture.Change("120");
        gesture.End(keep: true);

        Assert.False(session.CanUndo);
        // The pin of the patch line stays, so the mod keeps its value when a lower mod changes.
        Assert.Equal(ValueOrigin.ActiveMod, Bind(session, Ship("s1"), "mass").Origin);
        var result = session.Save();
        Assert.True(result.Saved);
        Assert.Empty(result.Written);
    }

    [Fact]
    public void AGestureWithTextThatIsNotValidEndsWithItsErrorAndChangesNothing()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var gesture = new FieldGesture(session, Bind(session, Ship("s1"), "mass"));
        gesture.Change("130");
        gesture.Change("13x");
        Assert.Contains("is not a number", gesture.Error);
        // The last valid text shows until the end.
        Assert.Equal("130", Bind(session, Ship("s1"), "mass").Text);

        Assert.Contains("is not a number", gesture.End(keep: true));
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AGestureThatTheUserCancelsPutsBackTheStart()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var gesture = new FieldGesture(session, Bind(session, Ship("s1"), "mass"));
        gesture.Change("130");

        Assert.Null(gesture.End(keep: false));
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AGestureOnALineThatAPatchCannotHoldKeepsTheErrorOfTheCommand()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var gesture = new FieldGesture(session, Bind(session, Ship("s1"), "da_archetype"));
        gesture.Change(@"ships\s1;old.cmp");

        Assert.Contains("';'", gesture.Error);
        Assert.NotNull(gesture.End(keep: true));
        Assert.Equal([@"ships\s1.cmp"], Firsts(session, Ship("s1"), "da_archetype"));
    }

    [Fact]
    public void ARowOfAReadOnlySessionHasTheReasonOfTheSession()
    {
        using var ws = Layers();
        var session = EditSession.Open(Data.Mods.ModProfile.Load(ws.ProfilePath), null, TestWorkspace.FakeDllWriter.Instance);

        var mass = Bind(session, Ship("s1"), "mass");

        Assert.Equal(session.ReadOnlyReason, mass.ReadOnlyReason);
        Assert.NotNull(mass.ReadOnlyReason);
        Assert.False(mass.CanRevert);
        Assert.Throws<InvalidOperationException>(() => new FieldGesture(session, mass));
    }

    [Fact]
    public void TheEditOfAFieldSavesAsAPatchLineOfTheActiveMod()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        session.Execute(Bind(session, Good(GoodsPath, "s1_hull"), "price").Edit("1200"));

        Assert.True(session.Save().Saved);

        var patch = File.ReadAllText(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath));
        Assert.Contains("nickname = s1_hull", patch);
        Assert.Contains("price = 1200", patch);
        Assert.False(File.Exists(Path.Combine(ws.ModFolder("low"), "patches", ShipsPath)));
    }
}

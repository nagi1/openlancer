using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The edit session: commands, the edit history, transactions, and where the values of the edit view come from.
/// </summary>
public sealed class EditSessionTests
{
    internal const string GoodsPath = @"DATA\EQUIPMENT\goods.ini";
    internal const string WeaponsPath = @"DATA\EQUIPMENT\weapon_equip.ini";
    internal const string MarketPath = @"DATA\EQUIPMENT\market_misc.ini";
    internal const string ShipsPath = @"DATA\SHIPS\shiparch.ini";

    internal const string Goods = """
        [Good]
        nickname = g1
        price = 100
        volume = 1

        [Good]
        nickname = g2
        price = 200
        """;

    internal const string Weapons = """
        [Gun]
        nickname = gun1
        damage = 10
        """;

    internal const string Market = """
        [BaseGood]
        base = b1
        marketgood = g1, 0, -1, 1, 1, 0, 1, 1
        """;

    internal const string Ships = """
        [Ship]
        nickname = s1
        mass = 100
        [CollisionGroup]
        obj = a
        [Ship]
        nickname = s2
        mass = 200
        """;

    /// <summary>A workspace with the base files of these tests, an empty active mod "act" and the profile.</summary>
    internal static TestWorkspace Workspace(Dictionary<string, string>? activeFiles = null)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        ws.WriteBase(WeaponsPath, Weapons);
        ws.WriteBase(MarketPath, Market);
        ws.WriteBase(ShipsPath, Ships);
        ws.Mod("act", activeFiles ?? new());
        ws.WriteProfile("act");
        return ws;
    }

    internal static SectionRef Good(string nickname) => new(GoodsPath, new SectionIdentity("Good", nickname));

    internal static SectionRef Gun(string nickname) => new(WeaponsPath, new SectionIdentity("Gun", nickname));

    internal static SectionRef Ship(string nickname) => new(ShipsPath, new SectionIdentity("Ship", nickname));

    internal static EditCommand Set(SectionRef target, string name, params ValueBase[] values) =>
        EditCommand.SetLines(target, name, [values]);

    /// <summary>The values of the lines of a name, as text.</summary>
    internal static string[] Values(EditSession session, SectionRef target, string name) =>
        session.Get(target)!.Lines(name).Select(l => string.Join(", ", l.Values.Select(v => v.ToString()))).ToArray();

    [Fact]
    public void SetLinesPutsTheLinesAtTheFirstLineOfTheName()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLines(Good("g1"), "price", [[new Int32Value(1)], [new Int32Value(2)]]));
        session.Execute(Set(Good("g1"), "ids_name", new Int32Value(7)));

        Assert.Equal(["nickname", "price", "price", "volume", "ids_name"], session.Get(Good("g1"))!.Entries.Select(e => e.Name));
        Assert.Equal(["1", "2"], Values(session, Good("g1"), "price"));
        session.Execute(EditCommand.RemoveName(Good("g1"), "price"));
        Assert.Empty(session.Get(Good("g1"))!.Lines("price"));
    }

    [Fact]
    public void APathFindsItsFileInAnyFormOfTheInstallPath()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;
        var other = new SectionRef("data/equipment/GOODS.ini", new SectionIdentity("Good", "G1"));

        session.Execute(Set(other, "price", new Int32Value(7)));

        Assert.Equal(["7"], Values(session, Good("g1"), "price"));
        Assert.Equal(GoodsPath, Assert.Single(Assert.Single(changes).Sections).Path, ignoreCase: true);
    }

    [Fact]
    public void UndoAndRedoPutBackTheStatesAndTheLabels()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        session.Execute(Set(Good("g2"), "price", new Int32Value(400)));

        Assert.Equal("Set price", session.UndoLabel);
        session.Undo();
        Assert.Equal(["200"], Values(session, Good("g2"), "price"));
        Assert.Equal(["300"], Values(session, Good("g1"), "price"));
        Assert.Equal("Set price", session.RedoLabel);
        session.Redo();
        Assert.Equal(["400"], Values(session, Good("g2"), "price"));
        session.Undo();
        session.Undo();
        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        Assert.False(session.CanUndo);
        Assert.Throws<InvalidOperationException>(session.Undo);
        Assert.Equal(6, changes.Count);
        Assert.Equal([Good("g2")], changes[2].Sections);
    }

    [Fact]
    public void ANewEditClearsTheRedoStack()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        session.Undo();
        session.Execute(Set(Good("g2"), "price", new Int32Value(400)));

        Assert.False(session.CanRedo);
        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
    }

    /// <summary>The dirty state compares entry ids, not the number of entries.</summary>
    [Fact]
    public void TheDirtyStateFollowsTheEntryOfTheLastSave()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Assert.False(session.IsDirty);

        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        Assert.True(session.IsDirty);
        Assert.True(session.Save().Saved);
        Assert.False(session.IsDirty);

        session.Undo();
        Assert.True(session.IsDirty);
        session.Redo();
        Assert.False(session.IsDirty);

        // The saved entry leaves the history, so the history has the same length but is not saved.
        session.Undo();
        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void ATransactionIsOneEntryAndEachApplyReplacesTheLastOne()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        using (var transaction = session.Begin("Drag price"))
        {
            transaction.Apply(Set(Good("g1"), "price", new Int32Value(1)));
            Assert.Equal(["1"], Values(session, Good("g1"), "price"));
            transaction.Apply(Set(Good("g2"), "price", new Int32Value(2)));
            // The second step replaces the first, so g1 is back at its value.
            Assert.Equal(["100"], Values(session, Good("g1"), "price"));
            Assert.Throws<InvalidOperationException>(() => session.Execute(Set(Good("g1"), "price", new Int32Value(5))));
            Assert.Throws<InvalidOperationException>(session.Undo);
            Assert.Throws<InvalidOperationException>(() => session.Save());
            transaction.Commit();
        }

        Assert.Equal("Drag price", session.UndoLabel);
        Assert.Equal(["2"], Values(session, Good("g2"), "price"));
        session.Undo();
        Assert.Equal(["200"], Values(session, Good("g2"), "price"));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ATransactionWithoutCommitChangesNothing()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        using (var transaction = session.Begin("Type"))
            transaction.Apply(Set(Good("g1"), "price", new Int32Value(1)));

        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
        session.Execute(Set(Good("g1"), "price", new Int32Value(2)));
        Assert.True(session.CanUndo);
    }

    /// <summary>
    /// The last step of a transaction throws, so the edit view goes back to its state before the transaction. The
    /// session tells it, and a commit then adds no entry.
    /// </summary>
    [Fact]
    public void ATransactionWhoseLastStepThrowsAddsNoEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        using (var transaction = session.Begin("Type"))
        {
            transaction.Apply(Set(Good("g1"), "price", new Int32Value(1)));
            Assert.Throws<ArgumentException>(() => transaction.Apply(Set(Good("missing"), "price", new Int32Value(2))));
            Assert.Equal(["100"], Values(session, Good("g1"), "price"));
            Assert.Equal([Good("g1")], changes[^1].Sections);
            Assert.Equal(2, changes.Count);
            transaction.Commit();
        }

        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
    }

    [Fact]
    public void ACommandThatThrowsChangesNothing()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var group = EditCommand.Group("Two", [
            Set(Good("g1"), "price", new Int32Value(1)),
            Set(Good("missing"), "price", new Int32Value(2)),
        ]);

        Assert.Throws<ArgumentException>(() => session.Execute(group));
        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AddAndDeleteSectionsFollowTheMergeRules()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        session.Execute(EditCommand.AddSection(GoodsPath, "Good",
            [new EntryView("nickname", [new StringValue("g3")]), new EntryView("price", [new Int32Value(5)])]));
        session.Execute(EditCommand.AddSection(ShipsPath, "CollisionGroup",
            [new EntryView("obj", [new StringValue("b")])], Ship("s1")));

        Assert.Equal(["5"], Values(session, Good("g3"), "price"));
        Assert.True(changes[0].Structural);
        var child = new SectionRef(ShipsPath, new SectionIdentity("CollisionGroup", null, new SectionIdentity("Ship", "s1"), 1));
        Assert.Equal(["b"], Values(session, child, "obj"));
        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.AddSection(ShipsPath, "CollisionGroup", [])));
        Assert.Throws<InvalidOperationException>(() =>
            session.Execute(EditCommand.AddSection(GoodsPath, "Good", [], Good("g1"))));

        // A delete takes the child sections with the parent.
        session.Execute(EditCommand.DeleteSection(Ship("s1")));
        Assert.Null(session.Get(Ship("s1")));
        Assert.Null(session.Get(child));
        Assert.NotNull(session.Get(Ship("s2")));
        session.Undo();
        Assert.Equal(["b"], Values(session, child, "obj"));
    }

    [Fact]
    public void RenameChangesOnlyTheKeyOfASectionThatTheModAdds()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(GoodsPath, "Good", [new EntryView("nickname", [new StringValue("g3")])]));

        session.Execute(EditCommand.Rename(Good("g3"), "g4"));

        Assert.Null(session.Get(Good("g3")));
        Assert.Equal(["g4"], Values(session, Good("g4"), "nickname"));
        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.Rename(Good("g4"), "G4")));
        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.Rename(Good("g4"), "g2")));
        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.Rename(Good("g1"), "g9")));
        Assert.Throws<InvalidOperationException>(() => session.Execute(Set(Good("g4"), "nickname", new StringValue("g5"))));
    }

    [Fact]
    public void TextEditsAreInTheHistory()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var placeholder = new StringPlaceholder(StringKind.String, "g1_name");

        session.Execute(EditCommand.SetText(StringKind.String, "g1_name", "Gun"));
        session.Execute(EditCommand.SetText(StringKind.String, "g1_name", "Gun two"));
        Assert.Equal("Gun two", session.Text(placeholder));
        session.Undo();
        session.Undo();
        Assert.Null(session.Text(placeholder));
        session.Redo();
        Assert.Equal("Gun", session.Text(placeholder));
        session.Redo();
        Assert.Equal("Gun two", session.Text(placeholder));
        Assert.Throws<ArgumentException>(() => EditCommand.SetText(StringKind.String, "bad key", "x"));
        Assert.Throws<ArgumentException>(() => EditCommand.SetText(StringKind.String, "key", ""));
    }

    [Fact]
    public void ASessionWithoutAnActiveModIsReadOnly()
    {
        using var ws = Workspace();
        var session = ws.Session(null);

        Assert.False(session.CanEdit);
        Assert.NotNull(session.Get(Good("g1"))!.ReadOnlyReason);
        Assert.Throws<InvalidOperationException>(() => session.Execute(Set(Good("g1"), "price", new Int32Value(1))));
        Assert.Throws<ArgumentException>(() => ws.Session("missing"));
    }

    [Fact]
    public void SectionsThatAPatchCannotChangeAreReadOnly()
    {
        using var ws = Workspace(new() { [@"files\" + WeaponsPath] = Weapons });
        var session = ws.Session("act");
        var lowerMarket = new SectionRef(MarketPath, new SectionIdentity("BaseGood", null));

        Assert.Null(session.Get(Good("g1"))!.ReadOnlyReason);
        Assert.Contains("only adds [BaseGood]", session.Get(lowerMarket)!.ReadOnlyReason);
        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.RemoveName(lowerMarket, "marketgood")));
        // The active mod replaces this file, so the editor cannot patch it.
        Assert.Contains("replaces", session.Get(Gun("gun1"))!.ReadOnlyReason);
        Assert.Null(session.Get(new SectionRef(@"C:\x.ini", new SectionIdentity("Good", "g1"))));
        // A new [BaseGood] can change.
        session.Execute(EditCommand.AddSection(MarketPath, "BaseGood", [new EntryView("base", [new StringValue("b2")])]));
        Assert.Null(session.Get(lowerMarket with { Id = new SectionIdentity("BaseGood", null, null, 1) })!.ReadOnlyReason);
    }

    [Fact]
    public void OriginNamesTheModThatGivesTheLines()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        ws.WriteBase(WeaponsPath, Weapons);
        ws.Mod("low", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 150" });
        // The active mod pins volume: the line equals B, but the mod sets it.
        ws.Mod("act", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nvolume = 1" });
        ws.Mod("late", new()
        {
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 175",
            [@"files\" + WeaponsPath] = Weapons,
        });
        ws.WriteProfile("low", "act", "late");
        var session = ws.Session("act");

        Assert.Equal((ValueOrigin.LowerMod, "low"), session.Origin(Good("g1"), "price"));
        Assert.Equal((ValueOrigin.ActiveMod, "act"), session.Origin(Good("g1"), "volume"));
        Assert.Equal((ValueOrigin.Base, null), session.Origin(Good("g2"), "price"));
        session.Execute(Set(Good("g2"), "price", new Int32Value(1)));
        Assert.Equal((ValueOrigin.ActiveMod, "act"), session.Origin(Good("g2"), "price"));

        Assert.Equal(["late"], session.LaterModsThatChange(Good("g1"), "price"));
        Assert.Empty(session.LaterModsThatChange(Good("g1"), "volume"));
        // A later whole file discards the active patch of its path.
        Assert.Equal(["late"], session.LaterModsThatChange(Gun("gun1"), "damage"));
        Assert.Equal(["late"], session.LaterMods.Select(m => m.Id));
    }
}

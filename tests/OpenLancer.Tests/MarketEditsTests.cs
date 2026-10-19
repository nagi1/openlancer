using System;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using Xunit;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>The edits of the "Markets" section: a base that sells a good, and the lines of the active mod.</summary>
public sealed class MarketEditsTests
{
    private const string ShipMarketPath = @"DATA\EQUIPMENT\market_ships.ini";

    /// <summary>
    /// The item workspace with a second market file before market_misc.ini in the load order. Base b1 sells the ship
    /// package there, and the gun in market_misc.ini.
    /// </summary>
    private static TestWorkspace TwoMarkets()
    {
        var ws = Create();
        ws.WriteBase(@"EXE\freelancer.ini", FreelancerIni.Replace(@"markets = equipment\market_misc.ini",
            "markets = equipment\\market_ships.ini\nmarkets = equipment\\market_misc.ini"));
        ws.WriteBase(ShipMarketPath, """
            [BaseGood]
            base = b1
            marketgood = s1_package, 0, -1, 1, 1, 0, 1, 1
            """);
        ws.WriteBase(MarketPath, """
            [BaseGood]
            base = b1
            marketgood = gun1, 0, -1, 10, 10, 0, 1
            [BaseGood]
            base = b2
            marketgood = eng1, 0, -1, 10, 10, 0, 1
            """);
        return ws;
    }

    /// <summary>All values of a line after the good.</summary>
    private static string Values(MarketLine line) => FieldEdit.FormatLine(line.Values.Skip(1).ToList());

    private static string[] Bases(EditSession session, string good) =>
        session.MarketLines(good).Select(l => $"{l.Base}: {Values(l)}").ToArray();

    /// <summary>Adds a line that sells the good at base b1 with the values, and returns the line.</summary>
    private static MarketLine AddedLine(EditSession session, string good, string values)
    {
        session.Execute(MarketEdits.AddToBase(session, good, "b1", values));
        return session.MarketLines(good).Single(l => l.Base == "b1");
    }

    [Fact]
    public void ABaseSellsAGoodWithANewSectionInTheMarketFileOfTheSameCategory()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        session.Execute(MarketEdits.AddToBase(session, "eng1", "b1", MarketEdits.DefaultValues("equipment")));

        // The first file of b1 sells a ship, so the engine goes into the file in which b1 sells equipment.
        var line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal(MarketPath, line.Section.Path, ignoreCase: true);
        Assert.Equal(new SectionIdentity("BaseGood", null, null, 2), line.Section.Id);
        Assert.Equal(["b2: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 10, 10, 0, 1"], Bases(session, "eng1"));
        Assert.True(MarketEdits.CanChange(session, line));
        Assert.False(MarketEdits.CanChange(session, session.MarketLines("eng1")[0]));
        Assert.Equal("Sell eng1 at b1", session.UndoLabel);
    }

    [Fact]
    public void AShipPackageGoesIntoTheMarketFileThatSellsShipsAtTheBase()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        session.Execute(MarketEdits.AddToBase(session, "s1_package", "b2", MarketEdits.DefaultValues("ship")));

        // b2 is only in market_misc.ini.
        var line = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        Assert.Equal(MarketPath, line.Section.Path, ignoreCase: true);
        Assert.Equal("0, -1, 1, 1, 0, 1, 1", Values(line));
    }

    [Fact]
    public void ASecondGoodGoesIntoTheSectionThatTheActiveModAdds()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        session.Execute(MarketEdits.AddToBase(session, "eng1", "b1", "0, -1, 5, 5, 0, 1"));

        session.Execute(MarketEdits.AddToBase(session, "s1_package", "b2", "0, -1, 1, 1, 0, 1, 1"));
        session.Execute(MarketEdits.AddToBase(session, "gun1", "b2", "0, -1, 3, 3, 0, 1"));

        var added = session.MarketLines("gun1").Single(l => l.Base == "b2").Section;
        Assert.Equal(session.MarketLines("s1_package").Single(l => l.Base == "b2").Section, added);
        Assert.Equal(2, session.Get(added)!.Lines("marketgood").Count);
        Assert.True(session.Save().Saved);
        var patch = File.ReadAllText(Path.Combine(ws.ModFolder("act"), "patches", MarketPath));
        Assert.Contains("base = b2", patch);
        Assert.Contains("marketgood = gun1, 0, -1, 3, 3, 0, 1", patch);
    }

    [Fact]
    public void ABaseThatSellsTheGoodIsRefused()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        var error = Assert.Throws<InvalidOperationException>(() => MarketEdits.AddToBase(session, "gun1", "B1", "0"));

        Assert.Contains("already sells gun1", error.Message);
    }

    [Fact]
    public void ALineOfTheActiveModChangesAndGoes()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        session.Execute(MarketEdits.AddToBase(session, "eng1", "b1", "0, -1, 5, 5, 0, 1"));
        var line = session.MarketLines("eng1").Single(l => l.Base == "b1");

        session.Execute(MarketEdits.SetLine(session, line, "1, -1, 7, 7, 0, 1.5"));
        Assert.Equal("b1: 1, -1, 7, 7, 0, 1.5", Bases(session, "eng1")[1]);

        line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        session.Execute(MarketEdits.SetLine(session, line, null));
        // The section had no other line, so it goes.
        Assert.Null(session.Get(line.Section));
        Assert.Equal(["b2: 0, -1, 10, 10, 0, 1"], Bases(session, "eng1"));
    }

    [Fact]
    public void ALineOfALowerLayerIsRefused()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        Assert.Throws<InvalidOperationException>(() =>
            MarketEdits.SetLine(session, session.MarketLines("gun1")[0], "0, -1, 1, 1, 0, 1"));
    }

    /// <summary>
    /// The values field leaves out the transaction type, because the type has its own dropdown. An edit of the field
    /// keeps the type of the line in its place.
    /// </summary>
    [Fact]
    public void TheValuesFieldKeepsTheTransactionType()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var line = AddedLine(session, "eng1", "0, -1, 5, 5, 2, 1");
        Assert.Equal("0, -1, 5, 5, 1", MarketEdits.FieldText(line));

        session.Execute(MarketEdits.SetValues(session, line, "1, -1, 7, 7, 1.5"));

        line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal("1, -1, 7, 7, 2, 1.5", Values(line));
        Assert.Equal(MarketTransaction.SellOnly, line.Transaction);
    }

    /// <summary>The dropdown changes only the sixth value. A ship line keeps its last value too.</summary>
    [Fact]
    public void TheTransactionTypeChangesOnlyTheSixthValue()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var gun = AddedLine(session, "eng1", "0, -1, 5, 5, 0, 1");
        Assert.Equal(MarketTransaction.BuyAndSell, gun.Transaction);

        session.Execute(MarketEdits.SetTransaction(session, gun, MarketTransaction.BuyOnly));
        session.Execute(MarketEdits.AddToBase(session, "s1_package", "b2", MarketEdits.DefaultValues("ship")));
        var ship = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        session.Execute(MarketEdits.SetTransaction(session, ship, MarketTransaction.SellOnly));

        gun = session.MarketLines("eng1").Single(l => l.Base == "b1");
        ship = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        Assert.Equal("0, -1, 5, 5, 1, 1", Values(gun));
        Assert.Equal(MarketTransaction.BuyOnly, gun.Transaction);
        Assert.Equal("0, -1, 1, 1, 2, 1, 1", Values(ship));
        Assert.Equal("0, -1, 1, 1, 1, 1", MarketEdits.FieldText(ship));
        Assert.Equal("Only sells", MarketSection.TransactionText(ship));
    }

    /// <summary>
    /// A line without the sixth value gets it from the dropdown. A line without a maximum has no place for it. A value
    /// that the game does not know shows as it is.
    /// </summary>
    [Fact]
    public void ALineWithoutAKnownTransactionTypeShowsWhy()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var shortLine = AddedLine(session, "eng1", "0, -1, 5, 5");
        Assert.Null(shortLine.Transaction);
        Assert.Equal("Not set", MarketSection.TransactionText(shortLine));
        Assert.Equal("0, -1, 5, 5", MarketEdits.FieldText(shortLine));

        session.Execute(MarketEdits.SetTransaction(session, shortLine, MarketTransaction.BuyOnly));
        Assert.Equal("0, -1, 5, 5, 1", Values(session.MarketLines("eng1").Single(l => l.Base == "b1")));

        var unknown = AddedLine(session, "s1_hull", "0, -1, 5, 5, 7, 1");
        Assert.Null(unknown.Transaction);
        Assert.Equal("Unknown type 7", MarketSection.TransactionText(unknown));

        session.Execute(MarketEdits.SetValues(session, unknown, "0, -1"));
        var tooShort = session.MarketLines("s1_hull").Single(l => l.Base == "b1");
        Assert.Throws<InvalidOperationException>(() =>
            MarketEdits.SetTransaction(session, tooShort, MarketTransaction.SellOnly));
    }

    /// <summary>Each transaction type has its own plain name in the dropdown.</summary>
    [Fact]
    public void EachTransactionTypeHasItsOwnName()
    {
        var names = Enum.GetValues<MarketTransaction>().Select(MarketSection.Name).ToList();

        Assert.Equal(["Buys and sells", "Only buys", "Only sells"], names);
    }

    [Fact]
    public void TheCategoryOfAGoodComesFromItsSection()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        Assert.Equal("ship", MarketEdits.Category(session, "s1_package"));
        Assert.Equal("equipment", MarketEdits.Category(session, "eng1"));
        Assert.Null(MarketEdits.Category(session, "nothing"));
        Assert.Equal("0, -1, 1, 1, 0, 1, 1", MarketEdits.DefaultValues("SHIP"));
        Assert.Equal("0, -1, 10, 10, 0, 1", MarketEdits.DefaultValues(null));
    }
}

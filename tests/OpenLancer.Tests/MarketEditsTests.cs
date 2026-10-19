using System;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
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

    /// <summary>The field of the value at a place of <see cref="MarketLine.Values"/>.</summary>
    private static MarketValue Field(int index) => MarketEdits.Fields.Single(f => f.Index == index);

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
    /// Each value of a line has its own field. An edit of a field changes only its value, so the transaction type and
    /// the values after the price multiplier stay in their places.
    /// </summary>
    [Fact]
    public void EachFieldChangesOnlyItsValue()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        AddedLine(session, "eng1", "0, -1, 5, 5, 2, 1");
        MarketLine Line() => session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal(["0", "-1", "5", "5", "1"], MarketEdits.Fields.Select(f => MarketEdits.FieldText(Line(), f)));

        session.Execute(MarketEdits.SetValue(session, Line(), Field(MarketLine.RankIndex), "1"));
        session.Execute(MarketEdits.SetValue(session, Line(), Field(MarketLine.ReputationIndex), " 0.5 "));
        session.Execute(MarketEdits.SetValue(session, Line(), Field(MarketLine.MinimumIndex), "7"));
        session.Execute(MarketEdits.SetValue(session, Line(), Field(MarketLine.MaximumIndex), "8"));
        session.Execute(MarketEdits.SetValue(session, Line(), Field(MarketLine.PriceIndex), "1.5"));
        Assert.Equal("1, 0.5, 7, 8, 2, 1.5", Values(Line()));
        Assert.Equal(MarketTransaction.SellOnly, Line().Transaction);

        session.Execute(MarketEdits.AddToBase(session, "s1_package", "b2", MarketEdits.DefaultValues("ship")));
        var ship = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        session.Execute(MarketEdits.SetValue(session, ship, Field(MarketLine.PriceIndex), "2"));
        Assert.Equal("0, -1, 1, 1, 0, 2, 1", Values(session.MarketLines("s1_package").Single(l => l.Base == "b2")));
    }

    /// <summary>
    /// Review finding of fix S2: a price multiplier on a line without the transaction type went into the place of the
    /// type. The engine ignores a line without the price multiplier, so an edit of a shorter line also writes the values
    /// of a new line in the missing places. Each value keeps its place, and the type is 0: the base buys and sells the
    /// good.
    /// </summary>
    [Fact]
    public void APriceOnALineWithoutTheTransactionTypeKeepsItsPlace()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var line = AddedLine(session, "eng1", "0, -1, 5, 5");
        Assert.Equal(["0", "-1", "5", "5", ""], MarketEdits.Fields.Select(f => MarketEdits.FieldText(line, f)));

        session.Execute(MarketEdits.SetValue(session, line, Field(MarketLine.PriceIndex), "2"));

        line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal("0, -1, 5, 5, 0, 2", Values(line));
        Assert.Equal(MarketTransaction.BuyAndSell, line.Transaction);
        Assert.Equal("2", MarketEdits.FieldText(line, Field(MarketLine.PriceIndex)));

        // A ship line gets the values of a new ship line.
        session.Execute(MarketEdits.AddToBase(session, "s1_package", "b2", "0, -1"));
        var ship = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        session.Execute(MarketEdits.SetValue(session, ship, Field(MarketLine.RankIndex), "3"));
        Assert.Equal("3, -1, 1, 1, 0, 1", Values(session.MarketLines("s1_package").Single(l => l.Base == "b2")));
    }

    /// <summary>
    /// Review finding of fix S2: a values field with fewer values dropped the transaction type, and a later edit put a
    /// price multiplier into the place of the type. A field takes only a number of the kind that the engine reads, so
    /// no edit of a field can remove a value.
    /// </summary>
    [Fact]
    public void AFieldTakesOnlyANumberOfItsKind()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var line = AddedLine(session, "eng1", "0, -1, 5, 5, 1, 1");

        var empty = Assert.Throws<InvalidOperationException>(() =>
            MarketEdits.SetValue(session, line, Field(MarketLine.MaximumIndex), ""));
        Assert.Equal("Maximum stock must be a whole number, for example 3.", empty.Message);
        var fraction = Assert.Throws<InvalidOperationException>(() =>
            MarketEdits.SetValue(session, line, Field(MarketLine.MinimumIndex), "1.5"));
        Assert.Equal("Minimum stock must be a whole number, for example 3.", fraction.Message);
        var list = Assert.Throws<InvalidOperationException>(() =>
            MarketEdits.SetValue(session, line, Field(MarketLine.PriceIndex), "1, 2"));
        Assert.Equal("Price multiplier must be a number, for example 1.5.", list.Message);
        Assert.Throws<InvalidOperationException>(() =>
            MarketEdits.SetValue(session, line, Field(MarketLine.ReputationIndex), "1e3"));

        session.Execute(MarketEdits.SetValue(session, line, Field(MarketLine.PriceIndex), "1.5"));
        line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal("0, -1, 5, 5, 1, 1.5", Values(line));
        Assert.Equal(MarketTransaction.BuyOnly, line.Transaction);
    }

    /// <summary>Each value of a line, except the transaction type, has a field with a plain name, in the order of the
    /// line.</summary>
    [Fact]
    public void EachValueHasAFieldWithItsOwnName()
    {
        Assert.Equal([1, 2, 3, 4, 6], MarketEdits.Fields.Select(f => f.Index));
        Assert.Equal(["Rank", "Reputation", "Minimum stock", "Maximum stock", "Price multiplier"],
            MarketEdits.Fields.Select(f => f.Name));
        Assert.All(MarketEdits.Fields, f => Assert.NotEmpty(f.Tip));
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
        Assert.Equal("Only sells", MarketSection.TransactionText(ship));
    }

    /// <summary>
    /// A line without the sixth value gets it from the dropdown, with the values of a new line in the other missing
    /// places. A value that the game does not know shows as it is.
    /// </summary>
    [Fact]
    public void ALineWithoutAKnownTransactionTypeShowsWhy()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var shortLine = AddedLine(session, "eng1", "0, -1");
        Assert.Null(shortLine.Transaction);
        Assert.Equal("Not set", MarketSection.TransactionText(shortLine));

        session.Execute(MarketEdits.SetTransaction(session, shortLine, MarketTransaction.BuyOnly));
        Assert.Equal("0, -1, 10, 10, 1, 1", Values(session.MarketLines("eng1").Single(l => l.Base == "b1")));

        var unknown = AddedLine(session, "s1_hull", "0, -1, 5, 5, 7, 1");
        Assert.Null(unknown.Transaction);
        Assert.Equal("Unknown type 7", MarketSection.TransactionText(unknown));
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

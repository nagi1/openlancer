using System;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using Xunit;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>The "Sold at" edits: a base that sells a good, and the lines of the active mod.</summary>
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

    private static string[] Bases(EditSession session, string good) =>
        session.MarketLines(good).Select(l => $"{l.Base}: {MarketEdits.ValuesText(l)}").ToArray();

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
        Assert.Equal("0, -1, 1, 1, 0, 1, 1", MarketEdits.ValuesText(line));
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

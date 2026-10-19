using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Goods;
using OpenLancer.Data.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The market rule of the validator (decision D511 of the console filter design). Retail reads the sixth value of a
/// <c>MarketGood</c> line as the transaction type: 0 buy and sell, 1 the base buys only, 2 the base sells only. A ship
/// with min = max = 0 is not for sale on purpose when the base only buys it.
/// </summary>
public sealed class ModValidatorMarketTests
{
    /// <summary>The engine reads a market line of the text.</summary>
    private static MarketGood Line(string values)
    {
        var section = IniFile.ParseFile("market.ini", new MemoryStream(Encoding.ASCII.GetBytes(
            $"[BaseGood]\nbase = b1\nmarketgood = {values}\n"))).Single();
        return new MarketGood(section.Single(e => e.Name == "marketgood"));
    }

    [Fact]
    public void AShipThatTheBaseOnlyBuysGivesNoWarning() =>
        Assert.False(ModValidator.ShipNotForSale(Line("pkg1, 0, -1, 0, 0, 1, 1")));

    [Fact]
    public void AShipWithMinAndMaxZeroThatTheBaseCanSellGivesTheWarning() =>
        Assert.True(ModValidator.ShipNotForSale(Line("pkg1, 0, -1, 0, 0, 0, 1")));

    [Fact]
    public void AShipWithStockGivesNoWarning() =>
        Assert.False(ModValidator.ShipNotForSale(Line("pkg1, 0, -1, 1, 1, 0, 1")));
}

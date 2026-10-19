using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The market rule on the retail data (decision D511 of the console filter design). Each retail ship line with
/// min = max = 0 marks the base as a buyer only, so the validator gives no warning for it. Before the rule, these lines
/// gave 1,744 false warnings. The test only reads the data.
/// </summary>
public sealed class ModValidatorMarketRetailTests(ITestOutputHelper output)
{
    static ModValidatorMarketRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheRetailShipMarketsGiveNoNotForSaleWarning()
    {
        var report = ModValidator.Validate(FileSystem.FromPath(GameDataPaths.RequireBase()));

        foreach (var problem in report.Checks)
            output.WriteLine(problem.ToString());
        Assert.DoesNotContain(report.Checks, p => p.Message.EndsWith("so the ship is not for sale.", System.StringComparison.Ordinal));
    }
}

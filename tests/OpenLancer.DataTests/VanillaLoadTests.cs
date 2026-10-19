using LibreLancer;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Smoke test: the engine loads the extracted vanilla data.
/// </summary>
public sealed class VanillaLoadTests(ITestOutputHelper output)
{
    static VanillaLoadTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void BaseFolderIsAFreelancerInstall()
    {
        var baseFolder = GameDataPaths.RequireBase();
        Assert.True(GameConfig.CheckFLDirectory(baseFolder), $"{baseFolder} has no EXE\\freelancer.ini");
    }

    /// <summary>
    /// Loads all game data, every star system into a headless world and every story mission script, as the base run of
    /// the Console does (decision D516 of the console filter design). Fails on any system or mission that does not load,
    /// on an engine line without a kind of the rules table, and on a count of a kind that differs from its pin. The
    /// rules table and the pins replace the text baseline in the out folder of the game data (decision D510), so the
    /// test writes no file.
    /// </summary>
    [Fact]
    public void VanillaDataLoadsWithTheKnownLines()
    {
        var baseFolder = GameDataPaths.RequireBase();

        EngineLineKindsRetail.AssertKinds(BaseEngineLines.Run(baseFolder), baseFolder, output);
    }
}

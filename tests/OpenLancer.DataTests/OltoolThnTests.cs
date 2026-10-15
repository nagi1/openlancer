using System.IO;
using System.Text;
using LibreLancer;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// <c>oltool thn</c> prints the canonical text of a script, and <c>oltool thn roundtrip</c> checks the writer. Both read
/// the bytes through the file system of the source, so a profile shows the scripts of its mods.
/// </summary>
public sealed class OltoolThnTests
{
    private const string S002xe = @"DATA\SCRIPTS\STORY\s002xe_nrml_sinclair_li_01_Cityscape_01.thn";
    private const string Broken = @"DATA\MODS\thnmod\broken.thn";

    static OltoolThnTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void RoundTripOnTheBaseFolderExitsZero()
    {
        var baseFolder = GameDataPaths.RequireBase();

        var (exit, output, errors) = Oltool.Run("thn", "roundtrip", baseFolder);

        Assert.True(exit == 0, errors);
        Assert.Equal("", errors);
        Assert.Contains("Round trip: 1506 of 1506 scripts give the same tables", output);
    }

    [Fact]
    public void ThnPrintsTheCanonicalTextOfABaseScript()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var fromFile = ThornEvaluator.Instance.Evaluate(File.ReadAllBytes(Path.Combine(baseFolder, S002xe)), S002xe);

        var (exit, output, errors) = Oltool.Run("thn", baseFolder, S002xe);

        Assert.True(exit == 0, errors);
        Assert.Equal(ThnWriter.Write(fromFile), output);
        Assert.Equal(fromFile, ThornEvaluator.Instance.Evaluate(Encoding.ASCII.GetBytes(output), "printed"));
        // The install path is case-insensitive and takes either separator, as in the engine.
        Assert.Equal(output, Oltool.Run("thn", baseFolder, S002xe.Replace('\\', '/').ToLowerInvariant()).Output);
    }

    /// <summary>A mod replaces s002xe with a short text script and adds a script with a number that is not finite.
    /// The print shows the mod script in canonical form, without its comment.</summary>
    [Fact]
    public void ThnReadsTheScriptsOfAProfileThroughItsMods()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var ws = new RetailWorkspace(baseFolder, "thnmod");
        WriteModFile(ws.ModFolder, S002xe, "-- A whole-file override.\nduration = 5.0\nentities = { }\nevents = {}\n");
        WriteModFile(ws.ModFolder, Broken, "duration = 1e39\n");

        var (exit, output, errors) = Oltool.Run("thn", ws.ProfilePath, S002xe);
        Assert.True(exit == 0, errors);
        Assert.Equal("duration = 5\nentities = {}\nevents = {}\n", output);

        var (brokenExit, _, brokenErrors) = Oltool.Run("thn", ws.ProfilePath, Broken);
        Assert.Equal(1, brokenExit);
        Assert.Contains("The value at duration is Infinity. A THN number must be finite.", brokenErrors);

        var (oneExit, oneOutput, oneErrors) = Oltool.Run("thn", "roundtrip", ws.ProfilePath, S002xe);
        Assert.True(oneExit == 0, oneErrors);
        Assert.Contains("Round trip: 1 of 1 scripts", oneOutput);

        var (allExit, allOutput, allErrors) = Oltool.Run("thn", "roundtrip", ws.ProfilePath);
        Assert.Equal(1, allExit);
        Assert.Contains("Round trip: 1506 of 1507 scripts", allOutput);
        Assert.Equal($"{Broken}: The value at duration is Infinity. A THN number must be finite.", allErrors.Trim(),
            ignoreCase: true);

        var (missingExit, _, missingErrors) = Oltool.Run("thn", ws.ProfilePath, @"DATA\SCRIPTS\no_such.thn");
        Assert.Equal(2, missingExit);
        Assert.Contains("is not in", missingErrors);
    }

    /// <summary>The steps 3 and 4 of the manual check A0 without the game: a mod with the design patch of s002xe merges
    /// with no problem, and <c>oltool thn</c> prints the merged text with the two changes.</summary>
    [Fact]
    public void ThnPrintsTheMergedTextOfAPatchedScript()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var ws = new RetailWorkspace(baseFolder, "m8proof");
        ThnPatchRetailTests.WriteDesignPatch(ws.ModFolder);

        var (buildExit, buildOutput, buildErrors) = Oltool.Run("build", ws.ProfilePath);
        var (exit, output, errors) = Oltool.Run("thn", ws.ProfilePath, S002xe);

        Assert.True(buildExit == 0, buildErrors);
        Assert.Contains("OK: 0 warnings.", buildOutput);
        Assert.True(exit == 0, errors);
        Assert.Contains("{ entity_name = \"Cam_02_Intro_Trent_A\", type = CAMERA, ", output);
        Assert.Contains("cameraprops = { fovh = 20, hvaspect = 1.85, nearplane = 1, farplane = 7000 } },", output);
        Assert.Contains("\t{ 27, SET_CAMERA, { \"Cam_Monitor_s002xe\", \"Cam_03_ACOH_Trent_Medics_A\" } },\n", output);
        Assert.DoesNotContain("25.75, SET_CAMERA, { \"Cam_Monitor_s002xe\", \"Cam_03_ACOH_Trent_Medics_A\" }", output);
    }

    private static void WriteModFile(string modFolder, string installPath, string text)
    {
        var file = Path.Combine(modFolder, Mod.FilesFolder, installPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
}

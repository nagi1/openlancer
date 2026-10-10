using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Resources;
using LibreLancer.World;
using OpenLancer.Data;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Smoke test: the unmodified Librelancer engine loads the extracted vanilla data.
/// </summary>
public sealed class VanillaLoadTests(ITestOutputHelper output)
{
    private const string BaselineFile = "vanilla-log-baseline.txt";
    private const string CurrentFile = "vanilla-log-current.txt";

    static VanillaLoadTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void BaseFolderIsAFreelancerInstall()
    {
        var baseFolder = GameDataPaths.RequireBase();
        Assert.True(GameConfig.CheckFLDirectory(baseFolder), $"{baseFolder} has no EXE\\freelancer.ini");
    }

    /// <summary>
    /// Loads all game data, every star system into a physics world, and every story mission script.
    /// Fails on any exception, and on any warning or error that is not in the reviewed baseline.
    /// </summary>
    [Fact]
    public void VanillaDataLoadsWithoutNewWarnings()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var failures = new List<string>();

        using var capture = EngineLogCapture.Start();
        var fs = FileSystem.FromPath(baseFolder);
        var resources = new ServerResourceManager(null, fs);
        // The resource manager is not disposable, but its physics shape pool is.
        using var shapes = resources.ConvexCollection;
        var gameData = new GameDataManager(new GameItemDb(fs), resources);
        gameData.LoadData(null);

        var objectCount = 0;
        foreach (var system in gameData.Items.Systems)
        {
            try
            {
                using var world = new GameWorld(null, null, resources, () => 0, true);
                world.LoadSystem(system, resources, null, server: false);
                objectCount += world.Objects.Count;
            }
            catch (Exception e)
            {
                failures.Add($"System {system.Nickname}: {e.GetType().Name}: {e.Message}");
            }
        }

        var missions = gameData.Items.Ini.Storyline.Missions;
        foreach (var mission in missions)
        {
            try
            {
                if (gameData.Items.Ini.LoadMissionIni(mission) == null)
                    failures.Add($"Mission {mission.Nickname}: file not found: {mission.File}");
            }
            catch (Exception e)
            {
                failures.Add($"Mission {mission.Nickname}: {e.GetType().Name}: {e.Message}");
            }
        }

        var logLines = capture.Stop();
        output.WriteLine($"Systems: {gameData.Items.Systems.Count}, objects: {objectCount}, " +
                         $"bases: {gameData.Items.Bases.Count}, story missions: {missions.Count}, " +
                         $"warnings and errors: {logLines.Count}");

        Assert.Empty(failures);
        AssertNoNewLogLines(GameDataPaths.OutFolder(baseFolder), logLines);
    }

    private void AssertNoNewLogLines(string outFolder, IReadOnlyList<string> logLines)
    {
        var current = logLines.Distinct().Order(StringComparer.Ordinal).ToList();
        File.WriteAllLines(Path.Combine(outFolder, CurrentFile), current);

        var baselinePath = Path.Combine(outFolder, BaselineFile);
        Assert.True(File.Exists(baselinePath),
            $"No log baseline. Review {CurrentFile} in {outFolder}, then copy it to {BaselineFile}.");

        var baseline = File.ReadAllLines(baselinePath).ToHashSet(StringComparer.Ordinal);
        var currentSet = current.ToHashSet(StringComparer.Ordinal);
        var added = current.Where(line => !baseline.Contains(line)).ToList();
        var gone = baseline.Count(line => !currentSet.Contains(line));
        if (gone > 0)
            output.WriteLine($"{gone} baseline lines no longer occur.");
        Assert.True(added.Count == 0,
            $"{added.Count} new warning/error lines:{Environment.NewLine}{string.Join(Environment.NewLine, added)}");
    }
}

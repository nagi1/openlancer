using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Storyline;
using LibreLancer.Resources;
using LibreLancer.World;
using OpenLancer.Data;
using OpenLancer.Data.Overlay;
using OpenLancer.Runtime.Strings;

namespace OpenLancer.Runtime.Validation;

/// <summary>The engine lines of a base run, in arrival order, for each part of the load.</summary>
/// <param name="Load">The lines of the data load, as at the open of a profile.</param>
/// <param name="Systems">The lines of the loads of the star systems, as the Scene loads them.</param>
/// <param name="Missions">The lines of the loads of the story mission scripts.</param>
/// <param name="Failures">A line for each system or mission script that did not load.</param>
public sealed record BaseRun(IReadOnlyList<EngineLine> Load, IReadOnlyList<EngineLine> Systems,
    IReadOnlyList<EngineLine> Missions, IReadOnlyList<string> Failures)
{
    /// <summary>All lines: the load, the systems, then the missions.</summary>
    public IEnumerable<EngineLine> All => Load.Concat(Systems).Concat(Missions);
}

/// <summary>
/// The base run of the console filter (decision D516): the engine lines that a base folder gives without mods, and the
/// count of each key. The Console compares the engine lines of a profile with these counts, so a line shows only when
/// the mods cause it.
/// </summary>
/// <remarks>
/// <para>The run loads the folder as the editor does: the edit view of a profile without mods, the game data with a
/// <see cref="ServerResourceManager"/>, each star system into a headless world as the Scene loads it, and each story
/// mission script. It only reads the folder.</para>
/// <para>FLLog and the material map are global, so the editor never runs this load itself. <c>oltool engine-lines</c>
/// runs it in its own process.</para>
/// </remarks>
public static class BaseEngineLines
{
    /// <summary>Loads a base folder without mods and gives its engine warning and error lines.</summary>
    public static BaseRun Run(string folder)
    {
        var fs = ModOverlay.Build(folder, [], DllStringResourceWriter.Instance, null).CreateFileSystem();
        var failures = new List<string>();
        var resources = new ServerResourceManager(null, fs);
        // The resource manager is not disposable, but its physics shape pool is.
        using var shapes = resources.ConvexCollection;
        GameDataManager? data = null;
        var load = Capture(() =>
        {
            data = new GameDataManager(new GameItemDb(fs), resources);
            data.LoadData(null);
        });
        var systems = Capture(() =>
        {
            foreach (var source in data!.Items.Systems)
                Try($"System {source.Nickname}", () => LoadSystem(data, resources, source), failures);
        });
        var missions = Capture(() =>
        {
            foreach (var mission in data!.Items.Ini.Storyline.Missions)
                Try($"Mission {mission.Nickname}", () => LoadMission(data, mission), failures);
        });
        return new BaseRun(load, systems, missions, failures);
    }

    /// <summary>The count of each key (<see cref="EngineLine.Key"/>) of the lines.</summary>
    public static IReadOnlyDictionary<string, int> Count(IEnumerable<EngineLine> lines) =>
        lines.CountBy(l => l.Key).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);

    /// <summary>The counts as one JSON object: <c>{"key": count, ...}</c>, in the order of the keys.</summary>
    public static string ToJson(IReadOnlyDictionary<string, int> counts) =>
        JsonSerializer.Serialize(counts.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value));

    /// <summary>Reads the counts of <see cref="ToJson"/>.</summary>
    /// <exception cref="InvalidDataException">The text is not such an object.</exception>
    public static IReadOnlyDictionary<string, int> ReadJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ??
                   throw new InvalidDataException("The base run gave no counts.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The base run gave text that is not a JSON object of counts: {e.Message}", e);
        }
    }

    /// <summary>The engine warning and error lines of an action.</summary>
    private static IReadOnlyList<EngineLine> Capture(Action action)
    {
        using var capture = EngineLogCapture.Start();
        action();
        return capture.StopLines();
    }

    /// <summary>Runs one load. A load that throws gives its lines up to the failure, and a failure line.</summary>
    private static void Try(string what, Action action, List<string> failures)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failures.Add($"{what}: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Loads a clone of a system into a headless world, as the Scene loads a system (SceneView.Open).</summary>
    private static void LoadSystem(GameDataManager data, ResourceManager resources, StarSystem source)
    {
        var system = source.Clone();
        data.LoadAllSystem(system);
        using var world = new GameWorld(null, null, resources, () => 0, true)
        {
            DefaultObjectType = EquipmentType.Editor,
        };
        world.LoadSystem(system, resources, null, false);
    }

    private static void LoadMission(GameDataManager data, StoryMission mission)
    {
        if (data.Items.Ini.LoadMissionIni(mission) == null)
            throw new FileNotFoundException($"The file {mission.File} does not exist.");
    }
}

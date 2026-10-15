using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.IO;
using LibreLancer.Resources;
using LibreLancer.World;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.DataTests;

/// <summary>
/// The scene of one retail system without a GPU, as the editor holds it: a clone of the loaded system in a world with
/// physics and without a renderer, and an applier with a fake host. The applier gets the changes of the session.
/// </summary>
internal sealed class HeadlessScene : IDisposable
{
    private readonly EditSession session;

    public HeadlessScene(EditSession session, string system)
    {
        this.session = session;
        var game = RetailGame.Instance;
        Source = game.Data.Items.Systems.Get(system) ?? throw new ArgumentException($"No system {system}.", nameof(system));
        System = Source.Clone();
        World = new GameWorld(null, null, game.Resources, () => 0, true);
        World.LoadSystem(System, game.Resources, null, server: false);
        Objects = World.Objects.Where(o => o.SystemObject != null).ToDictionary(o => o.SystemObject!);
        Applier = new SceneApplier(session, game.Data.Items, game.Resources, System, World, Objects, Host);
        session.Changed += Applier.Note;
    }

    /// <summary>The system of the loaded data.</summary>
    public StarSystem Source { get; }

    /// <summary>The clone of <see cref="Source"/> that the scene shows.</summary>
    public StarSystem System { get; }

    public GameWorld World { get; }

    public Dictionary<SystemObject, GameObject> Objects { get; }

    public FakeSceneHost Host { get; } = new();

    public SceneApplier Applier { get; }

    public SystemObject Object(string nickname) => System.Objects.Single(o => o.Nickname == nickname);

    public Zone Zone(string nickname) => System.Zones.Single(z => z.Nickname == nickname);

    public LightSource Light(string nickname) => System.LightSources.Single(l => l.Nickname == nickname);

    /// <summary>The section of a scene item.</summary>
    public SectionRef SectionOf(object item) => Applier.SectionOf(item) ?? throw new ArgumentException($"{item} has no section.");

    public void Dispose()
    {
        session.Changed -= Applier.Note;
        World.Dispose();
    }
}

/// <summary>The host of a headless scene: it records each call. A test sets whether a reload runs or an edit is
/// open.</summary>
internal sealed class FakeSceneHost : ISceneHost
{
    public bool CanLoadModels { get; set; } = true;

    public bool EditOpen { get; set; }

    /// <summary>The effects of each <see cref="IndexChanged"/>, in order.</summary>
    public List<SceneEffect> IndexChanges { get; } = [];

    /// <summary>Runs at each <see cref="IndexChanged"/>, so a test can see what the index would read.</summary>
    public Action? OnIndexChanged { get; set; }

    public List<Diagnostic> Problems { get; } = [];

    public int LightLoads { get; private set; }

    public int ZoneMoves { get; private set; }

    public int VolumeLoads { get; private set; }

    public int StarsphereLoads { get; private set; }

    public void LightsChanged(StarSystem system) => LightLoads++;

    public void ZonesMoved() => ZoneMoves++;

    public void VolumesChanged(StarSystem system) => VolumeLoads++;

    public void StarspheresChanged(StarSystem system) => StarsphereLoads++;

    public void IndexChanged(SceneEffect effects)
    {
        IndexChanges.Add(effects);
        OnIndexChanged?.Invoke();
    }

    public void ProblemFound(Diagnostic problem) => Problems.Add(problem);
}

/// <summary>
/// The retail game data, loaded once for the tests of the live scene, with a resource manager without a GPU. The
/// scenes only read the data: each scene shows a clone of a system. A new empty mod gives the same files, so a session of
/// a <see cref="RetailWorkspace"/> has W equal to this data.
/// </summary>
internal sealed class RetailGame
{
    private static readonly Lazy<RetailGame> Loaded = new(() => new RetailGame(GameDataPaths.RequireBase()));

    static RetailGame() => AppHandler.ConsoleInit(silent: true);

    private RetailGame(string baseFolder)
    {
        var fs = FileSystem.FromPath(baseFolder);
        Resources = new ServerResourceManager(null, fs);
        Data = new GameDataManager(new GameItemDb(fs), Resources);
        Data.LoadData(null);
    }

    /// <summary>The data. Call <see cref="GameDataPaths.RequireBase"/> first, so that a test without the data
    /// skips.</summary>
    public static RetailGame Instance => Loaded.Value;

    public ServerResourceManager Resources { get; }

    public GameDataManager Data { get; }
}

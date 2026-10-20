using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using LibreLancer.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the live scene (section 16.2 of the design): each change of W shows in a headless Li01 scene,
/// also an undo, a redo, a restore of unsaved edits and a system change, with the effect order and the deferral of the
/// design. The numbers in the names are the proofs of the design.
/// </summary>
public sealed class SceneApplierRetailTests(ITestOutputHelper output)
{
    private const string Depot = "Li01_14_depot_water";

    /// <summary>A jump gate with <c>rotate = 0, 105, 0</c>.</summary>
    private const string Gate = "Li01_to_Li05";

    /// <summary>A lane ring in the middle of a lane.</summary>
    private const string LaneRing = "Li01_Trade_Lane_Ring_17";

    /// <summary>The ring at an end of a lane, with <c>tradelane_space_name = 196776</c> and
    /// <c>ids_info = 66170</c>.</summary>
    private const string LaneEnd = "Li01_Trade_Lane_Ring_16";

    /// <summary>A sphere zone with <c>size = 4100</c> and no rotate line.</summary>
    private const string Death = "Zone_Li01_Manhattan_death";

    /// <summary>A cylinder zone with <c>size = 750, 13088</c> and <c>rotate = -90, -46, 0</c>.</summary>
    private const string PatrolPath = "Zone_Li01_path_outcasts1_1";

    /// <summary>The sphere zone of an asteroid field, with <c>size = 2000</c>.</summary>
    private const string Field = "Zone_Li01_debris_practice_001";

    /// <summary>A nebula zone with <c>ids_name = 261208</c> and <c>ids_info = 65996</c>.</summary>
    private const string Nebula = "Zone_Li01_Badlands_Nebula";

    private const string ManhattanLight = "Light_Li01_Manhattan";
    private const string Station = "largestation1";

    /// <summary>The bound of the median of the runs of the Br01 catch-up after the warm-up of proof (6).</summary>
    private const double WarmBrMilliseconds = 20;

    /// <summary>The bound of the median of the first runs of the Br01 catch-up after the loop of proof (6).</summary>
    private const double ColdBrMilliseconds = 40;

    /// <summary>The bound of the first catch-up of each system in the loop of proof (6).</summary>
    private const double FirstCatchUpMilliseconds = 100;

    static SceneApplierRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// (1) Ten steps of a transaction move an object in the scene at each run, and the commit leaves it 1,000 units
    /// along +X: the depot at (-37675, 0, -27705). (2) An undo puts back its position, its rotation and the transform of
    /// its world object, bit for bit. (3) A redo puts back the moved values, bit for bit. A move keeps the rotation of a
    /// rotated object, also in its world object.
    /// </summary>
    [Theory]
    [InlineData(Depot, false)]
    [InlineData(Gate, true)]
    public void P01To03TenStepsMoveAnObjectAndAnUndoAndARedoPutItBackBitForBit(string nickname, bool rotated)
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        Assert.Equal(0, scene.Applier.CatchUp());
        var obj = scene.Object(nickname);
        var gameObject = scene.Objects[obj];
        var section = scene.SectionOf(obj);
        var start = obj.Position;
        Assert.Equal(rotated, obj.Rotation != Quaternion.Identity);
        var rotation = RotationBits(obj, gameObject);
        var before = TransformBits(obj, gameObject);

        using (var transaction = session.Begin("Move"))
        {
            for (var step = 1; step <= 10; step++)
            {
                transaction.Apply(Pos(section, start + new Vector3(100 * step, 0, 0)));
                scene.Applier.Run();
                Assert.Equal(start + new Vector3(100 * step, 0, 0), obj.Position);
                Assert.Equal(obj.Position, gameObject.Transform.Position);
                Assert.Equal(rotation, RotationBits(obj, gameObject));
            }
            transaction.Commit();
        }
        scene.Applier.Run();
        Assert.Equal(start + new Vector3(1000, 0, 0), obj.Position);
        Assert.Equal(rotation, RotationBits(obj, gameObject));
        var after = TransformBits(obj, gameObject);

        session.Undo();
        scene.Applier.Run();
        Assert.Equal(before, TransformBits(obj, gameObject));
        session.Redo();
        scene.Applier.Run();
        Assert.Equal(after, TransformBits(obj, gameObject));
        Assert.Empty(scene.Applier.Problems(section));
    }

    /// <summary>(4) The same for a zone: the zone and the zone lookup of the world move, the move keeps the rotation and
    /// the size, and an undo gives the loaded zone again.</summary>
    [Theory]
    [InlineData(Death, false)]
    [InlineData(PatrolPath, true)]
    public void P04TenStepsMoveAZoneAndAnUndoGivesTheLoadedZone(string nickname, bool rotated)
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var zone = scene.Zone(nickname);
        var loaded = scene.Source.Zones.Single(z => z.Nickname == nickname);
        Assert.Equal(rotated, loaded.RotationMatrix != Matrix4x4.Identity);
        var section = scene.SectionOf(zone);
        var moved = loaded.Position + new Vector3(10_000, 0, 0);

        using (var transaction = session.Begin("Move zone"))
        {
            for (var step = 1; step <= 10; step++)
            {
                transaction.Apply(Pos(section, loaded.Position + new Vector3(1000 * step, 0, 0)));
                scene.Applier.Run();
                Assert.Equal(loaded.Position + new Vector3(1000 * step, 0, 0), zone.Position);
                Assert.Equal(RotationBits(loaded), RotationBits(zone));
            }
            transaction.Commit();
        }
        scene.Applier.Run();
        Assert.Equal(moved, zone.Position);
        Assert.Equal(loaded.Size, zone.Size);
        Assert.True(scene.Host.ZoneMoves >= 10);
        // The zone lookup of the world finds the zone at its new place, and not at its old place.
        Assert.Contains(zone, ZonesAt(scene, moved));
        Assert.DoesNotContain(zone, ZonesAt(scene, loaded.Position));

        session.Undo();
        scene.Applier.Run();
        Assert.True(zone.ZonesEqual(loaded));
        Assert.Contains(zone, ZonesAt(scene, loaded.Position));
        session.Redo();
        scene.Applier.Run();
        Assert.Equal(moved, zone.Position);
        Assert.Equal(RotationBits(loaded), RotationBits(zone));
    }

    /// <summary>(4) The same for a light: the renderer loads the lights again, and an undo gives the loaded
    /// light.</summary>
    [Fact]
    public void P04TenStepsMoveALightAndAnUndoGivesTheLoadedLight()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var light = scene.Light(ManhattanLight);
        var loaded = scene.Source.LightSources.Single(l => l.Nickname == ManhattanLight).Light;
        var section = scene.SectionOf(light);

        using (var transaction = session.Begin("Move light"))
        {
            for (var step = 1; step <= 10; step++)
            {
                transaction.Apply(Pos(section, -33520 + 50 * step, 0, -30946));
                scene.Applier.Run();
                Assert.Equal(new Vector3(-33520 + 50 * step, 0, -30946), light.Light.Position);
            }
            transaction.Commit();
        }
        scene.Applier.Run();
        Assert.Equal(10, scene.Host.LightLoads);
        var after = light.Light;

        session.Undo();
        scene.Applier.Run();
        Assert.True(light.Light == loaded);
        session.Redo();
        scene.Applier.Run();
        Assert.True(light.Light == after);
    }

    /// <summary>
    /// (5) The unsaved edits stay in the scene across a system change: the next open of Li01 catches up W at its first
    /// run, with each effect of the edits (see <see cref="AssertTheUnsavedEditsShow"/>).
    /// </summary>
    [Fact]
    public void P05TheUnsavedEditsStayAcrossASystemChange()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using (var li01 = new HeadlessScene(session, "Li01"))
        {
            li01.Applier.Run();
            MakeTheUnsavedEdits(session);
            li01.Applier.Run();
        }
        using (var li02 = new HeadlessScene(session, "Li02"))
            Assert.Equal(0, li02.Applier.CatchUp());

        using var again = new HeadlessScene(session, "Li01");
        var ringModel = again.Objects[again.Object(LaneRing)].Model;
        // The zone keeps the outline of its loaded size until its shape changes.
        again.Zone(Field).OutlineMesh();
        Assert.Equal(new Vector3(-38675, 0, -27705), again.Object(Depot).Position);
        Assert.True(again.Applier.HasPending);
        // The editor calls CatchUp at the open. Without it, the first run catches up.
        again.Applier.Run();
        AssertTheUnsavedEditsShow(again, ringModel);
        Assert.False(again.Applier.HasPending);
    }

    /// <summary>
    /// (6) Without edits, the catch-up of each of the 53 retail systems finds no difference and queues no deferred
    /// effect, so no model loads again. The catch-up of Br01, the largest system file, takes less than 20 ms when the
    /// runtime has optimized its code.
    /// </summary>
    /// <remarks>
    /// The first catch-ups of a process are slower. The runtime first compiles the code without optimization (tier 0)
    /// and optimizes it later (tiered compilation). The compile ran in the first catch-up of the loop only when no test
    /// before ran the code, so that bound depended on the order of the tests. Now one catch-up warms up the code before
    /// the loop. The test bounds the first catch-up of each system in a new scene, and the first runs of Br01 after the
    /// loop. The gates measure as <see cref="Timing"/> tells.
    /// </remarks>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void P06TheCatchUpOfEachRetailSystemWithoutEditsFindsNoDifference()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        var systems = RetailGame.Instance.Data.Items.Systems.Select(s => s.Nickname).ToList();
        var differences = 0;
        var pending = 0;
        var slowest = (System: "", Milliseconds: 0.0);
        using (var warmUp = new HeadlessScene(session, systems[0]))
            warmUp.Applier.CatchUp();
        // A first catch-up runs only once in a scene. So each try of the gate makes a new scene of the system.
        foreach (var nickname in systems)
        {
            var time = Timing.Gate(TimeSpan.FromMilliseconds(FirstCatchUpMilliseconds), () =>
            {
                using var scene = new HeadlessScene(session, nickname);
                var catchUp = Timing.Measure(() => differences += scene.Applier.CatchUp());
                pending += scene.Applier.HasPending ? 1 : 0;
                return catchUp;
            });
            if (time.TotalMilliseconds > slowest.Milliseconds)
                slowest = (nickname, time.TotalMilliseconds);
        }

        // Br01 again. The editor runs a catch-up when the user opens a system. In a long session, the runtime has
        // optimized the code by then. Just after the loop, much of that code is not optimized yet. So the test runs the
        // catch-up 30 times and gives the runtime a pause, as a benchmark warms up, then it measures seven runs. A
        // collection before each measure keeps the garbage of the 53 worlds out.
        using var br01 = new HeadlessScene(session, "Br01");
        var sections = session.Sections(session.SystemPath("Br01")!).Count;
        var br01Differences = 0;
        double[] Measure(int count)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var priority = Timing.HighPriority();
            var times = new double[count];
            for (var i = 0; i < times.Length; i++)
            {
                var timer = Stopwatch.StartNew();
                br01Differences += br01.Applier.CatchUp();
                times[i] = timer.Elapsed.TotalMilliseconds;
            }
            return times;
        }
        static string Times(double[] times) =>
            $"{string.Join(", ", times.Select(t => t.ToString("0.0")))} ms, median {Median(times):0.0} ms";

        // The first runs show the code before the runtime optimizes it, so they cannot run again.
        var cold = Measure(30)[..7];
        Thread.Sleep(500);
        var warm = new List<double[]>();
        var warmBest = Timing.Gate(TimeSpan.FromMilliseconds(WarmBrMilliseconds), () =>
        {
            warm.Add(Measure(7));
            return TimeSpan.FromMilliseconds(Median(warm[^1]));
        });
        output.WriteLine($"systems {systems.Count}, differences {differences}, systems with deferred effects {pending}, " +
                         $"slowest first catch-up {slowest.System} {slowest.Milliseconds:0.0} ms. Br01 ({sections} " +
                         $"sections) first runs: {Times(cold)}. Br01 after the warm-up: " +
                         $"{string.Join("; ", warm.Select(Times))}.");
        Assert.Equal(53, systems.Count);
        Assert.Equal(0, differences);
        Assert.Equal(0, pending);
        Assert.Equal(0, br01Differences);
        Assert.Equal(764, sections);
        Assert.True(warmBest.TotalMilliseconds < WarmBrMilliseconds,
            $"The catch-up of Br01 took {warmBest.TotalMilliseconds} ms.");
        Assert.True(Median(cold) < ColdBrMilliseconds, $"The first catch-ups of Br01 took {Median(cold)} ms.");
        Assert.True(slowest.Milliseconds < FirstCatchUpMilliseconds,
            $"The first catch-up of {slowest.System} took {slowest.Milliseconds} ms.");
    }

    /// <summary>
    /// The nebulae and fields of Li01 are scene items with their sections (section 12 of the M22 design). The 2 nebulae
    /// and 9 fields give 11 sections, each keyed by the zone of the item. The applier maps each section back to its item.
    /// They are not sections of the system, so a change of one never runs a system effect.
    /// </summary>
    [Fact]
    public void EachNebulaAndFieldOfLi01HasItsSection()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        Assert.Equal(0, scene.Applier.CatchUp());
        object[] items = [.. scene.System.Nebulae, .. scene.System.AsteroidFields];

        Assert.Equal((2, 9), (scene.System.Nebulae.Count, scene.System.AsteroidFields.Count));
        var sections = items.Select(scene.SectionOf).ToList();
        Assert.Equal(11, sections.Distinct().Count());
        foreach (var (item, section) in items.Zip(sections))
        {
            var zone = item is Nebula nebula ? nebula.Zone! : ((AsteroidField)item).Zone!;
            Assert.Equal((item is Nebula ? "Nebula" : "Asteroids", zone.Nickname), (section.Id.Type, section.Id.Key));
            Assert.Same(item, scene.Applier.ItemOf(section));
        }

        // The Badlands nebula and its field share a zone, and each has its own section.
        var badlands = scene.System.Nebulae.Single(n => n.Zone!.Nickname == Nebula);
        Assert.Equal(new SectionIdentity("Nebula", Nebula), scene.SectionOf(badlands).Id);
    }

    /// <summary>
    /// (7) An archetype edit while the game data loads again changes the object at once (Data), but its model loads only
    /// after the reload ends (Reinit, review finding F5).
    /// </summary>
    [Fact]
    public void P07AnArchetypeEditDuringAReloadLoadsTheModelAfterTheReload()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var depot = scene.Object(Depot);
        var gameObject = scene.Objects[depot];
        var model = gameObject.Model;
        Assert.NotNull(model);
        scene.Host.CanLoadModels = false;

        session.Execute(Text(scene.SectionOf(depot), "archetype", Station));
        scene.Applier.Run();
        Assert.Equal(Station, depot.Archetype!.Nickname);
        Assert.Same(model, gameObject.Model);
        Assert.True(scene.Applier.HasPending);
        scene.Applier.Run();
        Assert.Same(model, gameObject.Model);

        scene.Host.CanLoadModels = true;
        scene.Applier.Run();
        Assert.NotSame(model, gameObject.Model);
        Assert.False(scene.Applier.HasPending);
        Assert.Equal(depot.Position, gameObject.Transform.Position);
    }

    /// <summary>
    /// (8) A restore of unsaved edits lists no section, so the scene catches up while Li01 is open, with each effect of
    /// the edits (see <see cref="AssertTheUnsavedEditsShow"/>). The undo of the restore gives the loaded scene again,
    /// and the redo gives the edits again.
    /// </summary>
    [Fact]
    public void P08ARestoreOfUnsavedEditsShowsWhileTheSystemIsOpen()
    {
        using var workspace = Workspace();
        var first = workspace.Open();
        MakeTheUnsavedEdits(first);
        Assert.Empty(first.WriteRecovery());
        first.Dispose();

        var session = workspace.Open();
        Assert.True(session.HasRecovery);
        using var scene = new HeadlessScene(session, "Li01");
        Assert.Equal(0, scene.Applier.CatchUp());
        var depot = scene.Object(Depot);
        Assert.Equal(new Vector3(-38675, 0, -27705), depot.Position);
        var ringModel = scene.Objects[scene.Object(LaneRing)].Model;
        scene.Zone(Field).OutlineMesh();

        Assert.Empty(session.RestoreRecovery());
        scene.Applier.Run();
        AssertTheUnsavedEditsShow(scene, ringModel);

        session.Undo();
        scene.Applier.Run();
        Assert.Equal(new Vector3(-38675, 0, -27705), depot.Position);
        Assert.Equal(78, LaneRings(scene));
        Assert.True(scene.Zone(Field).ZonesEqual(scene.Source.Zones.Single(z => z.Nickname == Field)));
        Assert.True(scene.Light(ManhattanLight).Light ==
                    scene.Source.LightSources.Single(l => l.Nickname == ManhattanLight).Light);
        Assert.Equal(2, scene.Host.VolumeLoads);
        session.Redo();
        scene.Applier.Run();
        Assert.Equal(new Vector3(-37675, 0, -27705), depot.Position);
        Assert.Equal(77, LaneRings(scene));
    }

    /// <summary>
    /// (9) Each rename of the depot and its undo tell the host that a name changed: the second rename changes only the
    /// text, and lists no section. W then names the depot with the first new name, and the scene object keeps its
    /// loaded text id, because an unsaved text has no id.
    /// </summary>
    [Fact]
    public void P09EachRenameAndItsUndoChangeTheLabelsAndTheSceneKeepsTheLoadedTextId()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var depot = scene.Object(Depot);
        var section = scene.SectionOf(depot);
        var loadedId = depot.IdsName;
        Assert.Equal(261161, loadedId);

        foreach (var name in new[] { "Water depot A", "Water depot B" })
        {
            session.Execute(ItemEdits.SetText(session, section, "ids_name", name));
            scene.Applier.Run();
        }
        session.Undo();
        scene.Applier.Run();

        Assert.Equal(3, scene.Host.IndexChanges.Count);
        Assert.All(scene.Host.IndexChanges, e => Assert.True((e & SceneEffect.Label) != 0, e.ToString()));
        var value = session.Get(section)!.Lines("ids_name")[^1].Values[0];
        Assert.True(StringPlaceholder.TryParse(value, out var placeholder));
        Assert.Equal("Water depot A", session.Text(placeholder));
        Assert.Equal(loadedId, depot.IdsName);
        Assert.Empty(scene.Applier.Problems(section));
    }

    /// <summary>
    /// (10) An unknown attenuation curve does not throw. The light keeps its last state, the problem names the curve, and
    /// the host gets the problem once, after the edit closes. A problem that goes and comes back is new again.
    /// </summary>
    [Fact]
    public void P10AnUnknownCurveKeepsTheLightAndNamesTheCurve()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var light = scene.Light(ManhattanLight);
        var section = scene.SectionOf(light);
        var before = light.Light;
        scene.Host.EditOpen = true;

        session.Execute(Text(section, "atten_curve", "no_such_curve"));
        scene.Applier.Run();
        Assert.True(light.Light == before);
        Assert.Null(light.AttenuationCurveName);
        var problem = Assert.Single(scene.Applier.Problems(section));
        Assert.Contains("'no_such_curve'", problem.Message);
        Assert.Equal("atten_curve", problem.Key);
        Assert.Empty(scene.Host.Problems);

        scene.Host.EditOpen = false;
        scene.Applier.Run();
        scene.Applier.Run();
        Assert.Equal(problem, Assert.Single(scene.Host.Problems));

        session.Undo();
        scene.Applier.Run();
        Assert.Empty(scene.Applier.Problems(section));
        Assert.True(light.Light == before);
        session.Redo();
        scene.Applier.Run();
        Assert.Equal([problem, problem], scene.Host.Problems);
    }

    /// <summary>
    /// (11) A lane ring that gets a station archetype leaves its lane at once: when the host builds the index again, the
    /// system has one lane ring less, while the model still waits for the edit to end.
    /// </summary>
    [Fact]
    public void P11ALaneRingWithAStationArchetypeLeavesItsLaneBeforeItsModelLoads()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var ring = scene.Object(LaneRing);
        Assert.Equal(78, LaneRings(scene));
        var ringsAtIndex = -1;
        scene.Host.OnIndexChanged = () => ringsAtIndex = LaneRings(scene);
        scene.Host.EditOpen = true;

        session.Execute(Text(scene.SectionOf(ring), "archetype", Station));
        scene.Applier.Run();
        Assert.Equal(77, ringsAtIndex);
        Assert.True((scene.Host.IndexChanges.Single() & SceneEffect.Lanes) != 0);
        Assert.Null(ring.Dock);
        Assert.True(scene.Applier.HasPending);
    }

    /// <summary>(12) A change of W only records the change. The scene changes at the next run.</summary>
    [Fact]
    public void P12ANoteMovesNothingAndTheRunMovesTheDepot()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var depot = scene.Object(Depot);

        session.Execute(Pos(scene.SectionOf(depot), -37675, 0, -27705));
        Assert.Equal(new Vector3(-38675, 0, -27705), depot.Position);
        Assert.Equal(new Vector3(-38675, 0, -27705), scene.Objects[depot].Transform.Position);
        Assert.True(scene.Applier.HasPending);
        scene.Applier.Run();
        Assert.Equal(new Vector3(-37675, 0, -27705), depot.Position);
        Assert.Equal(depot.Position, scene.Objects[depot].Transform.Position);
    }

    /// <summary>
    /// A size change of the zone of an asteroid field changes the zone at once, and the renderer builds the fields again
    /// only after the edit ends (Volume). A size change of another zone builds no field.
    /// </summary>
    [Fact]
    public void AFieldZoneBuildsTheFieldsAgainAfterTheEdit()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var field = scene.Zone(Field);
        scene.Host.EditOpen = true;

        session.Execute(EditCommand.SetLines(scene.SectionOf(field), "size", [[new Int32Value(3000)]]));
        scene.Applier.Run();
        Assert.Equal(3000, field.Size.X);
        Assert.Equal(0, scene.Host.VolumeLoads);
        scene.Host.EditOpen = false;
        scene.Applier.Run();
        Assert.Equal(1, scene.Host.VolumeLoads);

        session.Execute(EditCommand.SetLines(scene.SectionOf(scene.Zone(Death)), "size", [[new Int32Value(5000)]]));
        scene.Applier.Run();
        Assert.Equal(5000, scene.Zone(Death).Size.X);
        Assert.Equal(1, scene.Host.VolumeLoads);
        Assert.False(scene.Applier.HasPending);
    }

    /// <summary>
    /// A size edit changes the role of a zone only when the zone starts or stops being a trade lane zone (see
    /// <see cref="ZoneRules.IsTradeLaneZone"/>). Only then does the index build again, so the steps of a scale drag of
    /// another zone do not build the index and the Hierarchy again (review of fix S2).
    /// </summary>
    [Fact]
    public void ASizeEditBuildsTheIndexAgainOnlyWhenTheTradeLaneRuleChanges()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        scene.Host.IndexChanges.Clear();
        var lane = scene.System.Zones.First(ZoneRules.IsTradeLaneZone);
        IReadOnlyList<ValueBase>[] Size(int x, int y, int z) =>
            [[new Int32Value(x), new Int32Value(y), new Int32Value(z)]];

        // A sphere zone gets a new size, and the trade lane zone gets longer.
        session.Execute(EditCommand.SetLines(scene.SectionOf(scene.Zone(Death)), "size", [[new Int32Value(5000)]]));
        session.Execute(EditCommand.SetLines(scene.SectionOf(lane), "size", Size(1000, 1000, 50_000)));
        scene.Applier.Run();
        Assert.Equal(50_000, lane.Size.Z);
        Assert.Empty(scene.Host.IndexChanges);

        // Now the zone is only 5 times longer than wide, so it is not a trade lane zone.
        session.Execute(EditCommand.SetLines(scene.SectionOf(lane), "size", Size(1000, 1000, 5000)));
        scene.Applier.Run();
        Assert.False(ZoneRules.IsTradeLaneZone(lane));
        Assert.Equal(SceneEffect.Roles, Assert.Single(scene.Host.IndexChanges));

        session.Undo();
        scene.Applier.Run();
        Assert.True(ZoneRules.IsTradeLaneZone(lane));
        Assert.Equal([SceneEffect.Roles, SceneEffect.Roles], scene.Host.IndexChanges);
    }

    /// <summary>
    /// The sections of the fields of the system: a color shows at once (Environment), a starsphere loads after the
    /// edit (Starsphere), and a new name of the system tells the host. The scene system keeps its loaded text id.
    /// </summary>
    [Fact]
    public void TheSystemSectionsChangeTheColorsTheStarspheresAndTheName()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var path = session.SystemPath("Li01")!;
        var loadedId = scene.System.IdsName;
        scene.Host.EditOpen = true;

        session.Execute(EditCommand.SetLines(new SectionRef(path, new SectionIdentity("Ambient", null)), "color",
            [[new Int32Value(60), new Int32Value(50), new Int32Value(80)]]));
        session.Execute(Text(new SectionRef(path, new SectionIdentity("Background", null)), "basic_stars",
            @"solar\starsphere\starsphere_br01_stars.cmp"));
        scene.Applier.Run();
        Assert.Equal(new Color3f(60 / 255f, 50 / 255f, 80 / 255f), scene.System.AmbientColor);
        Assert.Equal(@"solar\starsphere\starsphere_br01_stars.cmp", scene.System.StarsBasic!.SourcePath);
        Assert.Equal(0, scene.Host.StarsphereLoads);
        scene.Host.EditOpen = false;
        scene.Applier.Run();
        Assert.Equal(1, scene.Host.StarsphereLoads);

        session.Execute(ItemEdits.SetText(session, session.SystemSection("Li01")!, "strid_name", "New New York"));
        scene.Applier.Run();
        Assert.True((scene.Host.IndexChanges[^1] & SceneEffect.Label) != 0);
        Assert.Equal(loadedId, scene.System.IdsName);
    }

    /// <summary>
    /// The <c>[system]</c> section of the system in <c>universe.ini</c>: a number of <c>strid_name</c> names the system
    /// without a change of the text, so the scene system gets the id and the host reads the names again. A new
    /// <c>navmapscale</c> goes into the scene system. The <c>ids_info</c> of the system keeps its loaded id while W
    /// holds a placeholder.
    /// </summary>
    [Fact]
    public void TheUniverseSectionGivesTheNameAndTheScaleOfTheSystem()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var universe = session.SystemSection("Li01")!;
        Assert.Equal((196609, 66106), (scene.System.IdsName, scene.System.IdsInfo));
        session.Execute(ItemEdits.SetText(session, universe, "ids_info", ItemEdits.ToInfocard("The home of Liberty.")));
        scene.Applier.Run();
        scene.Host.IndexChanges.Clear();

        session.Execute(EditCommand.SetLines(universe, "strid_name", [[new Int32Value(196610)]]));
        session.Execute(EditCommand.SetLines(universe, "navmapscale", [[new SingleValue(1.5f, null)]]));
        scene.Applier.Run();

        Assert.Equal(SceneEffect.Label, Assert.Single(scene.Host.IndexChanges));
        Assert.Equal((196610, 66106), (scene.System.IdsName, scene.System.IdsInfo));
        Assert.Equal(1.5f, scene.System.NavMapScale);
        Assert.Empty(scene.Applier.Problems(universe));
    }

    /// <summary>
    /// A rename through the text of the mod keeps the loaded text id in the scene for each text key: the ids_name and
    /// the ids_info of a zone, and the tradelane_space_name and the ids_info of a lane ring. W names them through
    /// placeholders, and an unsaved text has no id.
    /// </summary>
    [Fact]
    public void ARenameKeepsTheLoadedTextIdsOfAZoneAndALaneRing()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var nebula = scene.Zone(Nebula);
        var ring = scene.Object(LaneEnd);
        var loaded = (nebula.IdsName, nebula.IdsInfo, ring.TradelaneSpaceName, ring.IdsInfo);
        Assert.Equal((261208, 65996, 196776, 66170), loaded);

        session.Execute(ItemEdits.SetText(session, scene.SectionOf(nebula), "ids_name", "Badlands B"));
        session.Execute(ItemEdits.SetText(session, scene.SectionOf(nebula), "ids_info", ItemEdits.ToInfocard("Blue.")));
        session.Execute(ItemEdits.SetText(session, scene.SectionOf(ring), "tradelane_space_name", "Manhattan B"));
        session.Execute(ItemEdits.SetText(session, scene.SectionOf(ring), "ids_info", ItemEdits.ToInfocard("A ring.")));
        scene.Applier.Run();

        Assert.Equal(loaded, (nebula.IdsName, nebula.IdsInfo, ring.TradelaneSpaceName, ring.IdsInfo));
        Assert.True((Assert.Single(scene.Host.IndexChanges) & SceneEffect.Label) != 0);
        Assert.Empty(scene.Applier.Problems(scene.SectionOf(nebula)));
        Assert.Empty(scene.Applier.Problems(scene.SectionOf(ring)));
    }

    /// <summary>The engine reads the last line of a key. So a second pos line moves the depot, and an undo of the second
    /// line moves it back.</summary>
    [Fact]
    public void TheLastLineOfAKeyApplies()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var depot = scene.Object(Depot);
        var section = scene.SectionOf(depot);

        session.Execute(EditCommand.SetLines(section, "pos", [Line(-38675, 0, -27705), Line(-37675, 0, -27705)]));
        scene.Applier.Run();
        Assert.Equal(new Vector3(-37675, 0, -27705), depot.Position);
        session.Undo();
        scene.Applier.Run();
        Assert.Equal(new Vector3(-38675, 0, -27705), depot.Position);
    }

    /// <summary>A line that the engine cannot read keeps the last valid state of the object, with a problem on its key.
    /// The correction applies it.</summary>
    [Fact]
    public void ALineWithAnErrorKeepsTheLastValidState()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var depot = scene.Object(Depot);
        var section = scene.SectionOf(depot);

        session.Execute(EditCommand.SetLinesFromText(section, "pos", ["abc, 0, -27705"]));
        scene.Applier.Run();
        Assert.Equal(new Vector3(-38675, 0, -27705), depot.Position);
        Assert.Equal("pos", Assert.Single(scene.Applier.Problems(section)).Key);

        session.Execute(Pos(section, -37675, 0, -27705));
        scene.Applier.Run();
        Assert.Equal(new Vector3(-37675, 0, -27705), depot.Position);
        Assert.Empty(scene.Applier.Problems(section));
    }

    /// <summary>
    /// A structural change of another file, for example a new item from a template, changes nothing in the scene. A
    /// structural change of the system file changes the items at once (decision D248 of the M3 design): a new section
    /// gets its item, and the item of a deleted section goes. The other items keep their sections and their problems, so a
    /// line with an error keeps one problem. An undo of the delete gives the same item again, with its section and the
    /// problem of its line.
    /// </summary>
    [Fact]
    public void OnlyAStructuralChangeOfTheSystemChangesTheItems()
    {
        using var workspace = Workspace();
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var depot = scene.SectionOf(scene.Object(Depot));
        session.Execute(EditCommand.SetLinesFromText(depot, "pos", ["abc, 0, -27705"]));
        scene.Applier.Run();
        Assert.Equal("pos", Assert.Single(scene.Applier.Problems(depot)).Key);

        var gun = session.Find(ItemFileKind.Equipment, "li_gun01_mark01")!;
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, gun, "ol_scene_gun")));
        Assert.False(scene.Applier.HasPending);

        var added = new SectionRef(scene.Applier.SystemPath!, new SectionIdentity("Object", "ol_scene_depot"));
        session.Execute(EditCommand.AddSection(added.Path, "Object",
            [new("nickname", [new StringValue("ol_scene_depot")]), new("archetype", [new StringValue("depot_water")])]));
        Assert.True(scene.Applier.HasPending);
        scene.Applier.Run();
        Assert.Equal(added, scene.SectionOf(scene.Object("ol_scene_depot")));
        Assert.Empty(scene.Applier.Problems(added));
        Assert.Equal("pos", Assert.Single(scene.Applier.Problems(depot)).Key);
        Assert.Equal(new Vector3(-38675, 0, -27705), scene.Object(Depot).Position);

        session.Execute(EditCommand.DeleteSection(depot));
        scene.Applier.Run();
        Assert.DoesNotContain(scene.System.Objects, o => o.Nickname == Depot);
        session.Undo();
        scene.Applier.Run();
        Assert.Equal(depot, scene.Applier.SectionOf(scene.Object(Depot)));
        Assert.Equal(new Vector3(-38675, 0, -27705), scene.Object(Depot).Position);
        Assert.Equal("pos", Assert.Single(scene.Applier.Problems(depot)).Key);
    }

    /// <summary>
    /// The unsaved edits of the catch-up proofs (5) and (8). Each one has another effect: a move of the depot
    /// (Transform), a station archetype for a lane ring (Data, Reinit and Lanes), a new size of the zone of a field
    /// (Volume) and a new color of a light (Light).
    /// </summary>
    private static void MakeTheUnsavedEdits(EditSession session)
    {
        session.Execute(Pos(Li01Section(session, "Object", Depot), -37675, 0, -27705));
        session.Execute(Text(Li01Section(session, "Object", LaneRing), "archetype", Station));
        session.Execute(EditCommand.SetLines(Li01Section(session, "Zone", Field), "size", [[new Int32Value(3000)]]));
        session.Execute(EditCommand.SetLines(Li01Section(session, "LightSource", ManhattanLight), "color",
            [Line(255, 0, 0)]));
    }

    /// <summary>
    /// The scene shows each edit of <see cref="MakeTheUnsavedEdits"/> after a catch-up: the depot and its world object
    /// moved, the lane ring has the model of its new archetype and left its lane (one IndexChanged with Lanes), the
    /// renderer built the field again and the zone outline has the new size, and the renderer loaded the light with its
    /// new color.
    /// </summary>
    /// <param name="ringModel">The model of the lane ring before the catch-up.</param>
    private static void AssertTheUnsavedEditsShow(HeadlessScene scene, object? ringModel)
    {
        var depot = scene.Object(Depot);
        Assert.Equal(new Vector3(-37675, 0, -27705), depot.Position);
        Assert.Equal(depot.Position, scene.Objects[depot].Transform.Position);

        var ring = scene.Object(LaneRing);
        Assert.Equal(Station, ring.Archetype!.Nickname);
        Assert.NotSame(ringModel, scene.Objects[ring].Model);
        Assert.Null(ring.Dock);
        Assert.Equal(77, LaneRings(scene));
        Assert.True((Assert.Single(scene.Host.IndexChanges) & SceneEffect.Lanes) != 0);

        var field = scene.Zone(Field);
        Assert.Equal(3000, field.Size.X);
        Assert.Equal(1, scene.Host.VolumeLoads);
        Assert.Equal(field.Clone().OutlineMesh(), field.OutlineMesh());

        Assert.Equal(1, scene.Host.LightLoads);
        Assert.Equal(new Color3f(1, 0, 0), scene.Light(ManhattanLight).Light.Color);
    }

    private static double Median(double[] values) => values.Order().ElementAt(values.Length / 2);

    /// <summary>A section of Li01 in W, found without a scene.</summary>
    private static SectionRef Li01Section(EditSession session, string type, string nickname) =>
        new(session.SystemPath("Li01")!, new SectionIdentity(type, nickname));

    private static RetailWorkspace Workspace() => new(GameDataPaths.RequireBase(), "m2proof");

    private static ValueBase[] Line(int x, int y, int z) => [new Int32Value(x), new Int32Value(y), new Int32Value(z)];

    private static EditCommand Pos(SectionRef section, int x, int y, int z) =>
        EditCommand.SetLines(section, "pos", [Line(x, y, z)]);

    /// <summary>A pos line of a retail position, which has integral values.</summary>
    private static EditCommand Pos(SectionRef section, Vector3 position) =>
        Pos(section, (int)position.X, (int)position.Y, (int)position.Z);

    private static EditCommand Text(SectionRef section, string key, string text) =>
        EditCommand.SetLines(section, key, [[new StringValue(text)]]);

    private static int LaneRings(HeadlessScene scene) => scene.System.Objects.Count(o => o.Dock is { Kind: DockKinds.Tradelane });

    private static Zone[] ZonesAt(HeadlessScene scene, Vector3 position)
    {
        var zones = new System.Collections.Generic.List<Zone>();
        scene.World.Zones!.ZonesAtPosition(position, zones.Add);
        return zones.ToArray();
    }

    /// <summary>The bits of each float of a vector, a quaternion or a matrix, so that a compare finds a change of the
    /// sign of zero.</summary>
    private static int[] Bits<T>(T value) where T : unmanaged =>
        MemoryMarshal.Cast<T, int>(MemoryMarshal.CreateSpan(ref value, 1)).ToArray();

    /// <summary>The bits of the position and the rotation of an object and of its world object.</summary>
    private static int[] TransformBits(SystemObject obj, GameObject gameObject) =>
        [..Bits(obj.Position), ..RotationBits(obj, gameObject), ..Bits(gameObject.Transform.Position)];

    /// <summary>The bits of the rotation of an object and of its world object.</summary>
    private static int[] RotationBits(SystemObject obj, GameObject gameObject) =>
        [..Bits(obj.Rotation), ..Bits(gameObject.Transform.Orientation)];

    /// <summary>The bits of the rotation of a zone: the matrix and the angles.</summary>
    private static int[] RotationBits(Zone zone) => [..Bits(zone.RotationMatrix), ..Bits(zone.RotationAngles)];
}

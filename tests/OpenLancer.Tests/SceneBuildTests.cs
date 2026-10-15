using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using OpenLancer.Data;
using OpenLancer.Runtime.Scene;
using Xunit;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.Tests;

/// <summary>
/// The engine views of the sections of a star system: the builders follow the engine, a light with an unknown curve
/// gives a problem and does not throw, and the compare rules cover each field. One test captures the global engine
/// log, so the class runs in the collection of such tests.
/// </summary>
[Collection(nameof(EngineLogCollection))]
public sealed class SceneBuildTests
{
    private const string Sections = """
        [Object]
        nickname = Li01_depot
        archetype = depot
        pos = 100, 0, -200
        rotate = 0, 105, 0
        dock_with = Li01_01_Base
        space_costume = robot_body_a, pi_pirate5_head
        ids_name = 196609

        [Object]
        nickname = Li01_to_Li05
        archetype = depot
        goto = Li05, Li05_to_Li01, gate_tunnel_bretonia

        [Object]
        nickname = Li01_lost
        archetype = no_such_archetype

        [Object]
        nickname = Li01_without_archetype

        [Zone]
        nickname = Zone_ring
        shape = RING
        size = 3000, 700, 200
        rotate = 90, 0, 0

        [LightSource]
        nickname = Light_curve
        pos = 1, 2, 3
        color = 255, 128, 0
        range = 50000
        atten_curve = curve

        [LightSource]
        nickname = Light_defaults

        [LightSource]
        nickname = Light_sun
        type = DIRECTIONAL
        direction = 0, -1, 0
        attenuation = 1, 0, 0.5
        """;

    private static readonly Dictionary<string, Section> Parsed =
        IniFile.ParseFile("li01.ini", new MemoryStream(Encoding.ASCII.GetBytes(Sections)))
            .ToDictionary(s => s["nickname"]![0].ToString(), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ALightWithACurveThatTheEngineCannotLoadGivesAProblemAndNoLight()
    {
        using var ws = new TestWorkspace();
        var items = Items(ws);
        var light = Parse<Universe.LightSource>("Light_curve", Universe.LightSource.TryParse);

        foreach (var (curve, text) in new[]
                 {
                     ("no_such_curve", "No graph has the name 'no_such_curve'"),
                     // Each key stroke of a text field gives a new name, for example the start of a real name.
                     ("curv", "No graph has the name 'curv'"),
                     ("twice", "No graph has the name 'twice', or more than one graph has it"),
                     ("empty", "The graph 'empty' has no points"),
                 })
        {
            light.AttenCurve = curve;
            Assert.Null(SceneBuild.Light(items, light, out var problem));
            Assert.StartsWith(text, problem);
        }

        light.AttenCurve = "curve";
        var built = SceneBuild.Light(items, light, out var none);
        Assert.Null(none);
        Assert.Equal(LightKind.PointAttenCurve, built!.Light.Kind);
        Assert.Equal(ApproximateCurve.GetQuadraticFunction([new(0, 1), new(100, 0.5f), new(200, 0)]), built.Light.Attenuation);
        Assert.Equal("curve", built.AttenuationCurveName);
        Assert.Equal(new Vector3(1, 2, 3), built.Light.Position);
        var color = light.Color!.Value;
        Assert.Equal(new Color3f(color.R, color.G, color.B), built.Light.Color);
        Assert.Equal(50000, built.Light.Range);
    }

    [Fact]
    public void ALightGetsTheDefaultsOfTheEngine()
    {
        using var ws = new TestWorkspace();
        var items = Items(ws);

        var plain = SceneBuild.Light(items, Parse<Universe.LightSource>("Light_defaults", Universe.LightSource.TryParse), out _)!;
        var sun = SceneBuild.Light(items, Parse<Universe.LightSource>("Light_sun", Universe.LightSource.TryParse), out _)!;

        Assert.Equal(new RenderLight
        {
            Kind = LightKind.Point, Color = Color3f.White, Range = 200000, Direction = Vector3.UnitZ, Attenuation = Vector3.UnitY,
        }, plain.Light);
        Assert.Equal(LightKind.Directional, sun.Light.Kind);
        Assert.Equal(-Vector3.UnitY, sun.Light.Direction);
        Assert.Equal(new Vector3(1, 0, 0.5f), sun.Light.Attenuation);
        Assert.True(SceneBuild.SameLight(plain, plain.Clone()));
        Assert.False(SceneBuild.SameLight(plain, sun));
    }

    /// <summary>
    /// A change of any one field of a light makes the compare false, except the flag that only an editor sets. A new
    /// upstream field fails this test until the compare covers it.
    /// </summary>
    [Fact]
    public void SameLightComparesEachFieldExceptTheEditorFlag()
    {
        using var ws = new TestWorkspace();
        var built = SceneBuild.Light(Items(ws), Parse<Universe.LightSource>("Light_curve", Universe.LightSource.TryParse), out _)!;

        foreach (var field in typeof(LightSource).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var changed = built.Clone();
            field.SetValue(changed, Other(field.FieldType, field.GetValue(changed)));
            Assert.True(SceneBuild.SameLight(built, changed) == (field.Name == nameof(LightSource.Disabled)), field.Name);
        }
    }

    [Fact]
    public void AnObjectWithAMissingArchetypeGivesAProblemAndNoObject()
    {
        using var ws = new TestWorkspace();
        var items = Items(ws);

        Assert.Null(SceneBuild.Object(items, "Li01", Parse<Universe.SystemObject>("Li01_lost", Universe.SystemObject.TryParse),
            out var missing));
        Assert.Null(SceneBuild.Object(items, "Li01",
            Parse<Universe.SystemObject>("Li01_without_archetype", Universe.SystemObject.TryParse), out var none));

        Assert.Equal("The game drops this object: the archetype 'no_such_archetype' does not exist.", missing);
        Assert.Equal("The game drops this object: it has no archetype.", none);
    }

    /// <summary>
    /// A new build makes a new dock and a new costume array, and a clone of the engine keeps its own copies. Both
    /// compare equal. A change of any one field of the dock compares unequal.
    /// </summary>
    [Fact]
    public void SameObjectComparesTheDockByItsFiveFields()
    {
        using var ws = new TestWorkspace();
        var items = Items(ws);
        foreach (var nickname in new[] { "Li01_depot", "Li01_to_Li05" })
        {
            var built = Build(items, nickname);
            var again = Build(items, nickname);
            Assert.NotSame(built.Dock, again.Dock);
            Assert.True(SceneBuild.SameObject(built, again));
            Assert.True(SceneBuild.SameObject(built, built.Clone()));

            foreach (var change in new Action<DockAction>[]
                     {
                         d => d.Kind = d.Kind == DockKinds.Base ? DockKinds.Jump : DockKinds.Base,
                         d => d.Target += "x",
                         d => d.TargetLeft += "x",
                         d => d.Exit += "x",
                         d => d.Tunnel += "x",
                     })
            {
                var changed = Build(items, nickname);
                change(changed.Dock!);
                Assert.False(SceneBuild.SameObject(built, changed));
            }
        }
        var depot = Build(items, "Li01_depot");
        Assert.Equal(["robot_body_a", "pi_pirate5_head"], depot.SpaceCostume!);
        Assert.NotSame(depot.SpaceCostume, Build(items, "Li01_depot").SpaceCostume);
    }

    /// <summary>
    /// A change of any one field of an object makes the compare false, except the lane names that the engine sets in a
    /// walk over the whole system. A new upstream field fails this test until the compare covers it.
    /// </summary>
    [Fact]
    public void SameObjectComparesEachFieldExceptTheLaneNames()
    {
        using var ws = new TestWorkspace();
        var built = Build(Items(ws), "Li01_depot");
        string[] laneNames = [nameof(SystemObject.IdsLeft), nameof(SystemObject.IdsRight)];

        foreach (var field in typeof(SystemObject).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var changed = built.Clone();
            field.SetValue(changed, Other(field.FieldType, field.GetValue(changed)));
            Assert.True(SceneBuild.SameObject(built, changed) == laneNames.Contains(field.Name), field.Name);
        }
    }

    /// <summary>
    /// The copy into a scene object takes each field that the compare reads, so the scene object then equals the built
    /// object. It keeps the lane names, which the engine sets in a walk over the whole system. A new upstream field fails
    /// this test until the copy covers it.
    /// </summary>
    [Fact]
    public void CopyObjectCopiesEachFieldExceptTheLaneNames()
    {
        using var ws = new TestWorkspace();
        var loaded = Build(Items(ws), "Li01_depot");
        string[] laneNames = [nameof(SystemObject.IdsLeft), nameof(SystemObject.IdsRight)];
        var fields = typeof(SystemObject).GetFields(BindingFlags.Public | BindingFlags.Instance);
        var built = loaded.Clone();
        foreach (var field in fields)
            field.SetValue(built, Other(field.FieldType, field.GetValue(built)));
        var scene = loaded.Clone();

        SceneBuild.CopyObject(built, scene);

        foreach (var field in fields)
        {
            var expected = laneNames.Contains(field.Name) ? field.GetValue(loaded) : field.GetValue(built);
            Assert.True(Equals(expected, field.GetValue(scene)), field.Name);
        }
        Assert.True(SceneBuild.SameObject(built, scene));
    }

    /// <summary>The copy into a scene system takes each field that the compare reads, and keeps the items and the CRC
    /// of the scene system.</summary>
    [Fact]
    public void CopySystemCopiesEachFieldThatTheCompareReads()
    {
        using var ws = new TestWorkspace();
        var loaded = SceneBuild.StarSystem(Items(ws),
            new Universe.StarSystem { Nickname = "Li01", File = @"systems\li01\li01.ini" });
        loaded.CRC = 7;
        var fields = typeof(StarSystem).GetFields(BindingFlags.Public | BindingFlags.Instance);
        var built = loaded.Clone();
        foreach (var field in fields)
            field.SetValue(built, Other(field.FieldType, field.GetValue(built)));
        var scene = loaded.Clone();
        scene.CRC = loaded.CRC;
        Assert.False(SceneBuild.SameSystem(built, scene));

        SceneBuild.CopySystem(built, scene);

        Assert.True(SceneBuild.SameSystem(built, scene));
        Assert.Equal(7u, scene.CRC);
        foreach (var field in fields.Where(f => f.Name is nameof(StarSystem.Objects) or nameof(StarSystem.Zones) or
                     nameof(StarSystem.LightSources) or nameof(StarSystem.ZoneDict)))
            Assert.False(ReferenceEquals(field.GetValue(built), field.GetValue(scene)), field.Name);
    }

    /// <summary>
    /// A clone of the engine has no preloads where the engine has an empty list, and it does not copy the CRC. Both
    /// compare equal. A change of any other field that the sections of the system set makes the compare false.
    /// </summary>
    [Fact]
    public void SameSystemComparesEachFieldThatTheSectionsOfTheSystemSet()
    {
        using var ws = new TestWorkspace();
        var ini = new Universe.StarSystem { Nickname = "Li01", File = @"systems\li01\li01.ini" };
        var built = SceneBuild.StarSystem(Items(ws), ini);
        string[] notCompared =
        [
            nameof(StarSystem.CRC), nameof(StarSystem.Objects), nameof(StarSystem.Zones), nameof(StarSystem.ZoneDict),
            nameof(StarSystem.LightSources), nameof(StarSystem.AsteroidFields), nameof(StarSystem.Nebulae),
            nameof(StarSystem.ResourceFiles), nameof(StarSystem.ShortestPathsLegal), nameof(StarSystem.ShortestPathsIllegal),
            nameof(StarSystem.ShortestPathsAny),
        ];
        Assert.Empty(built.Preloads!);
        var clone = built.Clone();
        Assert.Null(clone.Preloads);
        Assert.True(SceneBuild.SameSystem(built, clone));

        foreach (var field in typeof(StarSystem).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var changed = built.Clone();
            field.SetValue(changed, Other(field.FieldType, field.GetValue(changed)));
            Assert.True(SceneBuild.SameSystem(built, changed) == notCompared.Contains(field.Name), field.Name);
        }
    }

    /// <summary>A preload record compares its values by reference, so the compare reads each value. Equal values in
    /// new arrays compare equal. One other value or another type, with the same count, compares unequal.</summary>
    [Fact]
    public void SameSystemComparesThePreloadsByTheirValues()
    {
        using var ws = new TestWorkspace();
        var built = SceneBuild.StarSystem(Items(ws), new Universe.StarSystem { Nickname = "Li01", File = @"systems\li01\li01.ini" });
        built.Preloads = [new PreloadObject(PreloadType.Voice, "voice_a", "voice_b")];
        var clone = built.Clone();

        clone.Preloads = [new PreloadObject(PreloadType.Voice, "voice_a", "voice_b")];
        Assert.True(SceneBuild.SameSystem(built, clone));
        clone.Preloads = [new PreloadObject(PreloadType.Voice, "voice_a", "voice_c")];
        Assert.False(SceneBuild.SameSystem(built, clone));
        clone.Preloads = [new PreloadObject(PreloadType.Ship, "voice_a", "voice_b")];
        Assert.False(SceneBuild.SameSystem(built, clone));
    }

    /// <summary>
    /// As in the generated parser, the last section of a single type wins, a section that does not parse is left
    /// out, and each section of a list type adds an item.
    /// </summary>
    [Fact]
    public void ParseSystemKeepsTheLastSectionOfASingleType()
    {
        using var ws = SystemWorkspace.Create("""
            [SystemInfo]
            local_faction = first_grp

            [Archetype]
            ship = ship_a

            [SystemInfo]
            local_faction = last_grp

            [Archetype]
            ship = ship_b

            [SystemInfo]
            local_faction = broken_grp
            space_farclip = far
            """);
        var session = ws.Session("act");

        var system = SceneBuild.ParseSystem(session, "Li01")!;

        Assert.Equal("last_grp", system.Info!.LocalFaction);
        Assert.Equal(["ship_a", "ship_b"], system.Preloads.SelectMany(p => p.ArchetypeShip));
    }

    [Fact]
    public void AZoneGetsTheRingSwapAndTheFastValuesAreTheBuiltValues()
    {
        var ini = Parse<Universe.Zone>("Zone_ring", Universe.Zone.TryParse);

        var zone = SceneBuild.Zone(ini);

        // The line is (outer, inner, height). The engine stores (outer, height, inner).
        Assert.Equal(ShapeKind.Ring, zone.Shape);
        Assert.Equal(new Vector3(3000, 200, 700), zone.Size);
        var section = Parsed["Zone_ring"];
        Assert.Equal(zone.Shape, SceneBuild.ZoneShape(section["shape"]!.ToList()));
        Assert.Equal(zone.Size, SceneBuild.ZoneSize(section["size"]!.ToList(), zone.Shape));
        Assert.Equal(zone.RotationMatrix, SceneBuild.ZoneRotation(section["rotate"]!.ToList(), out var radians));
        Assert.Equal(zone.RotationAngles, radians);
        Assert.True(zone.ZonesEqual(SceneBuild.Zone(ini)));
    }

    /// <summary>The fast values read a line as the generated parser reads it from a binary file, and they never log,
    /// also for a text that is not a number.</summary>
    [Fact]
    public void TheFastValuesReadALineAsTheGeneratedParserReadsIt()
    {
        IReadOnlyList<string> log;
        using (var capture = EngineLogCapture.Start())
        {
            Assert.Equal(Vector3.Zero, SceneBuild.Position(null));
            Assert.Equal(Vector3.Zero, SceneBuild.Position(Values("0")));
            // The parser ignores a line with two values, so the engine uses its default.
            Assert.Equal(Vector3.Zero, SceneBuild.Position(Values("1, 2")));
            Assert.Equal(new Vector3(1, 2, 3), SceneBuild.Position(Values("1, 2, 3, 4")));
            Assert.Equal(new Vector3(0, 2.5f, 3), SceneBuild.Position(Values("not_a_number, 2.5, 3")));
            Assert.Equal(Quaternion.Identity, SceneBuild.ObjectRotation(null));
            Assert.Equal(MathHelper.QuatFromEulerDegrees(0, 105, 0), SceneBuild.ObjectRotation(Values("0, 105, 0")));
            Assert.Equal(Quaternion.Identity, SceneBuild.ObjectRotation(Values("x, y, z")));
            Assert.Equal(Matrix4x4.Identity, SceneBuild.ZoneRotation(Values("5"), out var radians));
            Assert.Equal(Vector3.Zero, radians);
            Assert.Equal(ShapeKind.Box, SceneBuild.ZoneShape(Values("box")));
            Assert.Equal(ShapeKind.Box, SceneBuild.ZoneShape(Values("2")));
            Assert.Equal(ShapeKind.Sphere, SceneBuild.ZoneShape(Values("cube")));
            Assert.Equal(ShapeKind.Sphere, SceneBuild.ZoneShape(null));
            Assert.Equal(Vector3.One, SceneBuild.ZoneSize(null, ShapeKind.Box));
            Assert.Equal(new Vector3(4100), SceneBuild.ZoneSize(Values("4100"), ShapeKind.Sphere));
            Assert.Equal(new Vector3(750, 13088, 0), SceneBuild.ZoneSize(Values("750, 13088"), ShapeKind.Cylinder));
            Assert.Equal(new Vector3(1, 0, 2), SceneBuild.ZoneSize(Values("1, 2"), ShapeKind.Ring));
            Assert.Equal(new Vector3(0, 7, 0), SceneBuild.ZoneSize(Values("abc, 7"), ShapeKind.Box));
            log = capture.Stop();
        }

        Assert.Empty(log);
    }

    /// <summary>
    /// The fast values read a number as a binary file stores it. A text file reads more forms, for example 100x as 100,
    /// so there the fast value and the engine value differ. The checks report each such value as an error, so a caller
    /// can find the lines whose fast values it must not use.
    /// </summary>
    [Fact]
    public void ANumberThatOnlyATextFileReadsIsACheckError()
    {
        using var ws = SystemWorkspace.Create("""
            [Object]
            nickname = Li01_depot
            pos = 100x, 0, -27705
            archetype = depot
            """);
        var session = ws.Session("act");
        var depot = SystemWorkspace.Li01Section("Object", "Li01_depot");
        var pos = session.Get(depot)!.Lines("pos").Single().Values;

        // The text parser of the engine reads 100x as 100.
        Assert.Equal(100, Assert.IsType<LancerStringValue>(pos[0]).ToSingle());
        Assert.Equal(new Vector3(0, 0, -27705), SceneBuild.Position(pos));
        Assert.Contains(session.Check(depot), d => d.Severity == DiagnosticSeverity.Error &&
                                                   d.Message.EndsWith("the engine cannot read '100x' in a 'pos' line as a number."));
    }

    /// <summary>A game data object with no data, a float graph for each curve case, and the archetype
    /// <c>depot</c>.</summary>
    private static GameItemDb Items(TestWorkspace ws)
    {
        var folder = Path.Combine(ws.Root, "game");
        Directory.CreateDirectory(Path.Combine(folder, "EXE"));
        File.WriteAllText(Path.Combine(folder, "EXE", "freelancer.ini"), "[Freelancer]\ndata path = ..\\data\n");
        var items = new GameItemDb(FileSystem.FromPath(folder));
        items.Ini.Graphs = new GraphIni
        {
            FloatGraphs =
            [
                new FloatGraph { Name = "curve", Points = [new(0, 1), new(100, 0.5f), new(200, 0)] },
                new FloatGraph { Name = "twice", Points = [new(0, 1)] },
                new FloatGraph { Name = "TWICE", Points = [new(0, 1)] },
                new FloatGraph { Name = "empty" },
            ],
        };
        items.Archetypes.Add(new LibreLancer.Data.GameData.Archetype
            { Nickname = "depot", CRC = FLHash.CreateID("depot"), Type = LibreLancer.Data.Schema.Solar.ArchetypeType.station });
        return items;
    }

    private static SystemObject Build(GameItemDb items, string nickname) =>
        SceneBuild.Object(items, "Li01", Parse<Universe.SystemObject>(nickname, Universe.SystemObject.TryParse), out _)!;

    private static T Parse<T>(string nickname, OpenLancer.Data.Editing.SectionParser<T> parser) where T : class =>
        parser(Parsed[nickname], out var value, null, IniParseProperties.Empty)
            ? value
            : throw new InvalidOperationException(nickname);

    private static List<ValueBase> Values(string text) => IniFile.ParseFile("x.ini",
        new MemoryStream(Encoding.ASCII.GetBytes($"[Zone]\nv = {text}\n"))).Single()["v"]!.ToList();

    /// <summary>
    /// A value of a field type that differs from the given value. A new field type throws, so the test names the field
    /// that needs a rule.
    /// </summary>
    private static object? Other(Type type, object? value) => value switch
    {
        bool b => !b,
        int i => i + 1,
        uint u => u + 1,
        float f => f + 1,
        Vector2 v => v + Vector2.One,
        Vector3 v => v + Vector3.One,
        Quaternion q => Quaternion.Normalize(q + new Quaternion(0.5f, 0, 0, 0)),
        Color3f c => new Color3f(c.R + 0.5f, c.G, c.B),
        Color4 c => new Color4(c.R + 0.5f, c.G, c.B, c.A),
        RenderLight light => light with { Range = light.Range + 1 },
        Enum e => Enum.ToObject(type, Convert.ToInt64(e) + 1),
        string s => s + "x",
        string[] a => a.Append("x").ToArray(),
        List<string> list => list.Append("x").ToList(),
        List<EncounterParameters> list => list.Append(new EncounterParameters("x", "x")).ToList(),
        PreloadObject[] preloads => preloads.Append(new PreloadObject(PreloadType.Ship, "x")).ToArray(),
        null when type == typeof(string) => "x",
        null when type == typeof(PreloadObject[]) => new[] { new PreloadObject(PreloadType.Ship, "x") },
        null when Nullable.GetUnderlyingType(type) is { } inner => Activator.CreateInstance(inner),
        null => RuntimeHelpers.GetUninitializedObject(type),
        _ when !type.IsValueType => null,
        _ => throw new NotSupportedException($"No other value for {type.Name}."),
    };
}

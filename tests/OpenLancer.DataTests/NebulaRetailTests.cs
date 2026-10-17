using System;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Render;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the nebula size fix (fork commit OL-0028) and the measurements of the Buffalo Base report (GA1 rows
/// NB-01 to NB-04, run U30). The tests need no GPU: they use the zone rules of the engine, as the renderers do.
/// </summary>
public sealed class NebulaRetailTests(ITestOutputHelper output)
{
    private const string Buffalo = "Li01_12";
    private const string BadlandsNebula = "Zone_Li01_Badlands_Nebula";
    private const string BadlandsField = "Zone_Li01_Badlands_Asteroids";

    static NebulaRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// Buffalo Base is inside the zone of the Badlands nebula, and now also inside the volume that the nebula draws. The
    /// field around it has no band and no billboards, so only the asteroid cubes near the camera show it.
    /// </summary>
    [Fact]
    public void TheBadlandsCloudHoldsBuffaloBase()
    {
        var system = Li01();
        var buffalo = system.Objects.Single(o => o.Nickname == Buffalo).Position;
        var nebula = system.Nebulae.Single(n => n.Zone!.Nickname == BadlandsNebula);
        var zone = nebula.Zone!;
        Assert.Equal(ShapeKind.Ellipsoid, zone.Shape);
        Assert.Equal(new Vector3(55000, 20000, 35000), zone.Size);

        var radii = NebulaRenderer.ZoneRadii(zone)!.Value;
        Assert.Equal(zone.Size, radii);
        Assert.True(zone.ContainsPoint(buffalo));
        Assert.Equal(0.458f, DrawnScaledDistance(zone, radii, buffalo), 3);
        output.WriteLine($"Buffalo: {DrawnScaledDistance(zone, radii, buffalo):F3} of the drawn volume, " +
                         $"{DrawnScaledDistance(zone, radii / 2, buffalo):F3} of the half-size volume before OL-0028.");

        var field = system.AsteroidFields.Single(f => f.Zone!.Nickname == BadlandsField);
        Assert.True(field.Zone!.ContainsPoint(buffalo));
        Assert.Null(field.Band);
        Assert.Equal(-1, field.BillboardCount);
        Assert.Contains(field.ExclusionZones, e => e.Zone!.Nickname == "Zone_Li01_12_exclusion" && e.Zone.ContainsPoint(buffalo));
    }

    /// <summary>
    /// Run U30 without a GPU: a camera at 1 km, 10 km and 100 km from Buffalo Base in eight directions. For each point,
    /// the nebula that holds the camera (the rule of <see cref="SystemRenderer.ObjectInNebula"/>), the fog opacity, the
    /// fog far distance, and an estimate of the asteroid cubes of the Badlands field with and without the fog.
    /// </summary>
    [Fact]
    public void U30CameraNearBuffaloBase()
    {
        var system = Li01();
        var buffalo = system.Objects.Single(o => o.Nickname == Buffalo).Position;
        var badlands = system.Nebulae.Single(n => n.Zone!.Nickname == BadlandsNebula);
        var field = system.AsteroidFields.Single(f => f.Zone!.Nickname == BadlandsField);
        output.WriteLine($"Badlands fog: color {badlands.FogColor}, range {badlands.FogRange}, edge_fraction " +
                         $"{badlands.Zone!.EdgeFraction}. Field: fill_dist {field.FillDist}, cube_size {field.CubeSize}.");
        var outward = Vector3.Normalize(buffalo - badlands.Zone.Position);
        (string Name, Vector3 Direction)[] directions =
        [
            ("outward", outward), ("inward", -outward), ("+X", Vector3.UnitX), ("-X", -Vector3.UnitX),
            ("+Y", Vector3.UnitY), ("-Y", -Vector3.UnitY), ("+Z", Vector3.UnitZ), ("-Z", -Vector3.UnitZ),
        ];
        output.WriteLine("distance direction | nebula | fog opacity | fog far | cubes with fog | cubes without fog");
        foreach (var distance in new[] { 1000f, 10_000f, 100_000f })
        {
            foreach (var (name, direction) in directions)
            {
                var camera = buffalo + direction * distance;
                var nebula = system.Nebulae.FirstOrDefault(n => n.Zone!.ContainsPoint(camera));
                var opacity = nebula == null ? 0 : NebulaRenderer.FogTransition(nebula.Zone!, camera);
                var fogFar = nebula == null ? float.MaxValue : FogFar(nebula, camera);
                var withFog = Cubes(field, camera, fogFar);
                var withoutFog = Cubes(field, camera, float.MaxValue);
                output.WriteLine($"{distance,7} {name,-8} | {nebula?.Zone!.Nickname ?? "none"} | {opacity:F2} | " +
                                 $"{(nebula == null ? "none" : fogFar.ToString("F0"))} | {withFog} | {withoutFog}");

                if (distance == 1000)
                {
                    // Full fog with a far distance of 1 km: the view shows only the asteroids within 1 km.
                    Assert.Same(badlands, nebula);
                    Assert.Equal(1f, opacity);
                    Assert.Equal(1000f, fogFar);
                    Assert.InRange(withFog, 1, withoutFog);
                }
                else if (distance == 100_000)
                {
                    // Outside every nebula: the exterior draws, and no cube of the field draws.
                    Assert.Null(nebula);
                    Assert.Equal(0, withoutFog);
                }
            }
        }
    }

    /// <summary>
    /// For each retail nebula: its zone is a sphere or an ellipsoid, so it draws an exterior. Each base in a nebula zone
    /// is in the drawn volume. Each exclusion shell takes the size of its hole times <c>shell_scalar</c>.
    /// </summary>
    [Fact]
    public void EachRetailNebulaDrawsTheVolumeOfItsZone()
    {
        GameDataPaths.RequireBase();
        var systems = RetailGame.Instance.Data.Items.Systems;
        var nebulae = systems.SelectMany(s => s.Nebulae.Select(n => (System: s, Nebula: n))).ToList();
        Assert.Equal(58, nebulae.Count);
        Assert.All(nebulae, x => Assert.NotNull(NebulaRenderer.ZoneRadii(x.Nebula.Zone!)));
        output.WriteLine($"Nebula zones: {nebulae.Count(x => x.Nebula.Zone!.Shape == ShapeKind.Ellipsoid)} ellipsoids, " +
                         $"{nebulae.Count(x => x.Nebula.Zone!.Shape == ShapeKind.Sphere)} spheres.");

        var bases = nebulae.SelectMany(x => x.System.Objects
                .Where(o => o.Base != null && x.Nebula.Zone!.ContainsPoint(o.Position))
                .Select(o => (Object: o, Zone: x.Nebula.Zone!)))
            .ToList();
        Assert.All(bases, b => Assert.InRange(DrawnScaledDistance(b.Zone, NebulaRenderer.ZoneRadii(b.Zone)!.Value,
            b.Object.Position), 0, 1));
        var outsideHalf = bases.Count(b => b.Zone.Shape == ShapeKind.Ellipsoid &&
                                           DrawnScaledDistance(b.Zone, b.Zone.Size / 2, b.Object.Position) > 1);
        output.WriteLine($"Bases in a nebula zone: {bases.Count}. Bases outside the half-size volume of an ellipsoid " +
                         $"before OL-0028: {outsideHalf}.");

        var shells = nebulae.SelectMany(x => x.Nebula.ExclusionZones ?? []).Where(e => e.ShellPath != null).ToList();
        Assert.Equal(86, shells.Count);
        Assert.All(shells, e =>
        {
            var radii = NebulaRenderer.ShellRadii(e)!.Value;
            Assert.Equal(ShapeKind.Sphere, e.Zone!.Shape);
            Assert.Equal(e.ShellScalar * e.Zone.Size.X, radii.X, 1);
            Assert.InRange(radii.X / e.Zone.Size.X, 1f, 1.2001f);
        });
    }

    private static StarSystem Li01()
    {
        GameDataPaths.RequireBase();
        return RetailGame.Instance.Data.Items.Systems.Get("li01")!;
    }

    /// <summary>The scaled distance of a point in the volume that a nebula draws with the given radii: the fill quads
    /// and the puff rings use the zone rotation and position (NebulaRenderer.RenderFill). 1 is the surface.</summary>
    private static float DrawnScaledDistance(Zone zone, Vector3 radii, Vector3 point)
    {
        var local = Vector3.Transform(point - zone.Position, Matrix4x4.Transpose(zone.RotationMatrix));
        return (local / radii).LengthSquared();
    }

    /// <summary>The fog far distance of the nebula that holds the camera: an exclusion zone moves it to its own
    /// fog_far (NebulaRenderer.GetLighting).</summary>
    private static float FogFar(Nebula nebula, Vector3 camera)
    {
        var far = nebula.FogRange.Y;
        var exclusion = nebula.ExclusionZones?.FirstOrDefault(e => e.Zone!.ContainsPoint(camera));
        return exclusion == null
            ? far
            : float.Lerp(far, exclusion.FogFar, NebulaRenderer.FogTransition(exclusion.Zone!, camera));
    }

    /// <summary>
    /// An estimate of the cubes that <c>AsteroidFieldRenderer</c> draws for a camera, with its rules: the camera within
    /// the largest size plus fill_dist of the field center, each cube within fill_dist and the fog far distance, in the
    /// zone, not empty and in no exclusion. It has no frustum test, and the cube radius is the half diagonal of a cube
    /// (the renderer reads the radius from the asteroid meshes).
    /// </summary>
    private static int Cubes(AsteroidField field, Vector3 camera, float fogFar)
    {
        var zone = field.Zone!;
        var reach = MathF.Max(MathF.Max(zone.Size.X, zone.Size.Y), zone.Size.Z) + field.FillDist;
        if (Vector3.Distance(camera, zone.Position) > reach)
        {
            return 0;
        }

        var radius = field.CubeSize * MathF.Sqrt(3) / 2;
        var close = AsteroidFieldShared.GetCloseCube(camera, field.CubeSize);
        var amount = (int)MathF.Floor(field.FillDist / field.CubeSize) + 1;
        var count = 0;
        for (var x = -amount; x <= amount; x++)
        {
            for (var y = -amount; y <= amount; y++)
            {
                for (var z = -amount; z <= amount; z++)
                {
                    var center = close + new Vector3(x, y, z) * field.CubeSize;
                    var closest = Vector3.Distance(center, camera) - radius;
                    if (closest < field.FillDist && closest < fogFar && zone.ContainsPoint(center) &&
                        AsteroidFieldShared.CubeExists(center, field.EmptyCubeFrequency, out _) &&
                        !field.ExclusionZones.Any(e => e.Zone!.ContainsPoint(center)))
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }
}

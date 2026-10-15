using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Scene;
using Xunit;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the engine views of the scene: each item that the builders make from W equals the item of a real
/// engine load, for every retail system. The fast values of the transform keys equal the built values.
/// </summary>
public sealed class SceneBuildRetailTests(ITestOutputHelper output)
{
    static SceneBuildRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EachRetailItemBuiltFromWEqualsTheItemOfTheEngineLoad()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var items = EngineDataLoader.Load(session.EditView.CreateFileSystem()).RequireComplete();

        var differences = new List<string>();
        int systems = 0, objects = 0, zones = 0, rings = 0, lights = 0, curves = 0;
        IReadOnlyList<string> lines;
        using (var capture = EngineLogCapture.Start())
        {
            foreach (var system in items.Systems)
            {
                var sections = session.Sections(session.SystemPath(system.Nickname)!);
                var report = (string text) => differences.Add($"{system.Nickname}: {text}");

                var builtSystem = SceneBuild.StarSystem(items, SceneBuild.ParseSystem(session, system.Nickname)!);
                if (!SceneBuild.SameSystem(builtSystem, system))
                    report("the system fields differ.");
                systems++;

                var objectSections = OfType(sections, "Object");
                Assert.Equal(system.Objects.Count, objectSections.Count);
                foreach (var (section, loaded) in objectSections.Zip(system.Objects))
                {
                    var ini = session.Parse<Universe.SystemObject>(section, Universe.SystemObject.TryParse)!;
                    var built = SceneBuild.Object(items, system.Nickname, ini, out var problem);
                    if (built == null || !SceneBuild.SameObject(built, loaded))
                        report($"{section.Id} differs. {problem}");
                    else if (!SceneBuild.Position(Last(session, section, "pos")).Equals(built.Position) ||
                             !SceneBuild.ObjectRotation(Last(session, section, "rotate")).Equals(built.Rotation))
                        report($"{section.Id}: the fast transform values differ.");
                    objects++;
                }

                var zoneSections = OfType(sections, "Zone");
                Assert.Equal(system.Zones.Count, zoneSections.Count);
                foreach (var (section, loaded) in zoneSections.Zip(system.Zones))
                {
                    var built = SceneBuild.Zone(session.Parse<Universe.Zone>(section, Universe.Zone.TryParse)!);
                    var shape = SceneBuild.ZoneShape(Last(session, section, "shape"));
                    var rotation = SceneBuild.ZoneRotation(Last(session, section, "rotate"), out var radians);
                    var size = SceneBuild.ZoneSize(Last(session, section, "size"), shape);
                    if (!built.ZonesEqual(loaded))
                        report($"{section.Id} differs.");
                    else if (!SceneBuild.Position(Last(session, section, "pos")).Equals(built.Position) ||
                             !rotation.Equals(built.RotationMatrix) || !radians.Equals(built.RotationAngles) ||
                             shape != built.Shape || !size.Equals(built.Size))
                        report($"{section.Id}: the fast transform values differ.");
                    zones++;
                    rings += built.Shape == ShapeKind.Ring ? 1 : 0;
                }

                var lightSections = OfType(sections, "LightSource");
                Assert.Equal(system.LightSources.Count, lightSections.Count);
                foreach (var (section, loaded) in lightSections.Zip(system.LightSources))
                {
                    var ini = session.Parse<Universe.LightSource>(section, Universe.LightSource.TryParse)!;
                    var built = SceneBuild.Light(items, ini, out var problem);
                    if (built == null || !SceneBuild.SameLight(built, loaded))
                        report($"{section.Id} differs. {problem}");
                    else if (!SceneBuild.Position(Last(session, section, "pos")).Equals(built.Light.Position))
                        report($"{section.Id}: the fast position differs.");
                    lights++;
                    curves += ini.AttenCurve != null ? 1 : 0;
                }
            }
            lines = capture.Stop();
        }

        output.WriteLine($"systems {systems}, objects {objects}, zones {zones} ({rings} rings), lights {lights} " +
                         $"({curves} with atten_curve), engine log lines {lines.Count}, differences {differences.Count}");
        foreach (var difference in differences.Take(20))
            output.WriteLine(difference);
        Assert.Empty(differences);
        Assert.Empty(lines);
        Assert.Equal(53, systems);
        Assert.Equal(3_430, objects);
        Assert.Equal(5_428, zones);
        Assert.Equal(8, rings);
        Assert.Equal(136, lights);
        Assert.Equal(88, curves);
    }

    private static List<SectionRef> OfType(IReadOnlyList<SectionRef> sections, string type) =>
        sections.Where(s => s.Id.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The values of the last line of a key in W, or null. The engine reads only the last line.</summary>
    private static IReadOnlyList<ValueBase>? Last(EditSession session, SectionRef section, string name) =>
        session.Get(section)!.Lines(name).LastOrDefault()?.Values;
}

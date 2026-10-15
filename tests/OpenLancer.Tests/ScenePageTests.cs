using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using Xunit;
using static OpenLancer.Tests.FieldBindingTests;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.Tests;

/// <summary>
/// The rules of the Inspector pages of the scene sections without the GPU: which rows a bound page shows, which keys
/// "More keys" lists, which name a row of a value with two names edits, and what the flags of a key show.
/// </summary>
/// <remarks>One test parses a section with the generated parser, so the class runs in the collection of the tests that
/// capture the global engine log.</remarks>
[Collection(nameof(EngineLogCollection))]
public sealed class ScenePageTests
{
    private const string Li01 = """
        [Dust]
        spacedust = Dust

        [Object]
        nickname = Li01_depot
        pos = -38675, 0, -27705
        archetype = depot

        [Object]
        nickname = Li01_planet
        pos = 0, 0, 0
        archetype = planet
        ambient = 40, 50, 60

        [Zone]
        nickname = Zone_pop
        pos = 0, 0, 0
        shape = SPHERE
        size = 4100

        [LightSource]
        nickname = Light_point
        pos = 1, 2, 3
        """;

    private static readonly SectionRef Depot = SystemWorkspace.Li01Section("Object", "Li01_depot");
    private static readonly SectionRef Planet = SystemWorkspace.Li01Section("Object", "Li01_planet");
    private static readonly SectionRef Point = SystemWorkspace.Li01Section("LightSource", "Light_point");

    /// <summary>The keys that bound rows showed in a frame without a row of the section.</summary>
    private static readonly IReadOnlySet<string> NoRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The depot has no dock, so its page has no docking row, and "More keys" lists dock_with and goto. An edit in that
    /// field gives the scene object a dock at once (the Data effect). The docking rows still do not come while the
    /// field is open, so the key stays in "More keys" and its field stays active. After the edit, the row of the key
    /// shows.
    /// </summary>
    [Theory]
    [InlineData("dock_with", "Li01_01_Base", DockKinds.Base)]
    [InlineData("goto", "Li02, Li02_to_Li01, gate_tunnel_bretonia", DockKinds.Jump)]
    public void ADockingRowComesFromTheLinesOfTheSectionAndNotFromTheSceneObject(string key, string text, DockKinds kind)
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");
        var start = session.Get(Depot)!;
        Assert.Equal((false, false, false), SystemObjectPage.DockingRows(start, null));
        Assert.Contains(key, SectionExtras.MoreKeys(session.Schema(Depot), NoRows, start));

        // The scene object after the Data effect of the edit.
        var dock = new DockAction { Kind = kind, Target = text.Split(',')[0] };
        using (var gesture = new LineGesture(session, Bind(session, Depot, key)))
        {
            gesture.Change(text);
            Assert.Single(session.Get(Depot)!.Lines(key));
            var open = PropertyGrid.LayoutView(session, Depot, gesture)!;
            Assert.Equal((false, false, false), SystemObjectPage.DockingRows(open, dock));
            Assert.Contains(key, SectionExtras.MoreKeys(session.Schema(Depot), NoRows, open));
            gesture.End(keep: true, interactive: true);
        }

        var shown = (kind == DockKinds.Base, kind == DockKinds.Jump, false);
        Assert.Equal(shown, SystemObjectPage.DockingRows(PropertyGrid.LayoutView(session, Depot, null), dock));
        // Without a bound section, the dock of the game data decides.
        Assert.Equal(shown, SystemObjectPage.DockingRows(null, dock));
    }

    /// <summary>
    /// The light has no curve, so its page has no curve row. A curve name that a graph has gives the scene light the
    /// curve at once (the Light effect). The curve row still does not come while the field in "More keys" is open. The
    /// attenuation and the direction also follow the lines and not the kind of the light.
    /// </summary>
    [Fact]
    public void ALightRowComesFromTheLinesOfTheSectionAndNotFromTheSceneLight()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");
        var point = Light(LightKind.Point, null);
        Assert.Equal((false, false, false), LightPage.Rows(session.Get(Point), point));
        Assert.Equal((true, false, false), LightPage.Rows(null, point));
        Assert.Equal((false, true, true), LightPage.Rows(null, Light(LightKind.Directional, "known_curve")));

        // The scene light after the Light effect of the edit.
        var curve = Light(LightKind.Point, "known_curve");
        using (var gesture = new LineGesture(session, Bind(session, Point, "atten_curve")))
        {
            gesture.Change("known_curve");
            var open = PropertyGrid.LayoutView(session, Point, gesture)!;
            Assert.False(LightPage.Rows(open, curve).Curve);
            Assert.Contains("atten_curve", SectionExtras.MoreKeys(session.Schema(Point), NoRows, open));
            gesture.End(keep: true, interactive: true);
        }

        Assert.Equal((false, false, true), LightPage.Rows(PropertyGrid.LayoutView(session, Point, null), curve));
    }

    /// <summary>
    /// "More keys" lists one name for each value. Two names of one field set one value, so a value shows its names that
    /// have lines, or else its first name. A value that a row shows is not in the list.
    /// </summary>
    [Fact]
    public void MoreKeysListsOneNameForEachValue()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");
        IReadOnlyList<string> More(SectionRef section, params string[] rows) =>
            SectionExtras.MoreKeys(session.Schema(section), new HashSet<string>(rows, StringComparer.OrdinalIgnoreCase),
                session.Get(section)!);

        var depot = More(Depot);
        Assert.Contains("ambient_color", depot);
        Assert.DoesNotContain("ambient", depot);
        Assert.DoesNotContain("nickname", depot);
        Assert.Contains("ambient", More(Planet));
        Assert.DoesNotContain("ambient_color", More(Planet));
        Assert.DoesNotContain("ambient", More(Planet, "ambient"));

        // A row of the value with one name hides the other name too.
        var dust = SystemWorkspace.Li01Section("Dust", null);
        Assert.Equal(["spacedust_maxparticles"], More(dust, "spacedust"));

        // With lines of both names, both lines show, so the user can see the line that the engine ignores.
        session.Execute(EditCommand.SetLinesFromText(Planet, "ambient_color", ["10, 20, 30"]));
        Assert.Contains("ambient", More(Planet));
        Assert.Contains("ambient_color", More(Planet));

        // Names of one entry handler are two values: each faction line and each faction_weight line adds a spawn.
        var zone = More(SystemWorkspace.Li01Section("Zone", "Zone_pop"));
        Assert.Contains("faction", zone);
        Assert.Contains("faction_weight", zone);
    }

    /// <summary>
    /// A row of a value with two names edits the name of the last line, because the generated parser sets the value
    /// from each line of either name in file order.
    /// </summary>
    [Theory]
    [InlineData("", "ambient_color", null)]
    [InlineData("ambient = 40, 50, 60", "ambient", 40)]
    [InlineData("ambient_color = 10, 20, 30", "ambient_color", 10)]
    [InlineData("ambient_color = 10, 20, 30\nambient = 40, 50, 60", "ambient", 40)]
    [InlineData("ambient = 40, 50, 60\nambient_color = 10, 20, 30", "ambient_color", 10)]
    public void TheRowOfAValueWithTwoNamesEditsTheLastLine(string lines, string name, int? red)
    {
        using var ws = SystemWorkspace.Create($"""
            [Object]
            nickname = Li01_depot
            pos = 0, 0, 0
            archetype = depot
            {lines}
            """);
        var session = ws.Session("act");

        Assert.Equal(name, PropertyGrid.LastName(session.Get(Depot)!, "ambient_color", "ambient"));
        var color = session.Parse<Universe.SystemObject>(Depot, Universe.SystemObject.TryParse)!.AmbientColor;
        Assert.Equal(red, color is { } c ? (int)MathF.Round(c.R * 255) : null);
    }

    /// <summary>The chips and the checkboxes of a flags key show the lines of the key: the first value of the last line,
    /// as the engine reads it, or 0 without a line.</summary>
    [Fact]
    public void TheFlagsOfABoundKeyComeFromItsLastLine()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");
        var system = SystemWorkspace.System("Li01");
        ulong? Bits() => PropertyGrid.LineBits(Bind(session, system, "visit").Lines);

        Assert.Equal(0UL, Bits());
        session.Execute(EditCommand.SetLinesFromText(system, "visit", ["128"]));
        Assert.Equal(128UL, Bits());
        session.Execute(EditCommand.SetLinesFromText(system, "visit", ["1", "36"]));
        Assert.Equal(36UL, Bits());
        session.Execute(EditCommand.SetLinesFromText(system, "visit", ["128", "flag"]));
        Assert.Null(Bits());
    }

    private static LightSource Light(LightKind kind, string? curve) =>
        new() { Nickname = "Light_point", AttenuationCurveName = curve, Light = new RenderLight { Kind = kind } };
}

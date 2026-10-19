using System.Collections.Generic;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Solar;
using OpenLancer.Data.Editing;
using OpenLancer.Editor;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Scene;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The names of the scene items and the systems come from W (section 14 of the design), so a rename shows before the
/// save, also when the scene item keeps the text id that it loaded with.
/// </summary>
public sealed class ItemTextsTests
{
    /// <summary>The strings of the game data.</summary>
    private static readonly Dictionary<int, string> GameNames = new()
    {
        [196609] = "New York",
        [261164] = "Depot",
    };

    /// <summary>The infocards of the game data. An id can name a string and an infocard with other texts.</summary>
    private static readonly Dictionary<int, string> GameInfocards = new()
    {
        [261164] = "A depot that holds water.",
    };

    private static readonly SectionRef Depot = SystemWorkspace.Li01Section("Object", "Li01_depot");

    /// <summary>The scene item of the depot, with the text id that the system loaded with.</summary>
    private static readonly SystemObject DepotItem = new()
    {
        Nickname = "Li01_depot", IdsName = 261164, Archetype = new Archetype { Nickname = "depot", Type = ArchetypeType.station },
    };

    private static readonly SectionRef Light = SystemWorkspace.Li01Section("LightSource", "Light_main");

    /// <summary>The scene item of the light.</summary>
    private static readonly LightSource LightItem = new() { Nickname = "Light_main", AttenuationCurveName = null };

    private static ItemTexts Texts(EditSession session) =>
        new(session, id => GameNames.GetValueOrDefault(id, ""), id => GameInfocards.GetValueOrDefault(id, ""))
        {
            SceneSections = item =>
                ReferenceEquals(item, DepotItem) ? Depot : ReferenceEquals(item, LightItem) ? Light : null,
        };

    private static StarSystem Li01() => new() { Nickname = "Li01", SourceFile = "", Objects = [DepotItem] };

    [Fact]
    public void ANameComesFromTheSectionInW()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var texts = Texts(session);

        // The game data has no id for the system, and W of universe.ini gives strid_name = 196609.
        Assert.Equal("New York", texts.NameOf(Li01()));
        Assert.Equal("Depot", texts.NameOf(DepotItem));

        session.Execute(ItemEdits.SetText(session, Depot, "ids_name", "Water depot"));
        session.Execute(ItemEdits.SetText(session, session.SystemSection("Li01")!, "strid_name", "New New York"));
        Assert.Equal("Water depot", texts.NameOf(DepotItem));
        Assert.Equal("New New York", texts.NameOf(Li01()));
        // The scene item keeps its loaded text id, and the name still shows the placeholder text.
        Assert.Equal(261164, DepotItem.IdsName);

        session.Undo();
        Assert.Equal("New York", texts.NameOf(Li01()));
        Assert.Equal("Water depot", texts.NameOf(DepotItem));
    }

    /// <summary>The Hierarchy reads its names through the texts: the system row shows the strid_name of W, and the
    /// depot row shows its placeholder name.</summary>
    [Fact]
    public void TheHierarchyShowsTheNamesOfW()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var texts = Texts(session);
        session.Execute(ItemEdits.SetText(session, Depot, "ids_name", "Water depot"));
        var system = Li01();

        var model = new HierarchyModel(new SystemIndex(system, system), texts.TextOf);

        Assert.Equal("New York", model.Root.Name);
        Assert.Equal("Water depot", model.NodeOf(DepotItem)!.Name);
    }

    [Fact]
    public void TheLastLineGivesTheTextAndAnInfocardShowsAsPlainText()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var texts = Texts(session);

        session.Execute(EditCommand.SetLines(Depot, "ids_name", [[new Int32Value(196609)], [new Int32Value(261164)]]));
        Assert.Equal("Depot", texts.NameOf(DepotItem));
        // A number of ids_info names an infocard, not a string.
        session.Execute(EditCommand.SetLines(Depot, "ids_info", [[new Int32Value(261164)]]));
        Assert.Equal("A depot that holds water.", texts.TextOf(DepotItem, "ids_info"));
        session.Execute(ItemEdits.SetText(session, Depot, "ids_info", ItemEdits.ToInfocard("Holds water.")));
        Assert.Equal("Holds water.", texts.TextOf(DepotItem, "ids_info"));
    }

    /// <summary>The engine reads no text of a light, so a light has no name, also when its section in W has an ids_name
    /// line.</summary>
    [Fact]
    public void ALightHasNoName()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var texts = Texts(session);

        session.Execute(EditCommand.SetLines(Light, "ids_name", [[new Int32Value(196609)]]));

        Assert.Equal("", texts.NameOf(LightItem));
        Assert.Equal("", texts.TextOf(LightItem, "ids_name"));
    }

    /// <summary>
    /// A nebula and a field have no text key in their sections. So they take the name of their zone from W (section 5
    /// of the M22 design). A rename of the zone shows on the nebula before the save.
    /// </summary>
    [Fact]
    public void ANebulaTakesTheNameOfItsZone()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var zone = new Zone { Nickname = "Zone_death", IdsName = 261164 };
        var zoneSection = SystemWorkspace.Li01Section("Zone", "Zone_death");
        var nebula = new Nebula { SourceFile = @"solar\nebula\death_cloud.ini", Zone = zone };
        var texts = new ItemTexts(session, id => GameNames.GetValueOrDefault(id, ""), _ => "")
        {
            SceneSections = item => ReferenceEquals(item, zone) ? zoneSection : null,
        };

        // W has no name line for the zone yet, so the zone has no name, and the nebula neither.
        Assert.Equal("", texts.NameOf(nebula));

        session.Execute(EditCommand.SetLines(zoneSection, "ids_name", [[new Int32Value(196609)]]));

        Assert.Equal("New York", texts.NameOf(zone));
        Assert.Equal("New York", texts.NameOf(nebula));
        Assert.Equal("Zone_death", SceneItems.Nickname(nebula));
    }

    [Fact]
    public void AFieldWithoutAZoneNameHasNoName()
    {
        using var ws = SystemWorkspace.Create();
        var texts = Texts(ws.Session("act"));
        var field = new AsteroidField
        {
            SourceFile = @"solar\asteroids\Li01_Badlands_Asteroids.ini", Zone = new Zone { Nickname = "Zone_badlands" },
        };

        Assert.Equal("", texts.NameOf(field));
        // The Hierarchy and the labels then show the file name without its extension.
        Assert.Equal("Li01_Badlands_Asteroids", SceneItems.FallbackName(field));
        Assert.Equal("Zone_badlands", SceneItems.FallbackName(field.Zone));
        // A field without a zone has no nickname, and its texts are empty.
        var lost = new AsteroidField { SourceFile = "" };
        Assert.Null(SceneItems.Nickname(lost));
        Assert.Null(SceneItems.FallbackName(lost));
        Assert.Equal("", texts.NameOf(lost));
    }

    /// <summary>An item without a section in W shows the text of its id: an object of another system, or a ring with
    /// its lane name.</summary>
    [Fact]
    public void AnItemWithoutASectionShowsTheTextOfItsId()
    {
        using var ws = SystemWorkspace.Create();
        var texts = Texts(ws.Session("act"));
        var other = new SystemObject { Nickname = "Br01_depot", IdsName = 261164, TradelaneSpaceName = 196609 };

        Assert.Equal("Depot", texts.NameOf(other));
        Assert.Equal("New York", texts.TextOf(other, "tradelane_space_name"));
        Assert.Equal("", texts.TextOf(other, "ids_info"));
        Assert.Equal("", texts.NameOf((object?)null));
    }
}

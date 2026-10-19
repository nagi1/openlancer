using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The tests that capture the lines of the global engine log. The collection runs alone, so the lines of another test
/// cannot get into a capture.
/// </summary>
[CollectionDefinition(nameof(EngineLogCollection), DisableParallelization = true)]
public sealed class EngineLogCollection;

/// <summary>
/// <c>EditSession.Parse</c> reads a section of W with the generated parser of the engine. The parser gets a copy of
/// the section with which it logs nothing, and each case checks that the engine log gets no line.
/// </summary>
[Collection(nameof(EngineLogCollection))]
public sealed class SectionParseTests
{
    private const string Li01 = """
        [Object]
        nickname = plain
        pos = 1, 2, 3
        archetype = depot

        [Object]
        nickname = unknown_key
        colour = red
        archetype = depot

        [Object]
        nickname = repeated
        archetype = first
        archetype = second

        [Object]
        nickname = too_many
        pos = 1, 2, 3, 4
        loadout = a, b

        [LightSource]
        nickname = bad_type
        type = BOAT

        [Zone]
        nickname = short_pos
        pos = 1, 2

        [Zone]
        nickname = encounters
        encounter = area_scout, 5, 0.4
        faction = li_n_grp, 1
        encounter = area_patrol, 7, 0.2
        faction = li_p_grp, 0.5
        faction = li_lsf_grp, 0.5
        path_label = lane_a, lane_b

        [Object]
        nickname = cases
        Pos = 1, 2, 3
        pos = 4, 5, 6
        """;

    /// <summary>A zone with a string in its integer key <c>toughness</c>.</summary>
    private const string StringZone = """
        [Zone]
        nickname = lane
        toughness = br01_2
        """;

    /// <summary>A zone with a string in <c>lane_id</c> in two lines. The retail game does not read the key, and each
    /// retail trade lane zone has a string in it.</summary>
    private const string LaneZone = """
        [Zone]
        nickname = lane
        lane_id = br01_2
        lane_id = br01_3
        """;

    private static readonly SectionRef Encounters = SystemWorkspace.Li01Section("Zone", "encounters");

    private static SectionRef Obj(string nickname) => SystemWorkspace.Li01Section("Object", nickname);

    /// <summary>Parses a section and checks that the engine log gets no line.</summary>
    private static T? Parse<T>(EditSession session, SectionRef target, SectionParser<T> parser) where T : class
    {
        T? value;
        IReadOnlyList<string> lines;
        using (var capture = EngineLogCapture.Start())
        {
            value = session.Parse(target, parser);
            lines = capture.Stop();
        }
        Assert.Empty(lines);
        return value;
    }

    [Fact]
    public void ASectionGivesTheValuesThatTheEngineReads()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");

        var plain = Parse<SystemObject>(session, Obj("plain"), SystemObject.TryParse)!;

        Assert.Equal("plain", plain.Nickname);
        Assert.Equal(new Vector3(1, 2, 3), plain.Pos);
        Assert.Equal("depot", plain.Archetype);
    }

    /// <summary>W keeps the placeholder of a mod text. The merge puts the id of the text there, but an unsaved text
    /// has no id, so the parser gets 0.</summary>
    [Fact]
    public void ATextPlaceholderIsZero()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLines(Obj("plain"), "ids_name", [[new StringValue("$str:depot")]]));

        // A placeholder without text is an error of the checks.
        Assert.Null(Parse<SystemObject>(session, Obj("plain"), SystemObject.TryParse));
        session.Execute(EditCommand.SetText(StringKind.String, "depot", "Depot"));

        Assert.Equal(0, Parse<SystemObject>(session, Obj("plain"), SystemObject.TryParse)!.IdsName);
    }

    [Fact]
    public void AKeyThatTheSchemaDoesNotKnowIsLeftOut()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");

        Assert.Equal("depot", Parse<SystemObject>(session, Obj("unknown_key"), SystemObject.TryParse)!.Archetype);
    }

    /// <summary>The checks report a repeated key as a warning, so the parse runs. The engine reads the last line.</summary>
    [Fact]
    public void ARepeatedKeyKeepsItsLastLine()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");
        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(session.Check(Obj("repeated"))).Severity);

        Assert.Equal("second", Parse<SystemObject>(session, Obj("repeated"), SystemObject.TryParse)!.Archetype);
    }

    /// <summary>The engine reads a key of a name in any letter case, so 'Pos' is an earlier line of 'pos'.</summary>
    [Fact]
    public void ALineInOtherLetterCaseIsALineOfTheSameKey()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");

        Assert.Equal(new Vector3(4, 5, 6), Parse<SystemObject>(session, Obj("cases"), SystemObject.TryParse)!.Pos);
    }

    /// <summary>The engine reads each line of a key of many lines in order. A faction line belongs to the encounter
    /// line before it.</summary>
    [Fact]
    public void EachLineOfAKeyOfManyLinesStays()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");

        var zone = Parse<Zone>(session, Encounters, Zone.TryParse)!;

        Assert.Equal(["area_scout", "area_patrol"], zone.Encounters.Select(e => e.Archetype));
        Assert.Equal([new FactionSpawn("li_n_grp", 1)], zone.Encounters[0].FactionSpawns);
        Assert.Equal([new FactionSpawn("li_p_grp", 0.5f), new FactionSpawn("li_lsf_grp", 0.5f)],
            zone.Encounters[1].FactionSpawns);
    }

    [Fact]
    public void EachValueOfAnArrayStays()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");

        Assert.Equal(["lane_a", "lane_b"], Parse<Zone>(session, Encounters, Zone.TryParse)!.PathLabel);
    }

    /// <summary>The merge puts the id of a text in place of a placeholder in each value of a line, so the copy has 0
    /// in each such value.</summary>
    [Fact]
    public void APlaceholderIsZeroInEachValue()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");
        session.Execute(EditCommand.SetText(StringKind.String, "note", "Note"));
        session.Execute(EditCommand.SetLines(Encounters, "comment", [[new StringValue("first"), new StringValue("$str:note")]]));
        session.Execute(EditCommand.SetLines(Encounters, "path_label", [[new StringValue("a"), new StringValue("$str:note")]]));

        var zone = Parse<Zone>(session, Encounters, Zone.TryParse)!;

        Assert.Equal("first, 0", zone.Comment);
        Assert.Equal(["a", "0"], zone.PathLabel);
    }

    /// <summary>
    /// The engine reads each string as an integer and logs nothing: -1 from a binary file, and from a text file the
    /// number at the start of the text, or 0. The checks give a warning with that integer, and the parse gives it. W
    /// can hold a plain string (the editor makes it) or a string of the text parser (a field or a patch line gives it).
    /// Both get the form of the file.
    /// </summary>
    [Theory]
    [InlineData(true, -1, -1, -1)]
    [InlineData(false, 0, 5, 7)]
    public void AStringInAnIntegerKeyGivesTheIntegerThatTheEngineReads(bool binary, int text, int plain, int fromText)
    {
        using var ws = SystemWorkspace.Create(StringZone, binary);
        var session = ws.Session("act");
        var zone = SystemWorkspace.Li01Section("Zone", "lane");

        AssertToughness(session, zone, "br01_2", text);
        session.Execute(EditCommand.SetLines(zone, "toughness", [[new StringValue("5x")]]));
        AssertToughness(session, zone, "5x", plain);
        session.Execute(EditCommand.SetLinesFromText(zone, "toughness", ["7y"]));
        AssertToughness(session, zone, "7y", fromText);
    }

    private static void AssertToughness(EditSession session, SectionRef zone, string text, int engine)
    {
        var problem = Assert.Single(session.Check(zone));
        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.EndsWith($"the game reads '{text}' in a 'toughness' line as the integer {engine}.", problem.Message);
        Assert.Equal(engine, Parse<Zone>(session, zone, Zone.TryParse)!.Toughness);
    }

    /// <summary>
    /// The retail game never reads <c>lane_id</c>, so the checks skip its lines (decision D513 of the console filter
    /// design): no warning for the string, and none for the second line.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheChecksSkipAKeyThatTheRetailGameDoesNotRead(bool binary)
    {
        using var ws = SystemWorkspace.Create(LaneZone, binary);
        var session = ws.Session("act");
        var lane = SystemWorkspace.Li01Section("Zone", "lane");

        Assert.Empty(session.Check(lane));
        Assert.False(session.Schema(lane, "lane_id")!.RetailReads);
        Assert.True(session.Schema(lane, "toughness")!.RetailReads);
    }

    [Fact]
    public void ALineKeepsTheValuesThatItsTypeReads()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");

        var parsed = Parse<SystemObject>(session, Obj("too_many"), SystemObject.TryParse)!;

        Assert.Equal(new Vector3(1, 2, 3), parsed.Pos);
        Assert.Equal("a", parsed.Loadout);
    }

    [Fact]
    public void AnErrorOfTheChecksGivesNoValue()
    {
        using var ws = SystemWorkspace.Create(Li01);
        var session = ws.Session("act");

        Assert.Null(Parse<LightSource>(session, SystemWorkspace.Li01Section("LightSource", "bad_type"), LightSource.TryParse));
        Assert.Null(Parse<Zone>(session, SystemWorkspace.Li01Section("Zone", "short_pos"), Zone.TryParse));
        Assert.Null(Parse<SystemObject>(session, Obj("nothing"), SystemObject.TryParse));
    }

    /// <summary>The engine reads the system file of a [system] while it parses the section. Parse gives the parser no
    /// file system, so the system has no objects.</summary>
    [Fact]
    public void AUniverseSystemParsesWithoutAFileRead()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");

        var system = Parse<StarSystem>(session, session.SystemSection("li01")!, StarSystem.TryParse)!;

        Assert.Equal("Li01", system.Nickname);
        Assert.Equal(@"Systems\Li01\Li01.ini", system.File);
        Assert.Equal(196609, system.IdsName);
        Assert.Equal(1.36f, system.NavMapScale);
        Assert.Equal(new Vector2(1, 2), system.Pos);
        Assert.Empty(system.Objects);
        Assert.Null(system.Info);
    }
}

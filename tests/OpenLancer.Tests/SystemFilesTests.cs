using System;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>A synthetic game install with a universe and star system files.</summary>
internal static class SystemWorkspace
{
    public const string UniversePath = @"DATA\UNIVERSE\universe.ini";
    public const string Li01Path = @"DATA\UNIVERSE\SYSTEMS\LI01\li01.ini";

    public const string FreelancerIni = $"""
        [Freelancer]
        data path = ..\data

        {TestWorkspace.ResourcesSection}

        [Data]
        universe = universe\universe.ini
        """;

    /// <summary>
    /// Li01 has a file. St01 names its file two times, the last time with a key and a path in other letter cases.
    /// Nofile has no file, and two systems have the nickname twice. Upper is a [System] section in other letter case.
    /// One system has two nickname lines, so it has no key. Empty_last ends with a file line without a value.
    /// </summary>
    public const string Universe = """
        [Time]
        seconds_per_day = 1800

        [system]
        nickname = Li01
        file = Systems\Li01\Li01.ini
        strid_name = 196609
        ids_info = 66106
        navmapscale = 1.36
        pos = 1, 2

        [system]
        nickname = st01
        file = Systems\St01\old.ini
        FILE = SYSTEMS\ST01\st01.ini

        [system]
        nickname = nofile

        [system]
        nickname = twice
        file = Systems\tw1\tw1.ini

        [system]
        nickname = twice
        file = Systems\tw2\tw2.ini

        [System]
        nickname = upper
        file = Systems\Up\up.ini

        [system]
        nickname = two_a
        nickname = two_b
        file = Systems\Two\two.ini

        [system]
        nickname = empty_last
        file = Systems\El\el.ini
        file =

        [Base]
        nickname = Li01_01_Base
        system = Li01
        file = Universe\Systems\Li01\Bases\Li01_01_Base.ini
        """;

    public const string Li01 = """
        [SystemInfo]
        space_color = 0, 0, 0
        local_faction = li_n_grp

        [Object]
        nickname = Li01_depot
        ids_name = 261164
        pos = -38675, 0, -27705
        archetype = depot

        [Zone]
        nickname = Zone_death
        pos = 0, 0, 0
        shape = SPHERE
        size = 4100

        [LightSource]
        nickname = Light_main
        pos = 1, 2, 3
        type = DIRECTIONAL
        color = 255, 255, 255
        """;

    public static SectionRef System(string nickname) => new(UniversePath, new SectionIdentity("system", nickname));

    public static SectionRef Li01Section(string type, string? nickname) => new(Li01Path, new SectionIdentity(type, nickname));

    /// <summary>A workspace with the universe and the base file of Li01, an empty active mod "act" and the profile.</summary>
    /// <param name="li01">The text of Li01.ini, or null for <see cref="Li01"/>.</param>
    /// <param name="binary">True to write Li01.ini as a binary (BINI) file, as retail stores it.</param>
    public static TestWorkspace Create(string? li01 = null, bool binary = false)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"EXE\freelancer.ini", FreelancerIni);
        ws.WriteBase(UniversePath, Universe);
        if (binary)
            ws.WriteBaseBini(Li01Path, li01 ?? Li01);
        else
            ws.WriteBase(Li01Path, li01 ?? Li01);
        ws.Mod("act", new());
        ws.WriteProfile("act");
        return ws;
    }
}

/// <summary>The edit session finds universe.ini and the file of each system as the engine finds them.</summary>
public sealed class SystemFilesTests
{
    [Fact]
    public void TheUniverseGivesThePathOfEachSystem()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal(SystemWorkspace.UniversePath, session.UniversePath, ignoreCase: true);
        // The path is relative to the folder of universe.ini. Nicknames, keys and paths compare without letter case.
        Assert.Equal(SystemWorkspace.Li01Path, session.SystemPath("LI01"), ignoreCase: true);
        // The engine reads only the last file line.
        Assert.Equal(@"DATA\UNIVERSE\SYSTEMS\ST01\st01.ini", session.SystemPath("st01"), ignoreCase: true);
        Assert.Null(session.SystemPath("nofile"));
        Assert.Equal(SystemWorkspace.System("nofile"), session.SystemSection("nofile"));
        // Two [system] sections have the nickname, and the engine uses only one of them.
        Assert.Null(session.SystemPath("twice"));
        Assert.Null(session.SystemSection("twice"));
        Assert.Null(session.SystemPath("nothing"));
        Assert.Null(session.SystemSection("nothing"));
        // The engine reads a [system] section in any letter case (upstream UniverseIni).
        Assert.Equal(@"DATA\UNIVERSE\Systems\Up\up.ini", session.SystemPath("upper"), ignoreCase: true);
        // A section with two nickname lines has no key, so no nickname finds it.
        Assert.Null(session.SystemPath("two_a"));
        Assert.Null(session.SystemPath("two_b"));
        Assert.Null(session.SystemSection("two_b"));
        // The engine ignores a file line without a value, so it keeps the file of the line before.
        Assert.Equal(@"DATA\UNIVERSE\Systems\El\el.ini", session.SystemPath("empty_last"), ignoreCase: true);

        var li01 = session.SystemSection("li01");
        Assert.Equal(SystemWorkspace.System("Li01"), li01);
        Assert.Equal("system", li01!.Id.Type);
        Assert.Equal("Li01", li01.Id.Key);
    }

    [Fact]
    public void TheDataPathAndTheLastUniverseEntryGiveTheUniversePath()
    {
        using var ws = SystemWorkspace.Create();
        ws.WriteBase(@"EXE\freelancer.ini", $"""
            [Freelancer]
            data path = ..\gamedata

            {TestWorkspace.ResourcesSection}

            [Data]
            universe = universe\universe.ini

            [Data]
            universe = world\all.ini
            """);
        ws.WriteBase(@"GAMEDATA\WORLD\all.ini", "[system]\nnickname = li01\nfile = li01.ini");
        var session = ws.Session("act");

        Assert.Equal(@"gamedata\world\all.ini", session.UniversePath, ignoreCase: true);
        Assert.Equal(@"gamedata\world\li01.ini", session.SystemPath("li01"), ignoreCase: true);
    }

    [Fact]
    public void AFreelancerIniWithoutAUniverseGivesNoSystem()
    {
        using var ws = SystemWorkspace.Create();
        ws.WriteBase(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n" + TestWorkspace.ResourcesSection);
        var session = ws.Session("act");

        Assert.Null(session.UniversePath);
        Assert.Null(session.SystemPath("li01"));
        Assert.Null(session.SystemSection("li01"));
        Assert.Empty(session.Schema(SystemWorkspace.Li01Section("Object", "Li01_depot")));
    }

    /// <summary>The map reads W of universe.ini, so an edit, its undo and its redo change the paths at once.</summary>
    [Fact]
    public void ThePathsFollowTheEditsOfTheUniverse()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        Assert.Equal(SystemWorkspace.Li01Path, session.SystemPath("li01"), ignoreCase: true);

        session.Execute(EditCommand.SetLinesFromText(SystemWorkspace.System("li01"), "file", [@"Systems\Li01\New.ini"]));
        Assert.Equal(@"DATA\UNIVERSE\Systems\Li01\New.ini", session.SystemPath("li01"), ignoreCase: true);
        session.Execute(EditCommand.AddSection(SystemWorkspace.UniversePath, "system",
            [new EntryView("nickname", [new StringValue("li02")]), new EntryView("file", [new StringValue(@"Systems\Li02\Li02.ini")])]));
        Assert.Equal(@"DATA\UNIVERSE\Systems\Li02\Li02.ini", session.SystemPath("li02"), ignoreCase: true);

        session.Undo();
        Assert.Null(session.SystemPath("li02"));
        session.Undo();
        Assert.Equal(SystemWorkspace.Li01Path, session.SystemPath("li01"), ignoreCase: true);
        session.Redo();
        Assert.Equal(@"DATA\UNIVERSE\Systems\Li01\New.ini", session.SystemPath("li01"), ignoreCase: true);
    }

    [Fact]
    public void SectionsListsTheSectionsOfAFileInFileOrder()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal(
        [
            SystemWorkspace.Li01Section("SystemInfo", null),
            SystemWorkspace.Li01Section("Object", "Li01_depot"),
            SystemWorkspace.Li01Section("Zone", "Zone_death"),
            SystemWorkspace.Li01Section("LightSource", "Light_main"),
        ], session.Sections(session.SystemPath("li01")!));
        Assert.Equal(10, session.Sections(SystemWorkspace.UniversePath).Count);
        Assert.Empty(session.Sections(@"DATA\UNIVERSE\SYSTEMS\NONE\none.ini"));
    }
}

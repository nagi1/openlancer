using System;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The schema and the fast checks of the sections of universe.ini and of the star system files.</summary>
public sealed class SystemSchemaTests
{
    private static readonly SectionRef Depot = SystemWorkspace.Li01Section("Object", "Li01_depot");

    [Fact]
    public void ASystemFileHasTheSchemaOfTheStarSystemClass()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal(typeof(Vector3), session.Schema(Depot, "pos")!.ClrType);
        Assert.True(session.Schema(Depot, "nickname")!.IsKeyEntry);
        Assert.Equal(StringKind.String, session.Schema(Depot, "ids_name")!.TextKind);
        Assert.Equal(Vec3Mode.Size, session.Schema(SystemWorkspace.Li01Section("Zone", "Zone_death"), "size")!.Mode);
        Assert.Equal(typeof(LightType), session.Schema(SystemWorkspace.Li01Section("LightSource", "Light_main"), "type")!.ClrType);
        Assert.NotNull(session.Schema(SystemWorkspace.Li01Section("SystemInfo", null), "space_color"));
        Assert.Equal(SchemaCatalog.Section(typeof(StarSystem), "object")!.Keys, session.Schema(Depot));
        // A file that no [system] names has no schema, also in the folder of the systems.
        Assert.Empty(session.Schema(Depot with { Path = @"DATA\UNIVERSE\SYSTEMS\LI01\other.ini" }));
    }

    /// <summary>The engine reads universe.ini without a [ParsedIni] class, so the schema comes from the section
    /// class and its base class UniverseElement.</summary>
    [Fact]
    public void TheSectionsOfTheUniverseHaveTheSchemaOfTheirClass()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var li01 = session.SystemSection("li01")!;

        var keys = session.Schema(li01);

        Assert.Equal(SchemaCatalog.SectionClass(typeof(StarSystem), "system").Keys, keys);
        Assert.Equal(["nickname", "strid_name", "name", "pos", "msg_id_prefix", "visit", "ids_info", "navmapscale", "file"],
            keys.Select(k => k.Name));
        Assert.True(keys[0].IsKeyEntry);
        Assert.Equal(StringKind.String, session.Schema(li01, "strid_name")!.TextKind);
        Assert.Equal(StringKind.Infocard, session.Schema(li01, "ids_info")!.TextKind);
        Assert.True(session.Schema(li01, "file")!.Required);
        var baseSection = new SectionRef(SystemWorkspace.UniversePath, new SectionIdentity("Base", "Li01_01_Base"));
        Assert.NotNull(session.Schema(baseSection, "system"));
        Assert.NotNull(session.Schema(baseSection, "strid_name"));
        Assert.Empty(session.Schema(new SectionRef(SystemWorkspace.UniversePath, new SectionIdentity("Time", null))));
    }

    /// <summary>The fast checks run on the sections of a system file. The nickname check of the item files does not
    /// apply to them.</summary>
    [Fact]
    public void TheFastChecksRunOnTheSectionsOfTheSystemFiles()
    {
        using var ws = SystemWorkspace.Create(SystemWorkspace.Li01 + """

            [LightSource]
            nickname = Light_bad
            type = BOAT
            colour = red
            pos = 1, 2
            """);
        var session = ws.Session("act");

        Assert.Empty(session.Check(Depot));
        Assert.Empty(session.Check(session.SystemSection("li01")!));
        Assert.Equal(
        [
            "Error: 'BOAT' in a 'type' line is not one of Point, Directional. The game ignores the line.",
            "Warning: the section type has no key 'colour'. The game ignores the line.",
            "Error: a 'pos' line needs 3 values or more, not 2. The game ignores the line.",
        ], session.Check(SystemWorkspace.Li01Section("LightSource", "Light_bad"))
            .Select(p => $"{p.Severity}: {p.Message[(p.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..]}"));
    }
}

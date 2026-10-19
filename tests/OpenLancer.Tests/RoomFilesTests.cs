using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Editor.Editing;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A small install with two bases, three room files and the cutscene scripts that the rooms name (section 5.5 of the
/// M8b design). The second base shares the bar file of the first base.
/// </summary>
internal static class RoomWorkspace
{
    public const string UniversePath = @"DATA\UNIVERSE\universe.ini";
    public const string Base1Path = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\li01_01_base.ini";
    public const string Base2Path = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\li01_02_base.ini";
    public const string BarPath = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\ROOMS\li01_01_bar.ini";
    public const string DeckPath = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\ROOMS\li01_01_deck.ini";
    public const string ShopPath = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\ROOMS\li01_02_shop.ini";
    public const string BarPatch = @"patches\" + BarPath;

    /// <summary>The cutscene scripts of the rooms, as install paths.</summary>
    public static readonly string[] Scripts =
    [
        @"DATA\SCRIPTS\BASES\Li_01_bar_hardpoint_01.thn", @"DATA\SCRIPTS\BASES\Li_01_bar_ambi_int_01.thn",
        @"DATA\SCRIPTS\BASES\Li_01_bar_ambi_int_s.thn", @"DATA\SCRIPTS\BASES\Li_01_bar_enter_01.thn",
        @"DATA\SCRIPTS\BASES\Li_01_deck_hardpoint_01.thn", @"DATA\SCRIPTS\BASES\Li_01_deck_land_01.thn",
        @"DATA\SCRIPTS\BASES\Li_01_deck_launch_01.thn",
    ];

    public const string Bar = """
        [Room_Info]
        set_script = Scripts\Bases\Li_01_bar_hardpoint_01.thn
        scene = all, ambient, Scripts\Bases\Li_01_bar_ambi_int_01.thn
        scene = ambient, scripts/bases/LI_01_BAR_AMBI_int_s.THN, TRAFFIC_PRIORITY
        [Camera]
        name = Camera_0
        [CharacterPlacement]
        name = Zone_Bar_Character_Start
        start_script = Scripts\Bases\Li_01_bar_enter_01.thn
        """;

    /// <summary>A workspace with the files in the base folder, the empty mods "lower" and "act", and the profile.</summary>
    public static TestWorkspace Create(Dictionary<string, string>? activeFiles = null,
        Dictionary<string, string>? lowerFiles = null)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"EXE\freelancer.ini", $"""
            [Freelancer]
            data path = ..\data

            {TestWorkspace.ResourcesSection}

            [Data]
            universe = universe\universe.ini
            """);
        ws.WriteBase(UniversePath, """
            [Base]
            nickname = Li01_01_Base
            system = Li01
            file = Universe\Systems\Li01\Bases\li01_01_base.ini
            [Base]
            nickname = Li01_02_Base
            system = Li01
            file = universe/systems/LI01/bases/li01_02_base.ini
            """);
        ws.WriteBase(Base1Path, """
            [BaseInfo]
            nickname = Li01_01_Base
            [Room]
            nickname = Bar
            file = Universe\Systems\Li01\Bases\Rooms\li01_01_bar.ini
            [Room]
            nickname = Deck
            file = Universe\Systems\Li01\Bases\Rooms\li01_01_deck.ini
            """);
        ws.WriteBase(Base2Path, """
            [BaseInfo]
            nickname = Li01_02_Base
            [Room]
            nickname = Bar
            file = Universe\Systems\Li01\Bases\Rooms\li01_01_bar.ini
            [Room]
            nickname = Shop
            file = Universe\Systems\Li01\Bases\Rooms\li01_02_shop.ini
            """);
        ws.WriteBase(BarPath, Bar);
        ws.WriteBase(DeckPath, """
            [Room_Info]
            set_script = Scripts\Bases\Li_01_deck_hardpoint_01.thn
            [PlayerShipPlacement]
            name = X
            landing_script = Scripts\Bases\Li_01_deck_land_01.thn
            launching_script = Scripts\Bases\Li_01_deck_launch_01.thn
            """);
        ws.WriteBase(ShopPath, """
            [Room_Info]
            [CharacterPlacement]
            name = Zone_Shop
            """);
        foreach (var script in Scripts)
            ws.WriteBase(script, "duration = 1\nentities = {}\nevents = {}\n");
        ws.Mod("lower", lowerFiles ?? new());
        ws.Mod("act", activeFiles ?? new());
        ws.WriteProfile("lower", "act");
        return ws;
    }

    /// <summary>The section of a type of a room file.</summary>
    public static SectionRef Section(EditSession session, string path, string type) =>
        session.Sections(path).Single(s => s.Id.Type == type);
}

/// <summary>
/// The room files (decisions D66 and D135, test 5 of section 8.1 of the M8b design): the role from the bases of
/// universe.ini, the schema of the room files, and the patch of a singleton room section.
/// </summary>
public sealed class RoomFilesTests
{
    [Fact]
    public void TheRoomFilesComeFromTheBasesOfUniverseIniWithoutRepeats()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal([RoomWorkspace.BarPath, RoomWorkspace.DeckPath, RoomWorkspace.ShopPath],
            session.DataFiles.Paths("room"), StringComparer.OrdinalIgnoreCase);
        Assert.True(session.IsRoomFile(RoomWorkspace.BarPath.ToLowerInvariant().Replace('\\', '/')));
        Assert.False(session.IsRoomFile(RoomWorkspace.Base1Path));
        Assert.False(session.IsRoomFile(RoomWorkspace.UniversePath));
        Assert.Contains("room", session.DataFiles.RolesOf(RoomWorkspace.DeckPath));
        Assert.Empty(session.DataFiles.RolesOf(RoomWorkspace.Base1Path));
        // The first use reads the base files from the edit view, so it opens no document of them.
        Assert.Null(session.OpenedDocument(RoomWorkspace.Base1Path));
        Assert.Null(session.OpenedDocument(RoomWorkspace.Base2Path));
    }

    /// <summary>The map reads W of a base file that the session has open, so an edit of a [Room] shows at once.</summary>
    [Fact]
    public void AnEditOfABaseFileChangesTheRoomFiles()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");
        Assert.True(session.IsRoomFile(RoomWorkspace.ShopPath));

        var shop = session.Sections(RoomWorkspace.Base2Path).Single(s => s.Id.Key == "Shop");
        session.Execute(EditCommand.DeleteSection(shop));

        Assert.False(session.IsRoomFile(RoomWorkspace.ShopPath));
        Assert.True(session.IsRoomFile(RoomWorkspace.BarPath));
        session.Undo();
        Assert.True(session.IsRoomFile(RoomWorkspace.ShopPath));
    }

    /// <summary>The editor knows the schema of a room file (decision D135), so the Inspector edits the lines of
    /// [CharacterPlacement]. A section without a key stays read-only.</summary>
    [Fact]
    public void TheSchemaOfARoomFileMakesItsSingletonSectionsEditable()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");
        var placement = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "CharacterPlacement");
        var camera = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "Camera");

        Assert.True(session.KnowsSchema(RoomWorkspace.BarPath));
        Assert.False(session.KnowsSchema(RoomWorkspace.Base1Path));
        var binding = FieldBinding.Create(session, placement, session.Get(placement)!, "start_script");
        Assert.Null(binding.ReadOnlyReason);
        Assert.NotNull(binding.Key);
        var info = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "Room_Info");
        Assert.True(session.Schema(info, "scene")!.Multiline);
        Assert.NotNull(FieldBinding.SectionLock(session, camera, session.Get(camera)!) ??
                       session.Get(camera)!.ReadOnlyReason);
    }

    /// <summary>Decision D66: [CharacterPlacement] is a singleton, so a patch section of the type finds its section by
    /// its type. The save writes one section with the new line, and the merge puts it into the room file.</summary>
    [Fact]
    public void APatchOfCharacterPlacementMergesIntoTheRoomFile()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");
        var placement = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "CharacterPlacement");

        session.Execute(EditCommand.SetLinesFromText(placement, "start_script", [@"Scripts\Bases\Li_01_deck_land_01.thn"]));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        var patch = File.ReadAllText(Path.Combine(ws.ModFolder("act"), RoomWorkspace.BarPatch));
        Assert.Equal(1, patch.Split('[').Length - 1);
        Assert.Contains("[CharacterPlacement]", patch);
        Assert.Contains(@"start_script = Scripts\Bases\Li_01_deck_land_01.thn", patch);
        var merged = TestWorkspace.Sections(session.EditView, RoomWorkspace.BarPath);
        var section = Assert.Single(merged, s => s.Name == "CharacterPlacement");
        Assert.Equal(@"Scripts\Bases\Li_01_deck_land_01.thn",
            section.Single(e => e.Name == "start_script")[0].ToString());
        Assert.Equal("Zone_Bar_Character_Start", section.Single(e => e.Name == "name")[0].ToString());
        Assert.Equal(SectionMatchKind.Singleton, SectionKeys.For("CharacterPlacement").Kind);
        Assert.Equal(SectionMatchKind.Singleton, SectionKeys.For("room_info").Kind);
        Assert.Equal(SectionMatchKind.Singleton, SectionKeys.For("PlayerShipPlacement").Kind);
    }
}

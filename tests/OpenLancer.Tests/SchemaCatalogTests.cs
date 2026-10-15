using System;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Audio;
using LibreLancer.Data.Schema.Equipment;
using LibreLancer.Data.Schema.Goods;
using LibreLancer.Data.Schema.Interface;
using LibreLancer.Data.Schema.Ships;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The schema catalog, the key schema of the edit session, and the fast checks of a section.</summary>
public sealed class SchemaCatalogTests
{
    private static KeySchema Key(Type ini, string section, string name) =>
        SchemaCatalog.Section(ini, section)!.Key(name) ?? throw new InvalidOperationException($"No key {name}.");

    [Fact]
    public void AKeyHasTheRulesOfTheGeneratedParser()
    {
        // A field of a base class, the key entry, and the type of one line of a multiline list.
        Assert.Equal(new KeySchema("nickname", typeof(string), false, false, 1, true, null) { Field = "Nickname" },
            Key(typeof(EquipmentIni), "gun", "nickname"));
        Assert.Equal(new KeySchema("material_library", typeof(string), true, false, 1, false, null) { Field = "MaterialLibrary" },
            Key(typeof(EquipmentIni), "gun", "material_library"));
        Assert.Equal(typeof(float[]), Key(typeof(EquipmentIni), "gun", "lodranges").ClrType);
        Assert.Equal(StringKind.String, Key(typeof(EquipmentIni), "gun", "ids_name").TextKind);
        Assert.Equal(StringKind.Infocard, Key(typeof(ShiparchIni), "ship", "ids_info3").TextKind);
        // A key that an entry handler reads has no type.
        Assert.Equal(new KeySchema("addon", null, true, false, 2, false, null), Key(typeof(GoodsIni), "good", "addon"));
        Assert.Equal(new KeySchema("marketgood", null, true, false, 7, false, null), Key(typeof(MarketsIni), "basegood", "marketgood"));
        // A private field, an enum, and a required key.
        Assert.Equal(2, Key(typeof(ShiparchIni), "ship", "camera_offset").MinComponents);
        Assert.True(Key(typeof(ShiparchIni), "ship", "type").ClrType!.IsEnum);
        Assert.True(Key(typeof(ShiparchIni), "ship", "da_archetype").Required);
        // A [BaseGood] has no key entry.
        Assert.False(Key(typeof(MarketsIni), "basegood", "base").IsKeyEntry);
    }

    /// <summary>
    /// Two names of one field set one value, so they share the field. Two names of one entry handler do not share a
    /// field: the handler reads each line, for example a faction line and a faction_weight line add two spawns.
    /// </summary>
    [Fact]
    public void TheNamesOfOneFieldShareTheField()
    {
        Assert.Equal("AmbientColor", Key(typeof(StarSystem), "object", "ambient_color").Field);
        Assert.Equal("AmbientColor", Key(typeof(StarSystem), "object", "ambient").Field);
        Assert.Equal("Spacedust", Key(typeof(StarSystem), "dust", "dust").Field);
        Assert.Equal("Spacedust", Key(typeof(StarSystem), "dust", "spacedust").Field);
        Assert.Null(Key(typeof(StarSystem), "zone", "faction").Field);
        Assert.Null(Key(typeof(StarSystem), "zone", "faction_weight").Field);
    }

    [Fact]
    public void AKeyHasTheFourAttributeFieldsOfReviewFindingF15()
    {
        var separable = Key(typeof(ShiparchIni), "collisiongroup", "separable");
        Assert.True(separable.Presence);
        Assert.Equal(0, separable.MinComponents);
        var range = Key(typeof(AudioIni), "sound", "range");
        Assert.True(range.MinMax);
        Assert.Equal(1, range.MinComponents);
        Assert.Equal(typeof(Vector2), range.ClrType);
        var size = SchemaCatalog.All.First(s => s.Section.Equals("object", StringComparison.OrdinalIgnoreCase) &&
                                                s.Ini == typeof(StarSystem)).Schema.Key("size")!;
        Assert.Equal(Vec3Mode.Size, size.Mode);
        Assert.Equal(1, size.MinComponents);
        Assert.False(Key(typeof(EquipmentIni), "gun", "hit_pts").FloatColor);
    }

    [Fact]
    public void TheKeysAreInDeclarationOrderAndTheTextKindDependsOnTheSectionType()
    {
        var gun = SchemaCatalog.Section(typeof(EquipmentIni), "Gun")!;
        // The fields of the base class AbstractEquipment come first.
        Assert.Equal("nickname", gun.Keys[0].Name);
        Assert.True(gun.Keys.ToList().FindIndex(k => k.Name == "turn_rate") > gun.Keys.ToList().FindIndex(k => k.Name == "hit_pts"));
        Assert.Equal(typeof(Gun), gun.Class);
        Assert.Equal(StringKind.String, Key(typeof(KeymapIni), "keycmd", "ids_info").TextKind);
        Assert.Equal(StringKind.Infocard, SchemaCatalog.TextKind("Gun", "IDS_INFO"));
        Assert.Null(SchemaCatalog.TextKind("Gun", "hit_pts"));
        // Each section type of the item files has a schema.
        Assert.All(new[] { (typeof(ShiparchIni), "ship"), (typeof(ShiparchIni), "collisiongroup"), (typeof(EquipmentIni), "lod"),
            (typeof(EquipmentIni), "munition"), (typeof(GoodsIni), "good"), (typeof(MarketsIni), "basegood") },
            s => Assert.NotNull(SchemaCatalog.Section(s.Item1, s.Item2)));
    }

    [Fact]
    public void TheSessionGivesTheSchemaOfTheSectionsOfTheItemFiles()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal("nickname", session.Schema(ItemWorkspace.Gun("gun1"))[0].Name);
        Assert.Equal(StringKind.String, session.Schema(ItemWorkspace.Gun("gun1"), "IDS_NAME")!.TextKind);
        Assert.Null(session.Schema(ItemWorkspace.Gun("gun1"), "no_such_key"));
        Assert.NotNull(session.Schema(ItemWorkspace.Ref(ItemWorkspace.ShipsPath, "CollisionGroup", null) with
        {
            Id = new SectionIdentity("CollisionGroup", null, new SectionIdentity("Ship", "s1")),
        }, "obj"));
        // The freelancer.ini of this workspace names no universe, so universe.ini has no schema.
        Assert.Empty(session.Schema(new SectionRef(@"DATA\UNIVERSE\universe.ini", new SectionIdentity("System", "li01"))));
    }

    [Fact]
    public void TheFastChecksFindWhatTheEngineCannotRead()
    {
        using var ws = ItemWorkspace.Create();
        ws.WriteBase(ItemWorkspace.ShipsPath, """
            [Ship]
            nickname = s1
            mass = 100
            mass = 200
            type = BOAT
            steering_torque = 1, 2
            angular_drag = 0
            linear_drag = heavy
            hold_size = 7
            colour = red
            hp_type = hp_gun
            fuse = f, 0, 100
            """);
        var session = ws.Session("act");

        var problems = session.Check(ItemWorkspace.Ship("s1"));

        Assert.All(problems, p => Assert.Equal(ItemWorkspace.Ship("s1"), p.Target));
        Assert.Equal(
        [
            "Warning: 'mass' has 2 lines. The engine reads only the last line.",
            "Error: the engine cannot read 'BOAT' in a 'type' line as one of " +
            string.Join(", ", Enum.GetNames(Key(typeof(ShiparchIni), "ship", "type").ClrType!)) + ".",
            "Error: a 'steering_torque' line needs 3 values or more, not 2. The engine ignores the line.",
            "Error: the engine cannot read 'heavy' in a 'linear_drag' line as a number.",
            "Warning: the engine does not know the key 'colour'. It ignores the line.",
            "Error: a 'hp_type' line needs 2 values or more, not 1. The engine ignores the line.",
            "Error: the key 'da_archetype' is missing. The engine does not load the section without it.",
        ], problems.Select(p => $"{p.Severity}: {p.Message[(p.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..]}"));
        // Each problem names the key of its lines, so a caller can find the problems of one key.
        Assert.Equal(["mass", "type", "steering_torque", "linear_drag", "colour", "hp_type", "da_archetype"],
            problems.Select(p => p.Key));
        // A section that the engine reads without a problem.
        Assert.Empty(session.Check(ItemWorkspace.Gun("gun1")));
        Assert.Empty(session.Check(ItemWorkspace.Gun("nothing")));
    }

    /// <summary>
    /// The engine reads a string as an integer and logs nothing. A text file reads the number at the start of the
    /// string. So the engine loads the line, and the checks give a warning that names the integer. (SectionParseTests
    /// has the binary form.)
    /// </summary>
    [Fact]
    public void TheFastChecksNameTheIntegerThatTheEngineReadsFromAString()
    {
        using var ws = ItemWorkspace.Create();
        ws.WriteBase(ItemWorkspace.GoodsPath, "[Good]\nnickname = g1\nprice = 5x\nids_name = 2.5");
        var session = ws.Session("act");

        var problem = Assert.Single(session.Check(ItemWorkspace.Good(ItemWorkspace.GoodsPath, "g1")));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.EndsWith("the engine reads '5x' in a 'price' line as the integer 5.", problem.Message);
    }

    /// <summary>
    /// W keeps the text placeholders of the active mod. The merge puts the id of the text in place of a placeholder,
    /// so the checks read a placeholder in a key that holds a text id. A placeholder of the other kind of text, and a
    /// placeholder without text, are problems.
    /// </summary>
    [Fact]
    public void TheFastChecksReadTheTextPlaceholdersOfTheMod()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var gun = ItemWorkspace.Gun("gun1");

        session.Execute(ItemEdits.SetText(session, gun, "ids_name", "Ion Gun"));
        session.Execute(ItemEdits.SetText(session, gun, "ids_info", ItemEdits.ToInfocard("Fast.")));
        Assert.Empty(session.Check(gun));

        session.Execute(EditCommand.SetLines(gun, "ids_info", [[new StringValue("$str:gun1")]]));
        session.Execute(EditCommand.SetLines(gun, "ids_name", [[new StringValue("$str:nothing")]]));
        Assert.Equal(
        [
            "Warning: 'ids_info' holds the id of an infocard, but '$str:gun1' is a string. The engine can show no text for it.",
            "Error: '$str:nothing' is not in the strings.json of mod 'act'. Add the text, or change the value.",
        ], session.Check(gun).Select(p => $"{p.Severity}: {p.Message[(p.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..]}"));
        Assert.Equal(["ids_info", "ids_name"], session.Check(gun).Select(p => p.Key));
    }
}

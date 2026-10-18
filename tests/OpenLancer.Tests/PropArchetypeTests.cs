using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Scene;
using Xunit;
using static OpenLancer.Tests.AssetWorkspace;

namespace OpenLancer.Tests;

/// <summary>The models of the assets from W (section 5.5 of the M23 design).</summary>
public sealed class ModelSourceTests
{
    [Fact]
    public void EachKindGivesItsModelAndItsLibrariesInLineOrder()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var wreck = ModelSource.Of(session, Solar("wreck_fighter"))!;
        Assert.Equal(@"Ships\Liberty\li_fighter\li_fighter.cmp", wreck.Model);
        Assert.Equal([@"ships\liberty\li_fighter\li_fighter.mat"], wreck.Libraries);
        Assert.Null(wreck.EnvMapMaterial);
        Assert.Equal(["DA_archetype", "material_library", "LODranges"], wreck.Lines.Select(l => l.Name));
        Assert.Equal(Solar("wreck_fighter"), wreck.From);

        var gunboat = ModelSource.Of(session, Ship("br_gunboat"))!;
        Assert.Equal([@"ships\bretonia\br_gunboat\br_gunboat.mat", @"ships\bretonia\br_playerships.mat"], gunboat.Libraries);
        Assert.Equal("envmapbasic", gunboat.EnvMapMaterial);
        Assert.Equal(["DA_archetype", "material_library", "material_library", "envmap_material", "LODranges"],
            gunboat.Lines.Select(l => l.Name));

        Assert.Equal(@"solar\asteroids\rock1.3db", ModelSource.Of(session, Asteroid("rock1"))!.Model);
        Assert.Equal([@"equipment\models\weapons.mat"], ModelSource.Of(session, Gun("gun1"))!.Libraries);
        Assert.Null(ModelSource.Of(session, Solar("sun1")));
    }

    /// <summary>Decision D280: a model file takes the lines of its first solar archetype, ship or asteroid. A model that
    /// only equipment names, or no section names, has no source.</summary>
    [Fact]
    public void AModelFileTakesTheLinesOfItsFirstUser()
    {
        using var ws = Create();
        var session = ws.Session("act");
        ModelSource? Of(string path) => session.Assets.ModelOf(new AssetRef(AssetKind.Model, path));

        Assert.Equal(Solar("wreck_fighter"), Of(@"SHIPS\LIBERTY\li_fighter\li_fighter.cmp")!.From);
        Assert.Equal(Ship("br_gunboat"), Of(@"SHIPS\BRETONIA\br_gunboat\br_gunboat.cmp")!.From);
        Assert.Equal(Asteroid("rock1"), Of(@"SOLAR\ASTEROIDS\rock1.3db")!.From);
        Assert.Null(Of(@"EQUIPMENT\MODELS\WEAPONS\gun1.cmp"));
        Assert.Null(Of(@"INTERFACE\orphan.3db"));
    }

    /// <summary>The paths compare below the data folder, without letter case, with <c>\</c> and <c>/</c> alike.</summary>
    [Fact]
    public void TheUsersJoinTheSpellingsOfOnePath()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Equal([Solar("wreck_fighter"), Ship("ship1")],
            session.Assets.UsersOf("SHIPS/Liberty/LI_FIGHTER/li_fighter.cmp"));
        Assert.Equal([Solar("station1"), Solar("station_nodock")], session.Assets.UsersOf(@"solar\dockable\station1.cmp"));
    }

    [Fact]
    public void AChangeOfTheModelInWChangesTheSourceAtOnce()
    {
        using var ws = Create();
        var session = ws.Session("act");
        Assert.Equal(2, session.Assets.UsersOf(FighterModel).Count);

        session.Execute(EditCommand.SetLinesFromText(Ship("ship1"), "DA_archetype", [GunboatModel]));

        Assert.Equal(GunboatModel, ModelSource.Of(session, Ship("ship1"))!.Model);
        Assert.Equal([Solar("wreck_fighter")], session.Assets.UsersOf(FighterModel));
        Assert.Contains(Ship("ship1"), session.Assets.UsersOf(GunboatModel));
    }
}

/// <summary>The static prop archetypes of decisions D94, D278 and D279 of the M23 design.</summary>
public sealed class PropArchetypeTests
{
    [Fact]
    public void ExistingFindsEachSolarArchetypeOfTheModel()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Equal(["wreck_fighter"],
            PropArchetypes.Existing(session, ModelSource.Of(session, Ship("ship1"))!).Select(a => a.Nickname));
        Assert.Equal("mission_satellite",
            PropArchetypes.Existing(session, ModelSource.Of(session, Ship("ship1"))!)[0].Type);
        Assert.Equal(["station1", "station_nodock"],
            PropArchetypes.Existing(session, ModelSource.Of(session, Solar("station1"))!).Select(a => a.Nickname));
        Assert.Empty(PropArchetypes.Existing(session, ModelSource.Of(session, Ship("br_gunboat"))!));
    }

    /// <summary>Decision D279: the [Solar] has the lines of the retail decor ship without <c>hit_pts</c>, the object
    /// comes from the template "Other solar", and both are one entry of the edit history.</summary>
    [Fact]
    public void ANewPropArchetypeAndItsObjectAreOneEntry()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var source = ModelSource.Of(session, Ship("br_gunboat"))!;

        var plan = ScenePlans.CreateProp(session, "Li01", source, "br_gunboat", new Vector3(10.4f, 0, -5.6f), null);
        session.Execute(plan.Command!);

        var solar = Solar("br_gunboat_prop");
        var added = SceneWorkspace.Object("li01_solar_1");
        Assert.Equal(["nickname = br_gunboat_prop", "type = NON_TARGETABLE",
            @"DA_archetype = ships\bretonia\br_gunboat\br_gunboat.cmp",
            @"material_library = ships\bretonia\br_gunboat\br_gunboat.mat",
            @"material_library = ships\bretonia\br_playerships.mat", "envmap_material = envmapbasic",
            "LODranges = 0, 500, 1000"], ScenePlansDeleteTests.Lines(session, solar));
        Assert.Equal(["nickname = li01_solar_1", "pos = 10, 0, -6", "archetype = br_gunboat_prop"],
            ScenePlansDeleteTests.Lines(session, added));
        Assert.Equal([solar, added], plan.Adds);
        Assert.Equal(added, plan.SelectAfter);
        Assert.Equal(solar, plan.NewArchetype);
        Assert.Equal("Create li01_solar_1 from br_gunboat", session.UndoLabel);
        Assert.Equal(SceneTemplate.Solar, ScenePlans.TemplateFor(session, "br_gunboat_prop"));

        session.Undo();
        Assert.Null(session.Get(solar));
        Assert.Null(session.Get(added));
        Assert.False(session.CanUndo);

        session.Redo();
        Assert.True(session.Save().Saved);
        Assert.Contains("[Solar]\r\nol.new = true\r\nnickname = br_gunboat_prop", ws.ReadModPatch(ListWorkspace.SolarPath));
        Assert.Contains("[Object]\r\nol.new = true\r\nnickname = li01_solar_1", ws.ReadModPatch(SceneWorkspace.Li01Path));
    }

    [Fact]
    public void TheNicknameTakesTheNextNumber()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var source = ModelSource.Of(session, Ship("br_gunboat"))!;

        session.Execute(ScenePlans.CreateProp(session, "Li01", source, "br_gunboat", Vector3.Zero, null).Command!);
        var second = ScenePlans.CreateProp(session, "Li01", source, "br_gunboat", Vector3.Zero, null);

        Assert.Equal(Solar("br_gunboat_prop_2"), second.NewArchetype);
        Assert.Equal(SceneWorkspace.Object("li01_solar_2"), second.SelectAfter);
    }

    /// <summary>Review finding R11: the name must be free in the solar archetypes, the ships and the equipment, by name
    /// and by FLHash. The test finds a name with the FLHash of a prop name at run time.</summary>
    [Fact]
    public void ANameOfAShipOrOfEquipmentByNameOrByHashTakesTheNextNumber()
    {
        var (stem, equipment) = HashCollision();
        using var ws = Create();
        ws.WriteBase(ListWorkspace.ShipsPath, Ships + "[Ship]\nnickname = x_prop\n");
        ws.WriteBase(ListWorkspace.EquipmentPath, AssetWorkspace.Equipment + $"[Light]\nnickname = {equipment}\n");
        var session = ws.Session("act");

        Assert.Equal("x_prop_2", PropArchetypes.Nickname(session, "x"));
        Assert.Equal(FLHash.CreateID(stem + "_prop"), FLHash.CreateID(equipment));
        Assert.Equal(stem + "_prop_2", PropArchetypes.Nickname(session, stem));
        Assert.Equal("wreck_fighter_prop", PropArchetypes.Nickname(session, "WRECK_FIGHTER"));
    }

    [Fact]
    public void AReadOnlyFileOfTheSolarArchetypesRefuses()
    {
        using var ws = Create(activeFiles: new() { [@"files\" + ListWorkspace.SolarPath] = AssetWorkspace.Solars });
        var session = ws.Session("act");

        var plan = ScenePlans.CreateProp(session, "Li01", ModelSource.Of(session, Ship("br_gunboat"))!, "br_gunboat",
            Vector3.Zero, null);

        Assert.Null(plan.Command);
        Assert.StartsWith("solararch.ini: ", Assert.Single(plan.Refusals).Message);
    }

    /// <summary>The values of the source keep their types, as Duplicate keeps them: a float of a binary file stays a
    /// float.</summary>
    [Fact]
    public void TheValuesOfABinarySourceKeepTheirTypes()
    {
        using var ws = Create();
        var ship = new Section("Ship");
        ship.Add(new Entry(ship, "nickname") { new StringValue("bin_ship") });
        ship.Add(new Entry(ship, "DA_archetype") { new StringValue(GunboatModel) });
        ship.Add(new Entry(ship, "LODranges") { new SingleValue(0, null), new SingleValue(250.5f, null) });
        ws.WriteBaseBini(ListWorkspace.ShipsPath, ship);
        var session = ws.Session("act");

        var plan = ScenePlans.CreateProp(session, "Li01", ModelSource.Of(session, Ship("bin_ship"))!, "bin_ship",
            Vector3.Zero, null);
        session.Execute(plan.Command!);

        var lod = session.Get(Solar("bin_ship_prop"))!.Lines("LODranges").Single().Values;
        Assert.All(lod, v => Assert.IsType<SingleValue>(v));
        Assert.True(session.Save().Saved);
        Assert.Contains("LODranges = 0.0, 250.5", ws.ReadModPatch(ListWorkspace.SolarPath));
    }

    /// <summary>A stem and a name of equipment whose FLHash is the FLHash of the prop name of the stem.</summary>
    private static (string Stem, string Equipment) HashCollision()
    {
        var names = new Dictionary<uint, string>();
        for (var m = 0; m < 300_000; m++)
            names.TryAdd(FLHash.CreateID($"e{m}"), $"e{m}");
        for (var n = 0; ; n++)
        {
            if (names.TryGetValue(FLHash.CreateID($"s{n}_prop"), out var equipment))
                return ($"s{n}", equipment);
        }
    }
}

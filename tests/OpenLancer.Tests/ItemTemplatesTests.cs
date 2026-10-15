using System;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using Xunit;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>New items from a template: the copied sections, their nicknames and the links between them.</summary>
public sealed class ItemTemplatesTests
{
    [Fact]
    public void AGunCopiesItsMunitionAndItsGoodAndNamesTheCopies()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ItemTemplates.Plan(session, Gun("gun1"), "gun2");

        Assert.Empty(plan.Problems);
        Assert.Equal(
        [
            new TemplateCopy(Munition("gun1_ammo"), Munition("gun2_ammo"), 0),
            new TemplateCopy(Gun("gun1"), Gun("gun2"), 1),
            new TemplateCopy(Good(WeaponGoodsPath, "gun1"), Good(WeaponGoodsPath, "gun2"), 0),
        ], plan.Copies);
        session.Execute(ItemTemplates.Build(plan));

        Assert.Equal("New gun2 from gun1", session.UndoLabel);
        // Review finding F12: the copies name each other, and the template does not change.
        Assert.Equal(["gun2_ammo"], Firsts(session, Gun("gun2"), "projectile_archetype"));
        Assert.Equal(["gun2"], Firsts(session, Good(WeaponGoodsPath, "gun2"), "equipment"));
        Assert.Equal(["300"], Firsts(session, Gun("gun2"), "ids_name"));
        Assert.Equal(["gun1_ammo"], Firsts(session, Gun("gun1"), "projectile_archetype"));
        Assert.Equal(["10"], Firsts(session, Munition("gun2_ammo"), "hull_damage"));
        // The child section follows its new parent.
        var lod = new SectionRef(WeaponsPath, new SectionIdentity("LOD", null, new SectionIdentity("Gun", "gun2")));
        Assert.Equal(["gun1_lod1"], Firsts(session, lod, "obj"));
        Assert.Equal([Good(WeaponGoodsPath, "gun2")], session.Related(Gun("gun2"), ItemRelation.Good));
        Assert.Equal([Munition("gun2_ammo")], session.Related(Gun("gun2"), ItemRelation.Munition));
    }

    [Fact]
    public void AShipCopiesItsLastHullGoodAndItsPackage()
    {
        using var ws = Create();
        // A second hull good in a later goods file: the engine takes the price from it.
        ws.WriteBase(WeaponGoodsPath, """
            [Good]
            nickname = s1_hull_b
            category = shiphull
            ship = s1
            price = 2000
            [Good]
            nickname = s1_package_b
            category = ship
            hull = s1_hull_b
            addon = gun1, HpWeapon01, 1
            """);
        var session = ws.Session("act");

        var plan = ItemTemplates.Plan(session, Ship("s1"), "s2");
        session.Execute(ItemTemplates.Build(plan));

        Assert.Equal(
        [
            new TemplateCopy(Ship("s1"), Ship("s2"), 2),
            new TemplateCopy(Good(WeaponGoodsPath, "s1_hull_b"), Good(WeaponGoodsPath, "s2_hull"), 0),
            new TemplateCopy(Good(WeaponGoodsPath, "s1_package_b"), Good(WeaponGoodsPath, "s2_package"), 0),
        ], plan.Copies);
        Assert.Equal(["s2"], Firsts(session, Good(WeaponGoodsPath, "s2_hull"), "ship"));
        Assert.Equal(["2000"], Firsts(session, Good(WeaponGoodsPath, "s2_hull"), "price"));
        Assert.Equal(["s2_hull"], Firsts(session, Good(WeaponGoodsPath, "s2_package"), "hull"));
        // An addon names equipment that the plan does not copy, so it stays.
        Assert.Equal(["gun1"], Firsts(session, Good(WeaponGoodsPath, "s2_package"), "addon"));
        Assert.Equal([Good(WeaponGoodsPath, "s2_hull")], session.Related(Ship("s2"), ItemRelation.Hull));
        Assert.Equal([Good(WeaponGoodsPath, "s2_package")], session.Related(Ship("s2"), ItemRelation.Package));
        var wing = new SectionRef(ShipsPath, new SectionIdentity("CollisionGroup", null, new SectionIdentity("Ship", "s2")));
        Assert.Equal(["wing"], Firsts(session, wing, "obj"));
        Assert.Single(session.Get(wing)!.Lines("separable"));
    }

    [Fact]
    public void APlanWithAProblemAddsNothing()
    {
        using var ws = Create();
        var session = ws.Session("act");

        // Each copy needs a nickname that no section of its kind of file has, also in another file of the kind.
        Assert.Equal(
        [
            "another section of the equipment files has the nickname gun1_ammo.",
            "another section of the equipment files has the nickname gun1.",
            "another section of the goods files has the nickname gun1.",
        ], Messages(ItemTemplates.Plan(session, Gun("gun1"), "gun1")));
        Assert.Equal(
        [
            "another section of the equipment files has the nickname eng1.",
            "another section of the goods files has the nickname eng1.",
        ], Messages(ItemTemplates.Plan(session, Gun("gun1"), "eng1")));
        var bad = ItemTemplates.Plan(session, Gun("gun1"), "bad,name");
        Assert.NotEmpty(bad.Problems);
        Assert.Empty(bad.Copies);
        Assert.Equal(Gun("gun1"), bad.Problems[0].Target);
        Assert.Throws<InvalidOperationException>(() => ItemTemplates.Build(bad));
        // A template must be a ship or equipment.
        Assert.NotEmpty(ItemTemplates.Plan(session, Good(GoodsPath, "s1_hull"), "x").Problems);
        Assert.NotEmpty(ItemTemplates.Plan(session, Ref(ShipsPath, "Simple", "s1_debris"), "x").Problems);
        Assert.NotEmpty(ItemTemplates.Plan(session, Munition("nothing"), "x").Problems);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A copy of an item of the active mod gets its own copy of each text of the template, with a key from its own
    /// nickname. So a text edit of the copy does not change the template. Copies of sections that share a text share
    /// the copy.
    /// </summary>
    [Fact]
    public void ACopyOfAnItemOfTheModGetsItsOwnText()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));
        session.Execute(ItemEdits.SetText(session, Gun("gun2"), "ids_name", "Gun Two",
            ItemEdits.SharedText(session, Gun("gun2"), "ids_name")));

        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun2"), "gun3")));

        Assert.Equal(["$str:gun3"], Firsts(session, Gun("gun3"), "ids_name"));
        Assert.Equal(["$str:gun3"], Firsts(session, Good(WeaponGoodsPath, "gun3"), "ids_name"));
        Assert.Equal("Gun Two", session.Text(new StringPlaceholder(StringKind.String, "gun3")));
        session.Execute(ItemEdits.SetText(session, Gun("gun3"), "ids_name", "Gun Three",
            ItemEdits.SharedText(session, Gun("gun3"), "ids_name")));
        Assert.Equal(["$str:gun3"], Firsts(session, Good(WeaponGoodsPath, "gun3"), "ids_name"));
        Assert.Equal("Gun Two", session.Text(new StringPlaceholder(StringKind.String, "gun2")));
        Assert.Equal("Gun Three", session.Text(new StringPlaceholder(StringKind.String, "gun3")));
        // One undo takes back the copy and its text.
        session.Undo();
        session.Undo();
        Assert.Null(session.Text(new StringPlaceholder(StringKind.String, "gun3")));
    }

    /// <summary>
    /// The merge gives a text of a lower mod an id from the position of the text in the generated text DLL. A copy
    /// that kept the id would show other text when the lower mod adds text (rule S8). So the copy gets the text in the
    /// active mod, with a placeholder.
    /// </summary>
    [Fact]
    public void ACopyOfAnItemOfALowerModCopiesItsTextIntoTheActiveMod()
    {
        using var ws = Create();
        ws.Mod("low", new()
        {
            [@"patches\" + WeaponsPath] = "[Gun]\nnickname = lowgun\nol.new = true\nids_name = $str:lowgun_name\n" +
                                          "ids_info = $ifc:lowgun_info\nprojectile_archetype = gun1_ammo",
            [@"patches\" + WeaponGoodsPath] = "[Good]\nnickname = lowgun\nol.new = true\nequipment = lowgun\n" +
                                              "ids_name = $str:lowgun_name",
            ["strings.json"] = """{ "strings": { "lowgun_name": "Low Gun" }, "infocards": { "lowgun_info": "<RDL/>" } }""",
        });
        ws.WriteProfile("low", "act");
        var session = ws.Session("act");
        Assert.Equal([$"{session.EditView.StringIds.DllIndex * 65536 + 1}"], Firsts(session, Gun("lowgun"), "ids_name"));

        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("lowgun"), "actgun")));

        Assert.Equal(["$str:actgun"], Firsts(session, Gun("actgun"), "ids_name"));
        Assert.Equal(["$str:actgun"], Firsts(session, Good(WeaponGoodsPath, "actgun"), "ids_name"));
        Assert.Equal(["$ifc:actgun.info"], Firsts(session, Gun("actgun"), "ids_info"));
        Assert.Equal("Low Gun", session.Text(new StringPlaceholder(StringKind.String, "actgun")));
        Assert.Equal("<RDL/>", session.Text(new StringPlaceholder(StringKind.Infocard, "actgun.info")));
        var result = session.Save();
        Assert.True(result.Saved, string.Join("\n", result.Problems));

        // The lower mod adds a text whose key sorts first. The copy still shows its own text.
        File.WriteAllText(Path.Combine(ws.ModFolder("low"), "strings.json"),
            """{ "strings": { "a_motto": "Buy American", "lowgun_name": "Low Gun" }, "infocards": { "lowgun_info": "<RDL/>" } }""");
        var overlay = ws.Build(Mod.Load(ws.ModFolder("low")), Mod.Load(ws.ModFolder("act")));
        var actgun = TestWorkspace.Sections(overlay, WeaponsPath)
            .Single(s => s.Name == "Gun" && s["nickname"]![0].ToString() == "actgun");
        var id = actgun["ids_name"]![0].ToInt32();
        Assert.Equal("Low Gun", overlay.StringIds.Strings[id - overlay.StringIds.DllIndex * 65536]);
    }

    [Fact]
    public void APlanReportsAnIdOfTheTextOfTheModsWithoutText()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var id = session.EditView.StringIds.DllIndex * 65536 + 50;
        session.Execute(EditCommand.SetLines(Gun("gun1"), "ids_name", [[new Int32Value(id)]]));

        var plan = ItemTemplates.Plan(session, Gun("gun1"), "gun2");

        Assert.Equal([$"the 'ids_name' line of [Gun] holds {id}, which is in the id range of the text of the mods, but " +
                      "no mod has a text with this id."], Messages(plan));
        Assert.Empty(plan.Copies);
    }

    /// <summary>A plan holds the sections as the edit view had them. So it does not build, and its edit does not
    /// run, after the edit view changed.</summary>
    [Fact]
    public void APlanDoesNotRunAfterTheEditViewChanged()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var plan = ItemTemplates.Plan(session, Gun("gun1"), "gun2");
        var built = ItemTemplates.Build(plan);

        session.Execute(EditCommand.AddSection(WeaponsPath, "Gun", [Line("nickname", new StringValue("gun2"))]));

        Assert.Throws<InvalidOperationException>(() => ItemTemplates.Build(plan));
        Assert.Throws<InvalidOperationException>(() => session.Execute(built));
        Assert.Single(session.Items.Named(ItemFileKind.Equipment, "gun2"));
        Assert.Equal("Add [Gun]", session.UndoLabel);
    }

    /// <summary>The messages of the problems of a plan, without the template in front.</summary>
    private static string[] Messages(TemplatePlan plan) =>
        plan.Problems.Select(p => p.Message[(p.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..]).ToArray();

    [Fact]
    public void ACopyInABinaryFileWritesItsBoolStringsInLowercaseAndSaves()
    {
        using var ws = Create();
        var gun = new Section("Gun");
        gun.Add(new Entry(gun, "nickname") { new StringValue("gun1") });
        gun.Add(new Entry(gun, "lootable") { new StringValue("True") });
        gun.Add(new Entry(gun, "hit_pts") { new Int32Value(400) });
        ws.WriteBaseBini(WeaponsPath, gun);
        var session = ws.Session("act");

        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Equal(["true"], Firsts(session, Gun("gun2"), "lootable"));
        Assert.Equal(["True"], Firsts(session, Gun("gun1"), "lootable"));
        Assert.Equal("[Gun]\r\nol.new = true\r\nnickname = gun2\r\nlootable = true\r\nhit_pts = 400\r\n",
            File.ReadAllText(Path.Combine(ws.ModFolder("act"), "patches", WeaponsPath)));
        session.Dispose();
        Assert.Empty(ws.Session("act").Check(Gun("gun2")));
    }
}

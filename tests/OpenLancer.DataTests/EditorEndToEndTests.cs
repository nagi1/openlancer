using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Proof 3 of Milestone 1: the public edit API makes a new gun and a new ship from retail items, sells them at
/// Manhattan and saves them into a new mod. The engine loads the mod from a deployed folder, and a new story character
/// can buy the ship.
/// </summary>
/// <remarks>
/// <para>Each test makes a temporary profile of the retail data and adds a new mod to it, as the editor does. Then it
/// runs the same edits (see <see cref="Edit"/>). The deploy test also runs over a copy of the sample mod, as the
/// manual check of the review does (section 5.6).</para>
/// <para>The gun copies <c>[Munition] li_gun01_mark01_ammo</c> and <c>[Gun] li_gun01_mark01</c> of the binary file
/// weapon_equip.ini (sections 546 and 547, counted from 0), and <c>[Good] li_gun01_mark01</c> of weapon_good.ini. Retail
/// market_misc.ini sells li_gun01_mark01 at Manhattan. The ship copies <c>[Ship] li_elite</c> with its three
/// <c>[CollisionGroup]</c> sections, its hull good le_hull (price 10,400) and its package le_package.</para>
/// </remarks>
public sealed class EditorEndToEndTests(ITestOutputHelper output)
{
    private const string ModId = "proof";
    private const string ModName = "Proof";
    private const string SampleId = "ol-sample";
    private const string GunNickname = "proof_gun01";
    private const string ShipNickname = "proof_elite";
    private const string GunName = "Proof gun";
    private const string ShipName = "Proof Elite";
    private const string WeaponGood = @"patches\DATA\Equipment\weapon_good.ini";

    /// <summary>The hull price of the new ship. A new story character can pay it (retail le_hull: 10,400).</summary>
    private const int HullPrice = 1_200;

    /// <summary>The hull damage of the new munition (retail li_gun01_mark01_ammo: 16.3).</summary>
    private const float HullDamage = 20;

    /// <summary>
    /// The files that the edits save, without a lower mod. A new patch file has the letter case of the base file
    /// (rule P8).
    /// </summary>
    private static readonly string[] SavedFiles =
    [
        @"patches\DATA\Equipment\Goods.ini",
        @"patches\DATA\Equipment\market_misc.ini",
        @"patches\DATA\Equipment\market_ships.ini",
        @"patches\DATA\Equipment\weapon_equip.ini",
        WeaponGood,
        @"patches\DATA\Ships\shiparch.ini",
        "strings.json",
    ];

    /// <summary>
    /// The files that the edits save over the sample mod. A new patch file has the letter case of the file in the
    /// sample mod, when it has the file (rule P8).
    /// </summary>
    private static readonly string[] SavedFilesOverTheSample =
    [
        @"patches\DATA\EQUIPMENT\goods.ini",
        @"patches\DATA\EQUIPMENT\market_ships.ini",
        @"patches\DATA\Equipment\market_misc.ini",
        @"patches\DATA\Equipment\weapon_equip.ini",
        WeaponGood,
        @"patches\DATA\SHIPS\shiparch.ini",
        "strings.json",
    ];

    /// <summary>
    /// The SHA-256 of each file of the mod after the edits, without a lower mod. So output that changes between two
    /// processes or two builds fails <see cref="AReplayOfTheEditsGivesTheSameFiles"/>. When a change of the writer
    /// changes the files on purpose, that test prints the text and the new SHA-256 of each file.
    /// </summary>
    private static readonly SortedDictionary<string, string> SavedHashes = new(StringComparer.Ordinal)
    {
        ["mod.json"] = "e45eccd148073c0e5d8e24e6bd1b0ae2cf270e5cac46568cb5b3a7bfb3394f91",
        [@"patches\DATA\Equipment\Goods.ini"] = "1119c441220d28459ce54631cb366df8f4a50fe318dcb1910ee135a0b06fc859",
        [@"patches\DATA\Equipment\market_misc.ini"] = "15947051489c948a88a17174931eb7d21d584d9ac2eb7513aca66bc5a322856b",
        [@"patches\DATA\Equipment\market_ships.ini"] = "62538c8df22024131f5fbfd1c529bcbfcf0dc9eae31eb2428741a53bd3710e98",
        [@"patches\DATA\Equipment\weapon_equip.ini"] = "bb11b330f6948b8603ad3e5d0630e0b6dd96ecda01d0c60943bb9c154ff603f1",
        [WeaponGood] = "fb8481bb3164b4c72ad2018d3c944467012fab1c4cf8c21047036a6d74506668",
        [@"patches\DATA\Ships\shiparch.ini"] = "e62a27501871fd1d580fe62a5868d476193d5691bb26cb2c6b843b9bcacd86f9",
        ["strings.json"] = "f79f5fe4df938639e3ba0075f8ad719dc71b721d4431e50e8172c163f6b6e238",
    };

    static EditorEndToEndTests() => AppHandler.ConsoleInit(silent: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEngineLoadsTheNewItemsFromADeployOfTheSavedMod(bool overTheSample)
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, ModId, ModName,
            overTheSample ? [GameDataPaths.SampleMod(SampleId)] : null);
        var session = workspace.Open();
        // The sample mod sets the price of lf_hull (retail 6,600). The edit view shows that price and its origin, because
        // B is the document that the merge of the lower mods gives (decision D2, review finding F4).
        var lfHull = session.Find(ItemFileKind.Goods, "lf_hull")!;
        (ValueOrigin, string?) origin = overTheSample ? (ValueOrigin.LowerMod, SampleId) : (ValueOrigin.Base, null);
        Assert.Equal(origin, session.Origin(lfHull, "price"));
        Assert.Equal(overTheSample ? 5600 : 6600, Assert.Single(session.Get(lfHull)!.Lines("price")).Values[0].ToInt32());
        Edit(session);
        var outside = Files(workspace.Root, workspace.ModFolder);

        var result = session.Save();

        AssertSaved(result);
        Assert.False(session.IsDirty);
        var saved = overTheSample ? SavedFilesOverTheSample : SavedFiles;
        Assert.Equal(saved.Order(StringComparer.Ordinal), Relative(workspace.ModFolder, result.Written));
        // A folder has the letter case of the first file that the save puts into it.
        Assert.Equal(saved.Append(ModManifest.FileName).Order(StringComparer.OrdinalIgnoreCase),
            Files(workspace.ModFolder).Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(outside, Files(workspace.Root, workspace.ModFolder));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
        foreach (var file in result.Written.Order(StringComparer.Ordinal))
            output.WriteLine($"--- {Path.GetRelativePath(workspace.ModFolder, file)}\n{File.ReadAllText(file)}");

        // The deploy writes the folder that lancer.exe reads: <profile folder>\retail.
        var profile = ModProfile.Load(workspace.ProfilePath);
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        Assert.Empty(overlay.Diagnostics);
        var deploy = ProfileDeployer.Deploy(overlay, profile);
        var fs = FileSystem.FromPath(deploy.Folder);
        var problems = ProfileValidator.NewProblems(FileSystem.FromPath(baseFolder), fs, overlay.CutscenePaths);
        foreach (var problem in problems)
            output.WriteLine(problem.ToString());
        Assert.DoesNotContain(problems, p => p.Severity == DiagnosticSeverity.Error);
        var db = EngineDataLoader.Load(fs).RequireComplete();

        var gun = Assert.IsType<GunEquipment>(db.Equipment.Get(GunNickname));
        Assert.Equal(GunNickname + "_ammo", gun.Munition.Nickname);
        Assert.Equal(HullDamage, gun.Munition.Def.HullDamage);
        // The engine finds the equipment of a good by the nickname of the good. The equipment line of the good names
        // the new gun too, so that other tools find it (review finding F12).
        Assert.Same(gun, db.Goods.Get(GunNickname)!.Equipment);
        Assert.Equal(GunNickname, gun.Good!.Ini.Equipment);
        var ship = db.Ships.Get(ShipNickname)!;
        Assert.Equal(HullPrice, db.GetShipPrice(ship));
        var package = db.GetShipPackage(FLHash.CreateID(ShipNickname + "_package"))!;
        Assert.Same(ship, package.Ship);
        Assert.Contains(package.Addons, a => a.Equipment == gun && a.Hardpoint == "HpWeapon01");
        Assert.Equal(overTheSample ? 5600 : 6600, db.GetShipPrice(db.Ships.Get("li_fighter")!));
        AssertCopiesAreComplete(fs, gun.IdsName, ship.IdsName);

        // The Inspector shows the name of the gun, and a shop shows the name of its good. The text ids of the proof
        // follow the text ids of the sample mod in the generated DLL (rule S8).
        var text = new InfocardManager(db.Ini.Freelancer.Resources);
        Assert.Equal(ShipName, text.GetStringResource(ship.IdsName));
        Assert.Equal(GunName, text.GetStringResource(gun.IdsName));
        Assert.Equal(gun.IdsName, gun.Good!.Ini.IdsName);
        if (overTheSample)
            Assert.Equal("Starrunner", text.GetStringResource(db.Ships.Get("ol_starrunner")!.IdsName));

        var manhattan = db.Bases.Get(RetailChecks.Manhattan)!;
        Assert.Contains(manhattan.SoldShips, s => s.Package == package);
        Assert.Contains(manhattan.SoldGoods, g => g.Good.Equipment == gun && g.ForSale);
        Assert.Empty(PackageCheck.Check(db, [package.Nickname], fs));
        RetailChecks.AssertNewPlayerCanBuy(db, fs, package.Nickname, output);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>
    /// The same edits in a second new mod give the same files, byte for byte. A second session without edits would
    /// prove nothing, because a save without edits writes nothing. The files must also have the SHA-256 values of
    /// <see cref="SavedHashes"/>.
    /// </summary>
    [Fact]
    public void AReplayOfTheEditsGivesTheSameFiles()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var first = new RetailWorkspace(baseFolder, ModId, ModName);
        using var second = new RetailWorkspace(baseFolder, ModId, ModName);

        foreach (var workspace in new[] { first, second })
        {
            var session = workspace.Open();
            Edit(session);
            AssertSaved(session.Save());
        }

        var files = Files(first.ModFolder);
        Assert.Equal(files, Files(second.ModFolder));
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, hex) in files)
        {
            var bytes = Convert.FromHexString(hex);
            hashes[path] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (SavedHashes.GetValueOrDefault(path) != hashes[path])
                output.WriteLine($"--- {path}: {hashes[path]}\n{Encoding.UTF8.GetString(bytes)}");
        }
        Assert.Equal(SavedHashes, hashes);
    }

    /// <summary>
    /// A file whose patch fails the self-check of the writer (rule E6) stops the whole save: no file of the mod
    /// changes, and the edits stay unsaved (review finding F1).
    /// </summary>
    /// <remarks>
    /// <para>The engine reads the lines of an <c>[ObjList]</c> section of a mission in order. In
    /// <c>[ObjList] nickname = avoid</c> of m02.ini, the <c>system</c> line comes before the key line. A change of such a
    /// section writes all lines except the key line, and the merge puts them as one block at the place of the first line
    /// that they replace. So no patch can change the section and keep its order, and the editor takes no edit of it.</para>
    /// <para>The mod has a hand-written patch that changes the section, and the merge replaces that line in place. When
    /// an edit changes m02.ini, the writer writes the line of the hand-written patch again (a pinned line, review
    /// finding F3), and the self-check refuses the patch. So the test needs no test seam. When the writer can write
    /// such a section, the test needs another trigger.</para>
    /// </remarks>
    [Fact]
    public void AFileThatFailsTheSelfCheckStopsTheWholeSave()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, ModId, ModName);
        var objList = new SectionRef(@"DATA\MISSIONS\M02\m02.ini", new SectionIdentity("ObjList", "avoid"));
        var handWritten = Path.Combine(workspace.ModFolder, Mod.PatchesFolder, objList.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(handWritten)!);
        File.WriteAllText(handWritten, "[ObjList]\r\nnickname = avoid\r\nAvoidance = false\r\n");
        var session = workspace.Open();
        Assert.Equal(["system", "nickname", "Avoidance"], session.Get(objList)!.Entries.Select(e => e.Name));
        Assert.EndsWith("a patch keeps that order only when the 'nickname' line is the first line.",
            session.Get(objList)!.ReadOnlyReason);
        Assert.Throws<InvalidOperationException>(() =>
            session.Execute(EditCommand.SetLines(objList, "Avoidance", [[new StringValue("true")]])));
        var items = Edit(session);
        AssertSaved(session.Save());
        var saved = Files(workspace.ModFolder);

        session.Execute(ItemEdits.SetText(session, items.Gun, "ids_name", GunName + " Mk II",
            ItemEdits.SharedText(session, items.Gun, "ids_name")));
        session.Execute(EditCommand.SetLines(items.GunGood, "price", [[new Int32Value(600)]]));
        session.Execute(EditCommand.AddSection(objList.Path, "ObjList",
        [
            new("nickname", [new StringValue("proof_avoid")]),
            new("system", [new StringValue("Li01")]),
            new("Avoidance", [new StringValue("true")]),
        ]));
        var result = session.Save();

        Assert.False(result.Saved);
        var problem = Assert.Single(result.Problems);
        output.WriteLine(problem.ToString());
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal(objList, problem.Target);
        Assert.Contains("the patch does not give the edited section", problem.Message);
        Assert.Equal(saved, Files(workspace.ModFolder));
        Assert.True(session.IsDirty);

        // Without the edit of the mission, the other edits save. The hand-written patch keeps its bytes (rule P2).
        session.Undo();
        var next = session.Save();
        AssertSaved(next);
        Assert.False(session.IsDirty);
        Assert.Equal([WeaponGood, Mod.StringsFile], Relative(workspace.ModFolder, next.Written));
        // The second save starts from the files that the first save wrote (rules P10 and F14), so only the new
        // values change.
        var files = Files(workspace.ModFolder);
        Assert.Equal(saved.Keys, files.Keys);
        foreach (var path in saved.Keys.Except([WeaponGood, Mod.StringsFile]))
            Assert.Equal(saved[path], files[path]);
        var price = Changed(TextLines(saved[WeaponGood]), TextLines(files[WeaponGood]));
        Assert.StartsWith("price = ", price.Before);
        Assert.Equal("price = 600", price.After);
        var name = Changed(Strings(saved[Mod.StringsFile]), Strings(files[Mod.StringsFile]));
        Assert.Equal((name.Before.Key, GunName), name.Before);
        Assert.Equal((name.Before.Key, GunName + " Mk II"), name.After);
    }

    [Fact]
    public void AnUndoOfAllEditsAndASaveLeaveOnlyTheManifest()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, ModId, ModName);
        var manifest = Files(workspace.ModFolder);
        var session = workspace.Open();
        Edit(session);
        AssertSaved(session.Save());

        while (session.CanUndo)
            session.Undo();
        var result = session.Save();

        AssertSaved(result);
        Assert.Empty(result.Written);
        Assert.Equal(SavedFiles.Length, result.Deleted.Count);
        Assert.Equal(manifest, Files(workspace.ModFolder));
        // The save deletes the empty folders below the patches folder.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(workspace.ModFolder, Mod.PatchesFolder)));
        Assert.False(session.IsDirty);
    }

    /// <summary>
    /// The edits of the proof, as a user makes them in the editor: a new gun and a new ship from templates, their names,
    /// a new munition damage, a new hull price, the new gun in the package of the new ship, and both items for sale at
    /// Manhattan.
    /// </summary>
    private static ProofItems Edit(EditSession session)
    {
        // A plan holds the edit view as it is when the plan is made, so each plan runs before the next edit.
        var gunPlan = ItemTemplates.Plan(session, session.Find(ItemFileKind.Equipment, "li_gun01_mark01")!, GunNickname);
        Assert.Empty(gunPlan.Problems);
        session.Execute(ItemTemplates.Build(gunPlan));
        Assert.Equal(["Munition proof_gun01_ammo", "Gun proof_gun01", "Good proof_gun01"],
            gunPlan.Copies.Select(c => $"{c.Copy.Id.Type} {c.Copy.Id.Key}"));
        var gun = session.Find(ItemFileKind.Equipment, GunNickname)!;
        var gunGood = session.Find(ItemFileKind.Goods, GunNickname)!;
        var ammo = Assert.Single(session.Related(gun, ItemRelation.Munition));
        session.Execute(EditCommand.SetLines(ammo, "hull_damage", [[new SingleValue(HullDamage, null)]]));
        // The gun and its good have the same retail name. Both get the new name.
        session.Execute(ItemEdits.SetText(session, gun, "ids_name", GunName, ItemEdits.SharedText(session, gun, "ids_name")));

        var shipPlan = ItemTemplates.Plan(session, session.Find(ItemFileKind.Ships, "li_elite")!, ShipNickname);
        Assert.Empty(shipPlan.Problems);
        session.Execute(ItemTemplates.Build(shipPlan));
        Assert.Equal(["Ship proof_elite", "Good proof_elite_hull", "Good proof_elite_package"],
            shipPlan.Copies.Select(c => $"{c.Copy.Id.Type} {c.Copy.Id.Key}"));
        Assert.Equal(3, shipPlan.Copies[0].Children);
        var ship = session.Find(ItemFileKind.Ships, ShipNickname)!;
        var hull = Assert.Single(session.Related(ship, ItemRelation.Hull));
        var package = Assert.Single(session.Related(ship, ItemRelation.Package));
        session.Execute(ItemEdits.SetText(session, ship, "ids_name", ShipName, [hull]));
        session.Execute(EditCommand.SetLines(hull, "price", [[new Int32Value(HullPrice)]]));
        // The li_elite takes a gun of type hp_gun_special_1 on HpWeapon01.
        var addons = session.Get(package)!.Lines("addon").Select(l => l.Values)
            .Append(NewAddon().Values)
            .ToList();
        session.Execute(EditCommand.SetLines(package, "addon", addons));

        // Format version 1 cannot change a retail market line, so each item gets a new [BaseGood] for Manhattan.
        var retailGunLine = session.MarketLines("li_gun01_mark01")
            .Single(l => l.Base.Equals(RetailChecks.Manhattan, StringComparison.OrdinalIgnoreCase));
        session.Execute(EditCommand.AddSection(retailGunLine.Section.Path, "BaseGood",
            Market([new StringValue(GunNickname), .. retailGunLine.Values.Skip(1)])));
        var retailShipLine = session.MarketLines("le_package")
            .Single(l => l.Base.Equals(RetailChecks.Manhattan, StringComparison.OrdinalIgnoreCase));
        // A ship is for sale when the minimum or the maximum count is not 0: rank 0, any reputation, 1 in stock.
        session.Execute(EditCommand.AddSection(retailShipLine.Section.Path, "BaseGood",
            Market([new StringValue(package.Id.Key!), .. new[] { 0, -1, 1, 1, 0, 1, 1 }.Select(v => new Int32Value(v))])));

        Assert.True(session.IsDirty);
        return new ProofItems(gun, gunGood);
    }

    /// <summary>The line that mounts the new gun in the package of the new ship.</summary>
    private static EntryView NewAddon() =>
        Line("addon", new StringValue(GunNickname), new StringValue("HpWeapon01"), new Int32Value(1));

    /// <summary>The lines of a new <c>[BaseGood]</c> that sells one good at Manhattan.</summary>
    private static List<EntryView> Market(IReadOnlyList<ValueBase> marketGood) =>
        [Line("base", new StringValue(RetailChecks.Manhattan)), new("marketgood", marketGood)];

    private static EntryView Line(string name, params ValueBase[] values) => new(name, values);

    /// <summary>
    /// Checks that each copy of the deployed data has all lines of its template, the child sections included (decision
    /// D1). Only the key, the links to the other copies, the names, the damage, the price and the new addon differ.
    /// </summary>
    /// <param name="fs">The deployed data. It has the templates and the copies.</param>
    /// <param name="gunName">The text id of the name of the new gun.</param>
    /// <param name="shipName">The text id of the name of the new ship.</param>
    private static void AssertCopiesAreComplete(FileSystem fs, int gunName, int shipName)
    {
        var weapons = Deployed(fs, "DATA/EQUIPMENT/weapon_equip.ini");
        AssertCopy(weapons, "Munition", "li_gun01_mark01_ammo", GunNickname + "_ammo",
            Line("hull_damage", new SingleValue(HullDamage, null)));
        AssertCopy(weapons, "Gun", "li_gun01_mark01", GunNickname,
            Line("ids_name", new Int32Value(gunName)), Line("projectile_archetype", new StringValue(GunNickname + "_ammo")));
        AssertCopy(Deployed(fs, "DATA/EQUIPMENT/weapon_good.ini"), "Good", "li_gun01_mark01", GunNickname,
            Line("equipment", new StringValue(GunNickname)), Line("ids_name", new Int32Value(gunName)));
        AssertCopy(Deployed(fs, "DATA/SHIPS/shiparch.ini"), "Ship", "li_elite", ShipNickname,
            Line("ids_name", new Int32Value(shipName)));
        var goods = Deployed(fs, "DATA/EQUIPMENT/goods.ini");
        AssertCopy(goods, "Good", "le_hull", ShipNickname + "_hull", Line("ship", new StringValue(ShipNickname)),
            Line("price", new Int32Value(HullPrice)), Line("ids_name", new Int32Value(shipName)));
        var addons = Run(goods, "Good", "le_package")[0].Where(e => e.Name == "addon").Select(View);
        AssertCopy(goods, "Good", "le_package", ShipNickname + "_package",
            [Line("hull", new StringValue(ShipNickname + "_hull")), .. addons, NewAddon()]);
    }

    private static IniDocument Deployed(FileSystem fs, string path) => IniDocument.Parse(path, fs.ReadAllBytes(path));

    /// <summary>
    /// Checks that a copy and its child sections have the lines of the template and its child sections: the same
    /// names in the same order, and values of the same type that the engine reads the same. The copy has the new
    /// nickname. The lines of each name in <paramref name="changed"/> take the place of the first line of the name in
    /// the template, as an edit puts them.
    /// </summary>
    private static void AssertCopy(IniDocument file, string type, string template, string copy, params EntryView[] changed)
    {
        var source = Run(file, type, template);
        var result = Run(file, type, copy);
        Assert.Equal(source.Select(s => s.Name), result.Select(s => s.Name));
        for (var k = 0; k < source.Count; k++)
        {
            var expected = k == 0
                ? Replaced(source[0], [Line("nickname", new StringValue(copy)), .. changed])
                : source[k].Select(View).ToList();
            var actual = result[k].Select(View).ToList();
            var same = expected.Count == actual.Count &&
                       expected.Zip(actual).All(p => SameLine(p.First, p.Second, file.IsBinary));
            var label = k == 0 ? $"[{type}] {copy}" : $"[{result[k].Name}] #{k} of [{type}] {copy}";
            Assert.True(same, $"{label} in {file.Path} differs from its template.\n" +
                              $"Expected:\n{Describe(expected)}\nActual:\n{Describe(actual)}");
        }
    }

    /// <summary>The section of a type with a nickname, and its child sections.</summary>
    private static List<Section> Run(IniDocument file, string type, string nickname)
    {
        var i = Assert.Single(Enumerable.Range(0, file.Sections.Count), j =>
            file.Sections[j].Name.Equals(type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SectionIdentity.KeyOf(file.Sections[j]), nickname, StringComparison.OrdinalIgnoreCase));
        return file.Sections.GetRange(i, 1 + SectionKeys.ChildCount(file.Sections, i));
    }

    /// <summary>The lines of a section in which the lines of each changed name take the place of its first line.</summary>
    private static List<EntryView> Replaced(Section section, IReadOnlyList<EntryView> changed)
    {
        var byName = changed.ToLookup(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<EntryView>();
        foreach (var entry in section)
        {
            if (!byName.Contains(entry.Name))
                result.Add(View(entry));
            else if (placed.Add(entry.Name))
                result.AddRange(byName[entry.Name].Select(l => l with { Name = entry.Name }));
        }
        Assert.Equal(byName.Count, placed.Count);
        return result;
    }

    private static bool SameLine(EntryView a, EntryView b, bool binary) =>
        a.Name == b.Name && a.Values.Count == b.Values.Count && a.Values.Zip(b.Values).All(v =>
            v.First.GetType() == v.Second.GetType() && IniValueComparer.EngineEqual(v.First, v.Second, binary));

    private static string Describe(IEnumerable<EntryView> lines) => string.Join("\n",
        lines.Select(l => $"  {l.Name} = {string.Join(", ", l.Values.Select(v => $"{v.GetType().Name} {v}"))}"));

    private static EntryView View(Entry entry) => new(entry.Name, entry.ToList());

    private static void AssertSaved(SaveResult result)
    {
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Empty(result.Problems);
    }

    /// <summary>The paths relative to a folder, in ordinal order.</summary>
    private static IEnumerable<string> Relative(string folder, IEnumerable<string> paths) =>
        paths.Select(f => Path.GetRelativePath(folder, f)).Order(StringComparer.Ordinal);

    /// <summary>The one pair of values that differ between two lists of the same length.</summary>
    private static (T Before, T After) Changed<T>(IReadOnlyList<T> before, IReadOnlyList<T> after)
    {
        Assert.Equal(before.Count, after.Count);
        return Assert.Single(before.Zip(after), p => !EqualityComparer<T>.Default.Equals(p.First, p.Second));
    }

    /// <summary>The lines of a patch file, from its content as hex.</summary>
    private static string[] TextLines(string hex) => Encoding.ASCII.GetString(Convert.FromHexString(hex)).Split("\r\n");

    /// <summary>The keys and texts of the <c>strings</c> object of strings.json, in key order. The file content is in
    /// hex.</summary>
    private static (string Key, string? Text)[] Strings(string hex)
    {
        using var json = JsonDocument.Parse(Convert.FromHexString(hex));
        return json.RootElement.GetProperty("strings").EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => (p.Name, p.Value.GetString())).ToArray();
    }

    /// <summary>
    /// The files below a folder: the relative path to the content as hex. The lock files of an open session in the
    /// <c>.olsave</c> folder of a mod are not content of the mod, so the result leaves them out.
    /// </summary>
    /// <param name="exclude">A folder whose files the result leaves out, or null.</param>
    private static SortedDictionary<string, string> Files(string folder, string? exclude = null)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (exclude != null && !Path.GetRelativePath(exclude, file).StartsWith("..", StringComparison.Ordinal))
                continue;
            if (Path.GetFileName(Path.GetDirectoryName(file)) == SaveTransaction.FolderName &&
                Path.GetFileName(file) is ModLock.FileName or ModLock.InfoName)
                continue;
            result[Path.GetRelativePath(folder, file)] = Convert.ToHexString(File.ReadAllBytes(file));
        }
        return result;
    }

    /// <param name="Gun">The new <c>[Gun]</c>.</param>
    /// <param name="GunGood">The good of the new gun.</param>
    private sealed record ProofItems(SectionRef Gun, SectionRef GunGood);
}

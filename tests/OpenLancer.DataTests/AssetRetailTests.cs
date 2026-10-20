using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.World.Components;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>The retail proofs of the rows and the models of the Assets panel (section 14.2 of the M23 design).</summary>
public sealed class AssetRetailTests(ITestOutputHelper output)
{
    static AssetRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The six children of Solars hold each of the 321 retail solar archetypes once (decision D267, review
    /// finding R8). The rows of a category build within the budget of section 5.3.</summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void TheSolarCategoriesHoldEachRetailArchetypeOnce()
    {
        using var workspace = SceneRetail.Workspace("m23cats");
        var session = workspace.Open();

        // The budget is for the rows. The list of the solar archetypes builds at its first use, as the M21 budgets
        // measure it.
        Assert.NotNull(session.Lists.List("solar_archetype"));
        IReadOnlyList<AssetRow> rows = [];
        var build = Timing.Measure(() => rows = session.Assets.Rows("solars"));
        output.WriteLine($"Solars: {rows.Count} rows in {build.TotalMilliseconds:F1} ms");
        var children = session.Assets.Categories.Where(c => c.ParentId == "solars").ToList();
        foreach (var child in children)
            output.WriteLine($"{child.Id}: {child.Count}");

        Assert.Equal(["Station", "Planet", "Sun", "Satellite or buoy", "Other solar", "Jump gates, holes and lanes"],
            children.Select(c => c.Label));
        Assert.Equal([47, 55, 2, 38, 169, 10], children.Select(c => c.Count));
        Assert.Equal(321, rows.Count);
        Assert.Equal(321, rows.Select(r => r.Nickname.ToLowerInvariant()).Distinct().Count());
        Assert.All(rows.Where(r => r.CategoryId != "solars/connections"), r => Assert.Null(r.NotPlaceable));
        Assert.True(build.TotalMilliseconds < 20, $"The rows of Solars took {build.TotalMilliseconds:F1} ms.");

        // The search budget of section 5.4: one category, and All assets with each category.
        var watch = Stopwatch.StartNew();
        var found = session.Assets.Search("solars", "freeport", r => r.Nickname);
        var one = watch.Elapsed.TotalMilliseconds;
        var all = session.Assets.Rows(AssetCatalog.AllId).Count;
        watch.Restart();
        session.Assets.Search(AssetCatalog.AllId, "li_", r => r.Nickname);
        var everything = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"Search: {one:F2} ms in Solars, {everything:F2} ms in {all} rows of All assets");
        Assert.Contains(found, r => r.Nickname.Equals("space_freeport01", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(all, 5000, 7000);
    }

    /// <summary>Decision D293: the list of the models has the 511 <c>.cmp</c>, 1,341 <c>.3db</c> and 86 <c>.sph</c>
    /// files below the data folder, and each <c>DA_archetype</c> of a retail solar archetype is in it.</summary>
    [Fact]
    public void TheModelListHasTheRetailModels()
    {
        using var workspace = SceneRetail.Workspace("m23models");
        var session = workspace.Open();

        var models = session.Lists.List("model")!;
        int Count(string extension) => models.Entries.Count(e => e.Value.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"cmp {Count(".cmp")}, 3db {Count(".3db")}, sph {Count(".sph")}");

        Assert.Equal((511, 1341, 86), (Count(".cmp"), Count(".3db"), Count(".sph")));
        foreach (var entry in session.Lists.List("solar_archetype")!.Entries)
        {
            var source = ModelSource.Of(session, entry.Section!);
            Assert.True(source != null, entry.Value);
            Assert.True(models.Find(source.Model) != null, $"{entry.Value}: {source.Model}");
        }
        Assert.Equal(["BASES", "CHARACTERS", "COCKPITS", "EQUIPMENT", "FX", "INTERFACE", "SHIPS", "SOLAR"],
            session.Assets.Categories.Where(c => c.ParentId == "models").Select(c => c.Label.ToUpperInvariant()));
    }

    /// <summary>For each retail solar archetype, ship and asteroid, <see cref="ModelSource"/> gives the model and the
    /// library files that the loaded engine data has.</summary>
    [Fact]
    public void EachRetailModelSourceMatchesTheEngine()
    {
        using var workspace = SceneRetail.Workspace("m23engine");
        var session = workspace.Open();
        var items = RetailGame.Instance.Data.Items;
        var checkedCount = 0;

        void Check(string list, Func<string, ResolvedModel?> engine)
        {
            foreach (var entry in session.Lists.List(list)!.Entries)
            {
                var source = ModelSource.Of(session, entry.Section!);
                var model = engine(entry.Value);
                Assert.True(source != null && model != null, entry.Value);
                Assert.Equal(Normal(model.ModelFile), Normal(ThnReferences.PathOf(source.Model)));
                // The engine leaves out a library file that does not exist.
                Assert.Equal((model.LibraryFiles ?? []).Select(Normal),
                    source.Libraries.Where(session.Lists.FileExists).Select(l => Normal(ThnReferences.PathOf(l))));
                checkedCount++;
            }
        }

        Check("solar_archetype", n => items.Archetypes.Get(n)?.ModelFile);
        Check("ship", n => items.Ships.Get(n)?.ModelFile);
        Check("asteroid", n => items.Asteroids.Get(n)?.ModelFile);
        Assert.Equal(321 + 115 + 86, checkedCount);
    }

    private static string Normal(string? path) => (VfsPath.Normalize(path ?? "") ?? "").ToLowerInvariant();
}

/// <summary>The retail proofs of the ships as static props (decision D94 (a), section 8.5 of the M23 design).</summary>
public sealed class ShipPropRetailTests(ITestOutputHelper output)
{
    static ShipPropRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The proof of D94: 57 retail solar archetypes name a ship model, 220 objects of the 53 system files use
    /// them, and 82 of the 115 ships (45 of 64 models) have one (review finding R2).</summary>
    [Fact]
    public void TheRetailDataHasShipsAsStaticProps()
    {
        using var workspace = SceneRetail.Workspace("m23d94");
        var session = workspace.Open();

        var solars = session.Lists.List("solar_archetype")!.Entries
            .Where(e => ModelSource.Of(session, e.Section!)!.Model.Replace('/', '\\')
                .StartsWith(@"ships\", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var objects = session.DataFiles.Paths("system").SelectMany(p => session.Lists.List("object_of_system", p)!.Entries)
            .Count(o => SceneSections.Last(session.Get(o.Section!)!, "archetype") is { } a && solars.Contains(a));
        var ships = session.Lists.List("ship")!.Entries;
        var models = ships.GroupBy(s => ModelSource.Of(session, s.Section!)!.Key).ToList();
        var withProp = models.Where(m => PropArchetypes.Existing(session, ModelSource.Of(session, m.First().Section!)!).Count > 0)
            .ToList();
        output.WriteLine($"{solars.Count} archetypes, {objects} objects, {withProp.Sum(m => m.Count())} ships, " +
                         $"{withProp.Count} of {models.Count} models");

        Assert.Equal(57, solars.Count);
        Assert.Equal(220, objects);
        Assert.Equal(115, ships.Count);
        Assert.Equal((82, 45, 64), (withProp.Sum(m => m.Count()), withProp.Count, models.Count));
        var battleship = SceneRetail.Section(session, SceneRetail.Li01, "Object", "Li01_03");
        var archetype = SceneSections.Last(session.Get(battleship)!, "archetype")!;
        Assert.Contains(archetype, solars);
        Assert.Equal(SceneTemplate.Station, ScenePlans.TemplateFor(session, archetype));
        Assert.True(ScenePlans.AsksForBase(session, SceneTemplate.Station, archetype));
    }

    /// <summary>The model of <c>li_dreadnought</c> has four solar archetypes.</summary>
    [Fact]
    public void TheDreadnoughtModelOffersFourArchetypes()
    {
        using var workspace = SceneRetail.Workspace("m23dread");
        var session = workspace.Open();
        var ship = session.Lists.List("ship")!.Find("li_dreadnought")!;

        var existing = PropArchetypes.Existing(session, ModelSource.Of(session, ship.Section!)!);

        Assert.Equal(["l_dreadnought", "l_dreadnought_nodock", "MSN_d_l_dreadnought", "rm_l_dreadnought"],
            existing.Select(a => a.Nickname).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var fighter = session.Lists.List("ship")!.Find("li_fighter")!;
        Assert.Equal(["suprise_li_fighter"],
            PropArchetypes.Existing(session, ModelSource.Of(session, fighter.Section!)!).Select(a => a.Nickname));
    }

    /// <summary>In Li01, one new static prop for each of the 19 ship models without a solar archetype, from the first
    /// ship of the model. The engine loads the saved data, and each new object has the model of its ship.</summary>
    [Fact]
    public void EachShipModelWithoutAPropArchetypeGetsOneThatLoads()
    {
        using var workspace = SceneRetail.Workspace("m23props");
        var session = workspace.Open();
        var firsts = session.Lists.List("ship")!.Entries
            .GroupBy(s => ModelSource.Of(session, s.Section!)!.Key)
            .Select(g => g.First())
            .Where(s => PropArchetypes.Existing(session, ModelSource.Of(session, s.Section!)!).Count == 0)
            .ToList();
        Assert.Equal(19, firsts.Count);
        Assert.Contains(firsts, s => s.Value.Equals("br_gunboat", StringComparison.OrdinalIgnoreCase));

        var made = new Dictionary<string, string>();
        var x = -50_000;
        foreach (var ship in firsts)
        {
            var plan = ScenePlans.CreateProp(session, "Li01", ModelSource.Of(session, ship.Section!)!, ship.Value,
                new Vector3(x += 2000, 0, 80_000), null);
            Assert.Empty(plan.Refusals);
            session.Execute(plan.Command!);
            made[plan.SelectAfter!.Id.Key!] = ship.Value;
        }
        Assert.DoesNotContain(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);
        var db = SceneRetail.SaveAndLoad(session);

        var li01 = db.Systems.Get("li01")!;
        foreach (var (nickname, ship) in made)
        {
            var obj = li01.Objects.Single(o => o.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(db.Ships.Get(ship)!.ModelFile!.ModelFile, obj.Archetype!.ModelFile!.ModelFile, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(-1, obj.Archetype.Hitpoints);
        }
        Assert.Equal(19, made.Count);
    }
}

/// <summary>The retail proof of a spawn of a station with its base (the roadmap verify of M23).</summary>
public sealed class SpawnRetailTests
{
    static SpawnRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>A station archetype with a docking sphere, spawned in Bw05 with the first base of Bw05, loads and has a
    /// base dock.</summary>
    [Fact]
    public void AStationDroppedInBw05DocksWithItsBase()
    {
        using var workspace = SceneRetail.Workspace("m23spawn");
        var session = workspace.Open();
        var archetype = session.Assets.Rows("solars/station").First(r =>
            ScenePlans.AsksForBase(session, SceneTemplate.Station, r.Nickname)).Nickname;
        var baseName = ScenePlans.Bases(session, "Bw05")[0].Value;

        var plan = ScenePlans.Create(session, "Bw05", ScenePlans.TemplateFor(session, archetype)!.Value, archetype,
            new Vector3(12_345, 0, -6_789), null, baseName);
        session.Execute(plan.Command!);
        var db = SceneRetail.SaveAndLoad(session);

        var station = db.Systems.Get("bw05")!.Objects.Single(o => o.Nickname == plan.SelectAfter!.Id.Key);
        Assert.Equal(DockKinds.Base, station.Dock!.Kind);
        Assert.Equal(baseName, station.Dock.Target, ignoreCase: true);
        Assert.Equal(new Vector3(12_345, 0, -6_789), station.Position);
    }
}

/// <summary>The retail proofs of the prop groups (section 9 of the M23 design). The group files go into the mod of the
/// temporary workspace only (review finding R27).</summary>
public sealed class PropGroupRetailTests(ITestOutputHelper output)
{
    static PropGroupRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>Three transports of Li01, saved as a group and placed in Bw05, load with the same relative positions. The
    /// group leaves out their <c>parent</c> lines, because they name <c>Li01_14</c>, which is not a member.</summary>
    [Fact]
    public void ThreeTransportsOfLi01PlaceInBw05()
    {
        using var workspace = SceneRetail.Workspace("m23trio");
        var session = workspace.Open();
        var names = new[] { "Li01_14_large_transport_1", "Li01_large_transport_2", "Li01_large_transport_3" };
        var items = names.Select(n => SceneRetail.Section(session, SceneRetail.Li01, "Object", n)).ToList();

        var draft = PropGroups.Draft(session, items);
        foreach (var line in draft.LeftOut)
            output.WriteLine(line.Message);
        Assert.Empty(draft.Refusals);
        Assert.Equal(3, draft.LeftOut.Count(l => l.Key == "parent"));
        Assert.Equal(["14_large_transport_1", "large_transport_2", "large_transport_3"], draft.Members.Select(m => m.Member));
        var group = PropGroups.Write(session, draft, "transport_trio", "Three transports");
        Assert.Empty(group.Problems);
        Assert.StartsWith(workspace.ModFolder, group.File);

        var plan = PropGroups.Place(session, "Bw05", group, new Vector3(20_000, 0, 20_000), null);
        Assert.Empty(plan.Refusals);
        Assert.Empty(plan.PlacementErrors);
        session.Execute(plan.Command!);
        var db = SceneRetail.SaveAndLoad(session);

        var li01 = RetailGame.Instance.Data.Items.Systems.Get("li01")!;
        var bw05 = db.Systems.Get("bw05")!;
        var sources = names.Select(n => li01.Objects.Single(o => o.Nickname.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        var copies = plan.Adds.Select(a => bw05.Objects.Single(o => o.Nickname == a.Id.Key)).ToList();
        Assert.All(copies, c => Assert.Equal("large_transport", c.Archetype!.Nickname, ignoreCase: true));
        for (var i = 1; i < 3; i++)
        {
            var before = sources[i].Position - sources[0].Position;
            var after = copies[i].Position - copies[0].Position;
            Assert.True(Vector3.Distance(before, after) < 1.5f, $"{names[i]}: {before} and {after}");
        }
    }

    /// <summary>The group of <c>Li01_14</c> has six members. It leaves out one <c>base</c> line and keeps
    /// <c>dock_with</c>. A placement in Bw05 leaves out <c>dock_with</c> with a warning, because the base is in
    /// Li01.</summary>
    [Fact]
    public void Li01_14MakesAGroupOfSix()
    {
        using var workspace = SceneRetail.Workspace("m23six");
        var session = workspace.Open();

        var draft = PropGroups.Draft(session, [SceneRetail.Section(session, SceneRetail.Li01, "Object", "Li01_14")]);

        Assert.Equal(6, draft.Members.Count);
        Assert.Equal("base", Assert.Single(draft.LeftOut).Key);
        Assert.Contains(draft.Members[0].Lines, l => l.Name.Equals("dock_with", StringComparison.OrdinalIgnoreCase));
        var group = PropGroups.Write(session, draft, "li01_14_group", "Li01_14");
        var plan = PropGroups.Place(session, "Bw05", group, Vector3.Zero, null);
        Assert.Empty(plan.PlacementErrors);
        Assert.Contains(plan.Warnings, w => w.Message.StartsWith("14: the base ", StringComparison.Ordinal) &&
                                            w.Message.Contains(" is in Li01. The copy docks with no base."));
        session.Execute(plan.Command!);
        Assert.Empty(session.Get(plan.Adds[0])!.Lines("dock_with"));
        Assert.Equal(6, plan.Adds.Count);
    }

    /// <summary>Review finding R25: a retail nickname with a space gives a member id with a space, and the placement
    /// loads.</summary>
    [Fact]
    public void AKu01NicknameWithASpacePlaces()
    {
        using var workspace = SceneRetail.Workspace("m23space");
        var session = workspace.Open();
        var ku01 = session.SystemPath("Ku01")!;

        var draft = PropGroups.Draft(session, [SceneRetail.Section(session, ku01, "Object", "Ku01_large transport_1")]);
        Assert.Equal("large transport_1", draft.Members[0].Member);
        var group = PropGroups.Write(session, draft, "ku_transport", "Kusari transport");
        Assert.Equal("large transport_1", group.Members[0].Member);
        var plan = PropGroups.Place(session, "Bw05", group, new Vector3(-20_000, 0, 5_000), null);
        Assert.Empty(plan.Refusals);
        session.Execute(plan.Command!);
        var db = SceneRetail.SaveAndLoad(session);

        Assert.Equal("bw05_large transport_1", plan.Adds[0].Id.Key);
        Assert.Contains(db.Systems.Get("bw05")!.Objects, o => o.Nickname == "bw05_large transport_1");
    }
}

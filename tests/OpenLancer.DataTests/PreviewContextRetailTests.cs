using System;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Client;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The preview contexts and plans of retail scripts with the loaded retail data, without a GPU (test 13.2 item 6 of the
/// M8 design). A plan is pure data, so these tests check the order of the game without a build.
/// </summary>
public sealed class PreviewContextRetailTests(ITestOutputHelper output)
{
    internal const string AmbiDay = @"DATA\Scripts\Bases\Li_01_Cityscape_ambi_day_01.thn";
    private const string Traffic = "Li_01_Cityscape_traffic_01.thn";
    private const string Hardpoint = "Li_01_cityscape_hardpoint_01.thn";
    private const string Manhattan = "Li01_01_base";
    private const string Cityscape = "Cityscape";

    private static readonly Lazy<ThnReferences> Retail = new(() =>
        ThnReferences.Scan(FileSystem.FromPath(GameDataPaths.RequireBase())));

    static PreviewContextRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// An ambient script of the Manhattan cityscape plays in its room: the set script, then the scene scripts, each
    /// once, with the open script in its own place. The room plays without a cutscene, so the plan has the room camera
    /// and the ship marker.
    /// </summary>
    [Fact]
    public void AnAmbientScriptPlaysInItsRoomWithTheRoomCameraAndTheShipMarker()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;

        var context = PreviewContext.Suggest(AmbiDay, Retail.Value, items);
        var plan = context.Plan(AmbiDay, items, Retail.Value);

        Assert.Equal(PreviewContextKind.Room, context.Kind);
        Assert.Equal(Manhattan, context.Base, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Cityscape, context.Room, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Hardpoint, Path.GetFileName(plan.SetScript), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([Path.GetFileName(AmbiDay), Traffic], plan.Scripts.Select(Path.GetFileName),
            StringComparer.OrdinalIgnoreCase);
        Assert.Single(plan.Scripts, p => VfsPath.Comparer.Equals(p, AmbiDay));
        Assert.Null(plan.Cutscene);
        Assert.Equal("Camera_0", plan.RoomCamera);
        Assert.Equal("X/Shipcentre/01", plan.ShipMarker);
        output.WriteLine($"{context.Label}: set {plan.SetScript}, scripts {string.Join(", ", plan.Scripts)}, " +
                         $"camera {plan.RoomCamera}, ship marker {plan.ShipMarker}.");
    }

    /// <summary>
    /// The Manhattan intro plays in the same room: the scene scripts of the room, then s002xe as the cutscene, without
    /// the room camera. Its RTC file gives the [Char] lines.
    /// </summary>
    [Fact]
    public void S002xePlaysAsTheCutsceneAfterTheSceneScriptsOfItsRoom()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;

        var context = PreviewContext.Suggest(ThnPatchRetailTests.S002xe, Retail.Value, items);
        var plan = context.Plan(ThnPatchRetailTests.S002xe, items, Retail.Value);

        Assert.Equal(PreviewContextKind.Room, context.Kind);
        Assert.Equal(Manhattan, context.Base, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Cityscape, context.Room, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Hardpoint, Path.GetFileName(plan.SetScript), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([Path.GetFileName(AmbiDay), Traffic, Path.GetFileName(ThnPatchRetailTests.S002xe)],
            plan.Scripts.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ThnPatchRetailTests.S002xe, plan.Cutscene, VfsPath.Comparer);
        Assert.Null(plan.RoomCamera);
        Assert.Null(plan.ShipMarker);
        Assert.Null(plan.Note);
        Assert.Equal("M000_s002xe_Li01_01_nrml.ini", Path.GetFileName(plan.Encounter), StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"{context.Label}: scripts {string.Join(", ", plan.Scripts)}, cutscene {plan.Cutscene}, " +
                         $"encounter {plan.Encounter}.");
    }

    /// <summary>
    /// A goods cart script plays as the cutscene in its room, after the scene scripts. A banner says that the game does
    /// not play the cart scripts (section 3 of the M8 design).
    /// </summary>
    [Fact]
    public void ACartScriptPlaysAsTheCutsceneOfItsRoomWithTheCartNote()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        var cart = ThnReferences.PathOf(Retail.Value.All.First(r =>
            r.Section.Equals("Room_Info", StringComparison.OrdinalIgnoreCase) &&
            r.Key.Equals("goodscart_script", StringComparison.OrdinalIgnoreCase)).Value)!;

        var context = PreviewContext.Suggest(cart, Retail.Value, items);
        var plan = context.Plan(cart, items, Retail.Value);

        Assert.Equal(PreviewContextKind.Room, context.Kind);
        Assert.Contains(Retail.Value.Rooms(cart), u => u.Role == ThnRoomRole.Cart &&
                                                       u.Base.Equals(context.Base, StringComparison.OrdinalIgnoreCase) &&
                                                       u.Room.Equals(context.Room, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(cart, plan.Cutscene, VfsPath.Comparer);
        Assert.Equal(cart, plan.Scripts[^1], VfsPath.Comparer);
        Assert.Null(plan.RoomCamera);
        Assert.Null(plan.ShipMarker);
        Assert.Contains("goods cart", plan.Note);
        output.WriteLine($"{cart}: {context.Label}, note: {plan.Note}");
    }

    /// <summary>
    /// A mission ambient of <c>Act_AddAmbient</c> plays in its room after the scene scripts, as GetMsnAmbients adds it
    /// (RoomGameplay.cs:1053). The room plays without a cutscene, so the plan has the room camera and the ship marker.
    /// </summary>
    [Fact]
    public void AMissionAmbientPlaysAfterTheSceneScriptsOfItsRoomWithoutACutscene()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        var (ambient, use) = Retail.Value.All
            .Where(r => r.Key.Equals("Act_AddAmbient", StringComparison.OrdinalIgnoreCase))
            .Select(r => ThnReferences.PathOf(r.Value)!)
            .SelectMany(p => Retail.Value.Rooms(p).Where(u => u.Role == ThnRoomRole.Ambient).Select(u => (p, u)))
            .First(a => items.Bases.Get(a.u.Base)?.Rooms.Get(a.u.Room) != null);
        var room = items.Bases.Get(use.Base)!.Rooms.Get(use.Room)!;
        var sceneScripts = room.SceneScripts.Select(s => s.Thn?.DataPath is { } p ? VfsPath.Normalize(p) : null)
            .OfType<string>().Distinct(VfsPath.Comparer).ToList();

        var context = PreviewContext.Choices(ambient, Retail.Value, items).First(c =>
            c.Kind == PreviewContextKind.Room && c.Base!.Equals(use.Base, StringComparison.OrdinalIgnoreCase) &&
            c.Room!.Equals(use.Room, StringComparison.OrdinalIgnoreCase));
        var plan = context.Plan(ambient, items, Retail.Value);

        Assert.DoesNotContain(ambient, sceneScripts, VfsPath.Comparer);
        Assert.Equal([.. sceneScripts, ambient], plan.Scripts, VfsPath.Comparer);
        Assert.Null(plan.Cutscene);
        Assert.NotNull(room.Camera);
        Assert.Equal(room.Camera, plan.RoomCamera);
        Assert.Equal(room.PlayerShipPlacement, plan.ShipMarker);
        Assert.Null(plan.Note);
        output.WriteLine($"{ambient}: {context.Label}, scripts {string.Join(", ", plan.Scripts)}, camera " +
                         $"{plan.RoomCamera}, ship marker {plan.ShipMarker}.");
    }

    /// <summary>
    /// A base script with <c>$terrain_*</c> templates gets a note in Script only that names its room, because only the
    /// context of a room fills in the templates (section 8.2 of the M8 design). In its room, and for a script without
    /// templates, the plan has no note.
    /// </summary>
    [Fact]
    public void AScriptWithTerrainTemplatesAloneGetsANoteThatNamesItsRoom()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        const string terrain = @"DATA\SCRIPTS\BASES\Ambi_Terrain_Static_D01.thn";
        var script = Model(baseFolder, terrain);
        var room = PreviewContext.Choices(terrain, Retail.Value, items).First(c => c.Kind == PreviewContextKind.Room);

        var alone = PreviewContext.ScriptOnly.Plan(terrain, items, Retail.Value, script);

        Assert.Contains("$terrain_*", alone.Note);
        Assert.Contains(room.Label, alone.Note);
        Assert.Null(room.Plan(terrain, items, Retail.Value, script).Note);
        Assert.Null(PreviewContext.ScriptOnly.Plan(terrain, items, Retail.Value).Note);
        Assert.Null(PreviewContext.ScriptOnly.Plan(ThnPatchRetailTests.S002xe, items, Retail.Value,
            Model(baseFolder, ThnPatchRetailTests.S002xe)).Note);
        output.WriteLine($"{terrain}: {alone.Note}");
    }

    /// <summary>
    /// The <c>$terrain_*</c> substitutions of each plan equal the substitutions of the engine's room context, for the
    /// first room of each retail base. So a later engine change of the rule shows here.
    /// </summary>
    [Fact]
    public void TheSubstitutionsOfAPlanAreTheSubstitutionsOfTheEngine()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        var compared = 0;
        var withTerrain = 0;
        foreach (var dock in items.Bases)
        {
            var room = dock.Rooms.FirstOrDefault(r => r.SetScript?.DataPath != null);
            if (room == null)
                continue;
            var context = new PreviewContext(PreviewContextKind.Room, dock.Nickname, room.Nickname, null, null);
            var plan = context.Plan(room.SetScript!.DataPath!, items, Retail.Value);
            var engine = ThnRoomHandler.CreateContext(dock, room).Substitutions;
            Assert.Equal(engine.OrderBy(p => p.Key, StringComparer.Ordinal),
                plan.Substitutions.OrderBy(p => p.Key, StringComparer.Ordinal));
            compared++;
            if (engine.Count > 0)
                withTerrain++;
        }

        var manhattan = items.Bases.Get(Manhattan)!;
        var cityscape = manhattan.Rooms.Get(Cityscape)!;
        Assert.Equal(ThnRoomHandler.CreateContext(manhattan, cityscape).Substitutions.OrderBy(p => p.Key),
            PreviewContext.Suggest(AmbiDay, Retail.Value, items).Plan(AmbiDay, items, Retail.Value).Substitutions
                .OrderBy(p => p.Key));
        Assert.True(withTerrain > 0);
        output.WriteLine($"{compared} bases compared, {withTerrain} of them with terrain substitutions. Manhattan: " +
                         string.Join(", ", ThnRoomHandler.CreateContext(manhattan, cityscape).Substitutions
                             .Select(p => $"{p.Key} = {p.Value}")));
    }

    /// <summary>
    /// The fidget of a room fixture gives the Fidget kind, with the room and the stand marker of the fixture. The room
    /// has the NPC with that fidget at that marker, and the room plays without a cutscene.
    /// </summary>
    [Fact]
    public void AFixtureFidgetPlaysOnAStandInAtTheSpotOfTheFixture()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        var fidget = Retail.Value.All.First(r => r.Section.Equals("MRoom", StringComparison.OrdinalIgnoreCase));
        var path = ThnReferences.PathOf(fidget.Value)!;

        var context = PreviewContext.Suggest(path, Retail.Value, items);
        var plan = context.Plan(path, items, Retail.Value);

        Assert.Equal(PreviewContextKind.Fidget, context.Kind);
        Assert.NotNull(context.Fixture);
        var room = items.Bases.Get(context.Base)!.Rooms.Get(context.Room)!;
        var npc = Assert.Single(room.Npcs,
            n => n.Placement?.Spot.Equals(context.Fixture, StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(path, VfsPath.Normalize(npc.Placement!.FidgetScript.DataPath!), VfsPath.Comparer);
        Assert.Equal(path, plan.Fidget, VfsPath.Comparer);
        Assert.Equal(context.Fixture, plan.FidgetSpot);
        Assert.Null(plan.Cutscene);
        Assert.Equal(room.Camera, plan.RoomCamera);
        Assert.DoesNotContain(path, plan.Scripts, VfsPath.Comparer);
        output.WriteLine($"{path}: {context.Label}, NPC {npc.Nickname}, camera {plan.RoomCamera}.");
    }

    /// <summary>
    /// The plan tells how it plays the open script, so the Timeline greys the events that the game does not run there.
    /// The set script of the Manhattan cityscape plays as the set script of its room, a fixture fidget plays as a
    /// fidget, and s002xe and the ambient script of the room play with all their events. The editor opens a script by
    /// its path in normal form.
    /// </summary>
    [Fact]
    public void ThePlanOfARetailScriptGivesHowItPlays()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        var hardpoint = VfsPath.Normalize(@"DATA\Scripts\Bases\" + Hardpoint)!;
        var fidget = ThnReferences.PathOf(
            Retail.Value.All.First(r => r.Section.Equals("MRoom", StringComparison.OrdinalIgnoreCase)).Value)!;
        ScriptPlay PlayOf(string path) => PreviewContext.Suggest(path, Retail.Value, items)
            .Plan(path, items, Retail.Value).PlayOf(VfsPath.Normalize(path)!);

        var set = PreviewContext.Suggest(hardpoint, Retail.Value, items);
        Assert.Equal(PreviewContextKind.Room, set.Kind);
        Assert.Equal(Manhattan, set.Base, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Cityscape, set.Room, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ScriptPlay.Set, PlayOf(hardpoint));
        Assert.Equal(ScriptPlay.Fidget, PlayOf(fidget));
        Assert.Equal(ScriptPlay.Normal, PlayOf(ThnPatchRetailTests.S002xe));
        Assert.Equal(ScriptPlay.Normal, PlayOf(AmbiDay));
        output.WriteLine($"{hardpoint}: {set.Label}, {PlayOf(hardpoint)}. {fidget}: {PlayOf(fidget)}.");
    }

    /// <summary>
    /// The first Space context of the first Freeport 7 scene plays in the system of its trigger. The script has no
    /// main_object marker, so the plan has no main object.
    /// </summary>
    [Fact]
    public void TheFirstSpaceContextOfAFreeport7SceneHasItsSystemAndNoMainObject()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        const string path = @"DATA\MISSIONS\M01A\m01a_fp7_01.thn";
        var spaces = new SpaceScenes(items.VFS, Retail.Value, SpaceFacts.From(items));

        var space = PreviewContext.Choices(path, Retail.Value, items, spaces)
            .First(c => c.Kind == PreviewContextKind.Space);
        var plan = space.Plan(path, items, Retail.Value, Model(baseFolder, path));

        Assert.Equal("FP7_system", plan.System, ignoreCase: true);
        Assert.Null(plan.MainObject);
        Assert.Null(plan.MainObjectStart);
        Assert.NotNull(plan.Cutscene);
        output.WriteLine($"{space.Label}: {plan.Note}");
    }

    /// <summary>The model of a retail script, as the editor reads it.</summary>
    private static ThnScriptModel Model(string baseFolder, string path) =>
        ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(File.ReadAllBytes(Path.Combine(baseFolder, path)), path),
            ThnElementId.Counter());

    /// <summary>A mission script that the game plays in space gives the Space kind. A GCS script that is not a fidget
    /// plays alone.</summary>
    [Fact]
    public void AMissionScriptGivesSpaceAndAGcsScriptPlaysAlone()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        var mission = ThnReferences.PathOf(Retail.Value.All.First(r =>
            r.Key.Equals("Act_CallThorn", StringComparison.OrdinalIgnoreCase)).Value)!;
        var gcs = Retail.Value.All
            .Where(r => r.Section.Equals("GenericScripts", StringComparison.OrdinalIgnoreCase))
            .Select(r => ThnReferences.PathOf(r.Value)!)
            .First(p => !Retail.Value.IsFidget(p) && Retail.Value.Rooms(p).Count == 0);

        var space = PreviewContext.Suggest(mission, Retail.Value, items);
        var alone = PreviewContext.Suggest(gcs, Retail.Value, items);

        Assert.Equal(PreviewContextKind.Space, space.Kind);
        Assert.NotNull(space.Plan(mission, items, Retail.Value).Note);
        Assert.False(Retail.Value.IsFidget(gcs));
        Assert.Equal(PreviewContextKind.ScriptOnly, alone.Kind);
        var plan = alone.Plan(gcs, items, Retail.Value);
        // PathOf gives the normal form, which the plan keeps.
        Assert.Equal([gcs], plan.Scripts);
        Assert.Equal(gcs, plan.Cutscene);
        Assert.Null(plan.SetScript);
    }
}

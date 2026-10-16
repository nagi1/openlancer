using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema.Save;
using LibreLancer.Interface;
using LibreLancer.Resources;
using LibreLancer.Server;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;
using OpenLancer.Runtime.Play;
using OpenLancer.Runtime.Scene;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the hot reload (section 12.2 of the M4 design). Each test runs the production
/// <see cref="PlayReload"/> on a server without a window, in the order of the play host:
/// <see cref="PlayReload.CheckLeave"/> and <see cref="PlayReload.Leave"/>, <see cref="PlayReload.Load"/> on a task,
/// <see cref="PlayReload.Apply"/>, and <see cref="PlayReload.Resume"/> with the turn to <see cref="PlayPlace.Start"/>.
/// </summary>
/// <remarks>
/// Each test has its own game data on the live file system of a temp profile, as the play process has. The convex
/// shapes of that data go after the last server stops. In a Debug build, the buffer pool of the shapes fails an assert
/// when the collector finds it not cleared, and the test host ends.
/// </remarks>
public sealed class PlayReloadRetailTests(ITestOutputHelper output)
{
    /// <summary>The limit from the start of a server to the spawn packet. The first load of Li01 is the slow part.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(30);

    private const string ModId = "m4proof";

    private const string Depot = "Li01_14_depot_water";

    /// <summary>Where the later mod of the profile puts the depot. The play view of the active mod leaves that mod out
    /// (decision D38).</summary>
    private static readonly Vector3 LaterDepot = new(-30000, 0, -27705);

    /// <summary>A good whose price the tests change.</summary>
    private const string PricedGood = "ge_s_battery_01";

    /// <summary>The key of a text that a save adds to the active mod.</summary>
    private const string TextKey = "m4proof.reload";

    private const string Text = "OpenLancer reload text";

    /// <summary>A ship that the active mod adds as a copy of <c>ge_fighter</c>, as the Database adds it.</summary>
    private const string ModShip = "m4proof_fighter";

    /// <summary>A gun of the loadout <c>MP_ge_fighter</c> of the new character. The ship mounts it on two hardpoints,
    /// and its good is not combinable in retail.</summary>
    private const string Gun = "li_gun01_mark01";

    /// <summary>A point in open space in Li01, about 6,000 units from the depot.</summary>
    private static readonly Vector3 Li01Point = new(-33270, 0, -25039);

    /// <summary>Yaw 90, as the Scene camera turns. A save does not keep it, so only the place of the reload does.</summary>
    private static readonly Quaternion Heading = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0);

    static PlayReloadRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// A reload after a save shows the new data at the same place: the moved depot in the world of the new server, the
    /// new price in the items and in the base prices of the new server, the new merged files in the live file system,
    /// and the new text in the UI. The ship keeps its system, its position and its heading. The world of the old server
    /// freed its physics.
    /// </summary>
    [Fact]
    public async Task AReloadShowsTheNewDataAtTheSamePlace()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var game = new ReloadGame(baseFolder);
        var old = game.Start(output);
        var session = game.Session;
        var systemPath = session.SystemPath("Li01")!;
        var depot = new SectionRef(systemPath, new SectionIdentity("Object", Depot));
        var good = session.Find(ItemFileKind.Goods, PricedGood)!;
        var oldPrice = game.Data.Items.Goods.Get(PricedGood)!.Ini.Price;
        var newPrice = oldPrice + 1234;
        var oldDepot = old.OnWorld(w => w.GameWorld.GetObject(Depot)!.Transform.Position);
        var shipBefore = old.OnWorld(w => w.Players[old.Player].Transform);
        var (oldWorld, poolBefore) = old.OnWorld(w => (w, w.GameWorld.Physics!.BufferPool.GetTotalAllocatedByteCount()));
        var oldSystemBytes = game.Live.ReadAllBytes(systemPath);
        var oldGoodsBytes = game.Live.ReadAllBytes(good.Path);
        var oldBaseline = Baseline(old.Server);
        var textsBefore = game.Ui.Infocards!;

        // The move of the M2 proof, +1,000 on X, a new price and a new text, saved as the editor saves them.
        var start = session.Get(depot)!.Lines("pos").Single().Values;
        var moved = SceneBuild.Position(start) + new Vector3(1000, 0, 0);
        session.Execute(EditCommand.SetLines(depot, "pos", [TransformValues.Line(start, moved)]));
        session.Execute(EditCommand.SetLines(good, "price", [[new Int32Value(newPrice)]]));
        session.Execute(EditCommand.SetText(StringKind.String, TextKey, Text));
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));

        Assert.Null(PlayReload.CheckLeave(old.Server));
        var clock = Stopwatch.StartNew();
        PlayPlace place;
        var poolAfter = poolBefore;
        try
        {
            place = old.Leave();
            poolAfter = oldWorld.GameWorld.Physics!.BufferPool.GetTotalAllocatedByteCount();
        }
        finally
        {
            // Without OL-0021, the world stays alive after the stop. Then the test finishes it, so that its finalizer
            // cannot end the test host at a later collection.
            if (poolAfter != 0)
                oldWorld.Finish();
        }
        var leaveMs = clock.ElapsedMilliseconds;
        var load = Load(game.Workspace);
        var loadMs = clock.ElapsedMilliseconds - leaveMs;
        var data = await load;
        var failure = PlayReload.Apply(load, place, game.Target);
        var next = game.Resume(place, old, output);
        var ship = next.OnWorld(w => w.Players[next.Player].Transform);
        var newDepot = next.OnWorld(w => w.GameWorld.GetObject(Depot)!.Transform.Position);
        Assert.True(data.Overlay.StringIds.TryGet(ModId, new StringPlaceholder(StringKind.String, TextKey), out var textId));

        output.WriteLine($"Leave {leaveMs} ms, Load {loadMs} ms, to the spawn {clock.ElapsedMilliseconds} ms.");
        output.WriteLine($"Place: {place.System}, base {place.Base ?? "none"}, {place.Position}, " +
                         $"dot product with the heading {Dot(place.Orientation)}.");
        output.WriteLine($"Ship before {shipBefore.Position}, after {ship.Position}: " +
                         $"{Vector3.Distance(ship.Position, shipBefore.Position)} apart. Dot product with the heading " +
                         $"{Dot(shipBefore.Orientation)} before, {Dot(ship.Orientation)} after.");
        output.WriteLine($"Depot {oldDepot} before, {newDepot} after. Price of {PricedGood} {oldPrice} before, " +
                         $"{game.Data.Items.Goods.Get(PricedGood)!.Ini.Price} in the items after, base price " +
                         $"{oldBaseline} before, {Baseline(next.Server)} after.");
        output.WriteLine($"Text {textId}: '{textsBefore.GetStringResource(textId.Value)}' in the UI before, " +
                         $"'{game.Ui.Infocards!.GetStringResource(textId.Value)}' after.");
        output.WriteLine($"Buffer pool of the old world: {poolBefore} bytes before Leave, {poolAfter} bytes after it.");

        Assert.Null(failure);

        // The new price is in the items and in the base prices of the new server.
        Assert.Equal(oldPrice, (int)oldBaseline);
        Assert.Equal(newPrice, game.Data.Items.Goods.Get(PricedGood)!.Ini.Price);
        Assert.Equal(newPrice, (int)Baseline(next.Server));
        Assert.Same(data.Items, game.Data.Items);

        // The live file system gives the new merged files.
        Assert.Equal(Merged(data.Overlay, systemPath), game.Live.ReadAllBytes(systemPath));
        Assert.Equal(Merged(data.Overlay, good.Path), game.Live.ReadAllBytes(good.Path));
        Assert.NotEqual(oldSystemBytes, game.Live.ReadAllBytes(systemPath));
        Assert.NotEqual(oldGoodsBytes, game.Live.ReadAllBytes(good.Path));

        // The UI and the save list show the texts and the tables of the new items. The texts of the start do not have
        // the new text.
        Assert.NotEqual(Text, textsBefore.GetStringResource(textId.Value));
        Assert.Equal(Text, game.Ui.Infocards!.GetStringResource(textId.Value));
        Assert.Same(data.Items.Ini.Infocards, game.Ui.Infocards);
        Assert.Same(data.Items.Ini.Infocards, game.Saves.Infocards);
        Assert.Same(data.Items.Ini.Rollover.Map, game.Ui.RolloverMap);
        Assert.Same(data.Items.Ini.BaseNavBar.Navbar, game.Ui.NavbarIcons);
        Assert.Same(data.NavmapIcons, game.Ui.NavmapIcons);

        // The ship keeps its system, its position and its heading.
        Assert.Equal("li01", place.System, ignoreCase: true);
        Assert.Null(place.Base);
        Assert.True(Dot(place.Orientation) > 0.9999f, $"The place has the dot product {Dot(place.Orientation)}.");
        Assert.Equal("li01", next.Player.System, ignoreCase: true);
        Assert.Null(next.Player.Base);
        Assert.InRange(Vector3.Distance(ship.Position, shipBefore.Position), 0, 1);
        Assert.True(Dot(ship.Orientation) > 0.9999f, $"The ship has the dot product {Dot(ship.Orientation)}.");

        // The depot is at its new place in the world of the new server. The later mod puts it at LaterDepot, so a load
        // of all mods of the profile fails here.
        Assert.InRange(Vector3.Distance(oldDepot, moved - new Vector3(1000, 0, 0)), 0, 0.01f);
        Assert.InRange(Vector3.Distance(newDepot, moved), 0, 0.01f);
        Assert.NotEqual(LaterDepot, moved);

        // The world of the old server freed its physics (OL-0021).
        Assert.True(poolBefore > 0, "The world of the player has no physics memory.");
        Assert.Equal(0UL, poolAfter);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>
    /// A reload whose load fails continues with the old data, as the play host does: <see cref="PlayReload.Apply"/>
    /// gives the failure and changes nothing, and the server resumes at the place. Here a save journal stays in the
    /// mod, so the load stops after its wait.
    /// </summary>
    [Fact]
    public void AReloadWhoseLoadFailsResumesWithTheOldData()
    {
        using var game = new ReloadGame(GameDataPaths.RequireBase());
        var old = game.Start(output);
        var before = game.Parts();
        var journal = Path.Combine(game.Workspace.ModFolder, SaveTransaction.FolderName, "journal");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "olsave journal 1\n");

        var place = old.Leave();
        var clock = Stopwatch.StartNew();
        var load = Load(game.Workspace);
        var loadMs = clock.ElapsedMilliseconds;
        var failure = PlayReload.Apply(load, place, game.Target);
        var after = game.Parts();
        var next = game.Resume(place, old, output);
        var ship = next.OnWorld(w => w.Players[next.Player].Transform);

        output.WriteLine($"The load failed after {loadMs} ms: {failure?.Message}");
        output.WriteLine($"The ship is at {ship.Position} in {next.Player.System}, dot product with the heading " +
                         $"{Dot(ship.Orientation)}.");
        var refused = Assert.IsType<ModLoadException>(failure);
        Assert.Contains($"A save of the editor in mod '{ModId}' is not complete.", refused.Message);
        Assert.Equal(before, after);
        Assert.Equal(2, game.Live.FileProviders.Count);
        Assert.NotNull(next.Player.Space);
        Assert.Equal("li01", next.Player.System, ignoreCase: true);
        Assert.InRange(Vector3.Distance(ship.Position, Li01Point), 0, 1);
        Assert.True(Dot(ship.Orientation) > 0.9999f, $"The ship has the dot product {Dot(ship.Orientation)}.");
    }

    /// <summary>
    /// A reload keeps the old data when the new data does not have the ship of the player (review of step 7). The new
    /// server would throw on its thread at the spawn, and the game would end. Here the player flies a ship that the
    /// active mod adds, and a rename in the Database gives that ship a new nickname. The rename also changes the hull
    /// good and the package, so the mod stays valid, but the autosave names the old nickname.
    /// </summary>
    [Fact]
    public void AReloadKeepsTheOldDataWhenTheNewDataDoesNotHaveTheShipOfThePlayer()
    {
        using var game = new ReloadGame(GameDataPaths.RequireBase(), session => session.Execute(
            ItemTemplates.Build(ItemTemplates.Plan(session, session.Find(ItemFileKind.Ships, "ge_fighter")!, ModShip))));
        var old = game.Start(output, ModShip + "_package");
        var shipBefore = old.Player.Character!.Ship!.Nickname;
        var before = game.Parts();
        var session = game.Session;
        session.Execute(ItemEdits.Rename(session, session.Find(ItemFileKind.Ships, ModShip)!, ModShip + "_2"));
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));

        var place = old.Leave();
        var failure = PlayReload.Apply(Load(game.Workspace), place, game.Target);
        // The test checks the refusal before the resume. With the new items, the server thread of the resume would
        // throw, and the test host would end.
        output.WriteLine($"The ship {shipBefore} before the reload. Apply gave: {failure?.Message}");
        Assert.Equal(ModShip, shipBefore, ignoreCase: true);
        var refused = Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal($"The new data cannot load the autosave of the player. The ship '{ModShip}' is not in the game data.",
            refused.Message);
        Assert.Equal(before, game.Parts());

        var next = game.Resume(place, old, output);
        Assert.Equal(ModShip, next.Player.Character!.Ship!.Nickname, ignoreCase: true);
        Assert.Equal("li01", next.Player.System, ignoreCase: true);
    }

    /// <summary>
    /// A reload puts a mounted good that the new data makes combinable into the hold (decision D56, review of step 8).
    /// The server cannot mount a combinable good. It throws on its thread, which has no catch, and the game would end.
    /// Here a save makes the gun <see cref="Gun"/> combinable. The ship of the new character has it on two hardpoints.
    /// </summary>
    [Fact]
    public void AReloadPutsAMountedGoodThatTheNewDataMakesCombinableIntoTheHold()
    {
        using var game = new ReloadGame(GameDataPaths.RequireBase());
        var old = game.Start(output);
        var mountedBefore = Mounted(old.Player.Character!);
        Assert.Equal(2, mountedBefore.Count(m => m.Item == Gun));
        var session = game.Session;
        session.Execute(EditCommand.SetLines(session.Find(ItemFileKind.Goods, Gun)!, "combinable",
            [[new StringValue("true")]]));
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));

        var place = old.Leave();
        var failure = PlayReload.Apply(Load(game.Workspace), place, game.Target);
        var player = place.Save.Player!;
        var gunLines = player.Equip.Count(e => IsGun(e.Item));
        var gunCargo = player.Cargo.Where(c => IsGun(c.Item)).Select(c => c.Count).ToList();
        output.WriteLine($"Mounted before the reload: {string.Join(", ", mountedBefore)}.");
        output.WriteLine($"Apply gave: {failure?.Message ?? "no failure"}. The autosave has {gunLines} equip lines and " +
                         $"the cargo lines [{string.Join(", ", gunCargo)}] of {Gun}.");
        // The test checks the autosave before the resume. With an equip line of the gun, the server thread of the
        // resume would throw, and the test host would end.
        Assert.Null(failure);
        Assert.Equal(0, gunLines);
        Assert.Equal([1, 1], gunCargo);

        var next = game.Resume(place, old, output);
        var character = next.Player.Character!;
        var hold = character.Items.Where(i => i.Hardpoint == null && IsGun(i.Equipment!.Nickname))
            .Select(i => i.Count).ToList();
        output.WriteLine($"Mounted after the reload: {string.Join(", ", Mounted(character))}. The hold has " +
                         $"[{string.Join(", ", hold)}] of {Gun}.");
        Assert.Equal(mountedBefore.Where(m => m.Item != Gun), Mounted(character));
        // The server adds the second gun to the stack of the first one.
        Assert.Equal([2], hold);
        Assert.Equal("li01", next.Player.System, ignoreCase: true);
    }

    /// <summary>
    /// <see cref="PlayReload.Apply"/> changes nothing for data that the game cannot take: an overlay of another base
    /// folder, and an autosave whose system or base the new data does not have. A refusal of the base folder comes from
    /// <see cref="ModOverlay.Replace"/>, so Apply must call it before it changes the items.
    /// </summary>
    [Fact]
    public async Task ApplyChangesNothingForDataThatTheGameCannotTake()
    {
        using var game = new ReloadGame(GameDataPaths.RequireBase());
        // The server only makes the saves. It does not start.
        using var headless = new HeadlessServer(game.Data);
        var data = await Load(game.Workspace);
        var otherBase = Directory.CreateDirectory(Path.Combine(game.Workspace.Root, "other-base")).FullName;
        var otherData = data with { Overlay = ModOverlay.Build(otherBase, [], DllStringResourceWriter.Instance, ThornEvaluator.Instance) };
        var before = game.Parts();

        var folder = PlayReload.Apply(Task.FromResult(otherData), Place(headless, _ => { }), game.Target);
        var afterFolder = game.Parts();
        var system = PlayReload.Apply(Task.FromResult(data), Place(headless, p => p.System = "no_such_system"),
            game.Target);
        var dock = PlayReload.Apply(Task.FromResult(data), Place(headless, p => p.Base = "no_such_base"), game.Target);

        output.WriteLine($"Another base folder: {folder?.Message}");
        output.WriteLine($"An unknown system: {system?.Message}");
        output.WriteLine($"An unknown base: {dock?.Message}");
        Assert.StartsWith("The file system does not show an overlay of the base folder",
            Assert.IsType<InvalidOperationException>(folder).Message);
        Assert.Equal(before, afterFolder);
        Assert.Equal("The new data cannot load the autosave of the player. The system 'no_such_system' is not in the " +
                     "game data.", Assert.IsType<InvalidOperationException>(system).Message);
        Assert.Equal("The new data cannot load the autosave of the player. The base 'no_such_base' is not in the game " +
                     "data.", Assert.IsType<InvalidOperationException>(dock).Message);
        Assert.Equal(before, game.Parts());
        Assert.Equal(2, game.Live.FileProviders.Count);
    }

    /// <summary>
    /// A server that does not run never writes the autosave. So <see cref="PlayReload.CheckLeave"/> gives the reason,
    /// and <see cref="PlayReload.Leave"/> refuses at once and does not wait for the limit. The game is in this case when
    /// it left the play, for example with Quit to menu: the session of the play stopped its server.
    /// </summary>
    [Fact]
    public void LeaveRefusesAServerThatDoesNotRun()
    {
        GameDataPaths.RequireBase();
        using var headless = new HeadlessServer(RetailGame.Instance.Data);
        headless.Start(PlaySave.Create(headless.Server, new PlayStart.InSpace("li01", Li01Point, Heading), null));
        headless.WaitForPacket(HeadlessServer.SpawnPacket, StartLimit);
        headless.Stop();

        var clock = Stopwatch.StartNew();
        var reason = PlayReload.CheckLeave(headless.Server);
        var refused = Assert.Throws<InvalidOperationException>(() => PlayReload.Leave(headless.Server));

        output.WriteLine($"Leave refused after {clock.ElapsedMilliseconds} ms: {refused.Message}");
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.StartsWith("The server of the play does not run", reason);
        Assert.Equal(reason, refused.Message);
    }

    /// <summary>
    /// Twenty reloads in a row end without an exception, and they leak no thread. Each stopped server frees its worlds
    /// with their physics threads (OL-0021), and nothing holds a stopped server. A leak of one world costs three
    /// physics threads on a machine with 16 cores, and a leak of a server costs its thread too.
    /// </summary>
    /// <remarks>
    /// The count leaves out the threads of the thread pool. The load of the items blocks pool threads while it waits
    /// for its tasks, so the pool adds threads, and it ends them later when they have no work. A server thread and a
    /// physics thread are not pool threads.
    /// </remarks>
    [Fact]
    public void TwentyReloadsLeakNoThread()
    {
        const int cycles = 20;
        const int extraThreads = 4;
        using var game = new ReloadGame(GameDataPaths.RequireBase());
        var server = game.Start(output);
        var counts = new (int Threads, int PoolThreads)[cycles];
        var times = new long[cycles];
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var clock = Stopwatch.StartNew();
            var place = server.Leave();
            Assert.Null(PlayReload.Apply(Load(game.Workspace), place, game.Target));
            server = game.Resume(place, server, output: null);
            times[cycle] = clock.ElapsedMilliseconds;
            counts[cycle] = Threads();
        }

        var ship = server.OnWorld(w => w.Players[server.Player].Transform);
        var first = counts[0].Threads - counts[0].PoolThreads;
        var last = counts[^1].Threads - counts[^1].PoolThreads;
        output.WriteLine($"Reload times in ms: {string.Join(", ", times)}.");
        output.WriteLine($"Threads (pool threads) after each reload: " +
                         $"{string.Join(", ", counts.Select(c => $"{c.Threads} ({c.PoolThreads})"))}.");
        output.WriteLine($"Threads outside the pool: {first} after reload 1, {last} after reload {cycles}.");
        output.WriteLine($"The ship is {Vector3.Distance(ship.Position, Li01Point)} from the start, dot product with " +
                         $"the heading {Dot(ship.Orientation)}.");
        Assert.InRange(last - first, int.MinValue, extraThreads);
    }

    /// <summary>Runs <see cref="PlayReload.Load"/> on a task, as the reload state of the play host runs it, and gives
    /// the ended task.</summary>
    private static Task<PlayData> Load(RetailWorkspace workspace)
    {
        var load = Task.Run(() => PlayReload.Load(workspace.ProfilePath, ModId));
        ((IAsyncResult)load).AsyncWaitHandle.WaitOne();
        return load;
    }

    /// <summary>A place in space in Li01 whose autosave is a play save with a change.</summary>
    /// <param name="server">A server that makes the save. It does not have to run.</param>
    private static PlayPlace Place(HeadlessServer server, Action<SavePlayer> change)
    {
        var save = PlaySave.Create(server.Server, new PlayStart.InSpace("li01", Li01Point, Heading), null);
        change(save.Player!);
        return new PlayPlace(save, save.Player!.System!, save.Player.Base, Li01Point, Heading);
    }

    private static bool IsGun(HashValue item) => item.Hash == FLHash.CreateID(Gun);

    private static bool IsGun(string nickname) => nickname.Equals(Gun, StringComparison.OrdinalIgnoreCase);

    /// <summary>The mounted items of a character: the equipment in lower case and the hardpoint, sorted.</summary>
    private static List<(string Item, string Hardpoint)> Mounted(NetCharacter character) =>
        character.Items.Where(i => i.Hardpoint != null)
            .Select(i => (i.Equipment!.Nickname.ToLowerInvariant(), i.Hardpoint!.ToLowerInvariant())).Order().ToList();

    /// <summary>The base price of <see cref="PricedGood"/> that a server took from its data at its start.</summary>
    private static ulong Baseline(GameServer server) =>
        server.BaselineGoodPrices.Prices.Single(p => p.GoodCRC == FLHash.CreateID(PricedGood)).Price;

    /// <summary>The bytes of a merged file of an overlay.</summary>
    private static byte[] Merged(ModOverlay overlay, string path) =>
        overlay.Files.Single(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ReadAllBytes();

    /// <summary>The absolute dot product of an orientation with <see cref="Heading"/>: 1 for the same turn.</summary>
    private static float Dot(Quaternion orientation) => Same(orientation, Heading);

    /// <summary>The absolute dot product of two orientations: 1 for the same turn.</summary>
    private static float Same(Quaternion a, Quaternion b) =>
        MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)));

    /// <summary>The threads of the process, and the threads of the thread pool among them.</summary>
    private static (int Threads, int PoolThreads) Threads()
    {
        using var process = Process.GetCurrentProcess();
        return (process.Threads.Count, ThreadPool.ThreadCount);
    }

    /// <summary>The objects of the game that <see cref="PlayReload.Apply"/> replaces. Two values are equal when they
    /// have the same objects.</summary>
    private sealed record GameParts(GameItemDb Items, IFileProvider Layer, InfocardManager? UiTexts,
        INavmapIcons? NavmapIcons, Dictionary<int, int> Rollovers, Dictionary<string, string>? Navbar,
        InfocardManager SaveTexts);

    /// <summary>
    /// The game of a reload test, as the play process has it: a temp profile with the active mod <see cref="ModId"/>
    /// and a later mod that moves the depot, the file system of the play view for the whole test, game data on that
    /// file system, and the tables that the UI and the save list copy from the items. It disposes the server of the
    /// current play.
    /// </summary>
    private sealed class ReloadGame : IDisposable
    {
        private HeadlessServer? current;
        private EditSession? session;

        /// <param name="prepare">Edits of the active mod that the game has from its start. The game saves them before
        /// it loads its data.</param>
        public ReloadGame(string baseFolder, Action<EditSession>? prepare = null)
        {
            Workspace = new RetailWorkspace(baseFolder, ModId);
            var later = ModWorkspace.CreateMod(Path.Combine(Workspace.Root, "mods"), "later", "Later");
            var laterPatch = Path.Combine(later, Mod.PatchesFolder, @"DATA\UNIVERSE\SYSTEMS\LI01\li01.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(laterPatch)!);
            File.WriteAllText(laterPatch, $"[Object]\r\nnickname = {Depot}\r\npos = -30000, 0, -27705\r\n");
            ModWorkspace.AddToProfile(Workspace.ProfilePath, later);
            if (prepare != null)
            {
                prepare(Session);
                var saved = Session.Save();
                Assert.True(saved.Saved, string.Join("\n", saved.Problems));
            }

            Live = PlayView.Build(ModProfile.Load(Workspace.ProfilePath), ModId).CreateFileSystem();
            Data = new GameDataManager(new GameItemDb(Live), new ServerResourceManager(null, Live));
            Data.LoadData(null);
            // The copies that the game makes at its start (UiData.cs:51-64, FreelancerGame.cs:124).
            Ui = new UiData
            {
                Infocards = Data.Items.Ini.Infocards,
                NavbarIcons = Data.GetBaseNavbarIcons(),
                RolloverMap = Data.Items.Ini.Rollover.Map,
            };
            Saves = new SaveGameFolder { Infocards = Data.Items.Ini.Infocards };
            Target = new PlayGame(Live, Data, Ui, Saves);
        }

        public RetailWorkspace Workspace { get; }

        /// <summary>The edit session of the active mod, as the editor has it during the play.</summary>
        public EditSession Session => session ??= Workspace.Open();

        /// <summary>The file system that the game keeps for its whole life.</summary>
        public FileSystem Live { get; }

        public GameDataManager Data { get; }

        public UiData Ui { get; }

        public SaveGameFolder Saves { get; }

        /// <summary>The parts of the game that a reload gives the new data.</summary>
        public PlayGame Target { get; }

        /// <summary>The objects that the game has now.</summary>
        public GameParts Parts() => new(Data.Items, Live.FileProviders[1], Ui.Infocards, Ui.NavmapIcons,
            Ui.RolloverMap, Ui.NavbarIcons, Saves.Infocards);

        /// <summary>Starts a play in space in Li01 and turns the ship to <see cref="Heading"/>, as the play host
        /// does.</summary>
        /// <param name="package">The ship package, or null for the package of a new multiplayer character.</param>
        public HeadlessServer Start(ITestOutputHelper output, string? package = null)
        {
            var server = Track(new HeadlessServer(Data));
            var clock = Stopwatch.StartNew();
            var start = new PlayStart.InSpace("li01", Li01Point, Heading);
            server.Start(PlaySave.Create(server.Server, start, package));
            server.WaitForPacket(HeadlessServer.SpawnPacket, StartLimit);
            PlaySpawn.Orient(server.Player, start);
            WaitForTurn(server, Heading);
            output.WriteLine($"The start took {clock.ElapsedMilliseconds} ms.");
            return server;
        }

        /// <summary>
        /// Starts a new server at the place of a reload, waits for the spawn and turns the ship to the start of the
        /// place, as the wait state of the play host does. Then it disposes the server that the reload left.
        /// </summary>
        public HeadlessServer Resume(PlayPlace place, HeadlessServer left, ITestOutputHelper? output)
        {
            var server = Track(new HeadlessServer(Data));
            var clock = Stopwatch.StartNew();
            server.Resume(place);
            left.Dispose();
            server.WaitForPacket(HeadlessServer.SpawnPacket, StartLimit);
            var start = place.Start;
            PlaySpawn.Orient(server.Player, start);
            // The test waits for the heading that the start gives. The tests then compare the ship with Heading.
            if (start is PlayStart.InSpace space)
                WaitForTurn(server, space.Orientation);
            output?.WriteLine($"The resume took {clock.ElapsedMilliseconds} ms.");
            return server;
        }

        public void Dispose()
        {
            current?.Dispose();
            // The shapes go after the last server, which uses them.
            Data.Resources.ConvexCollection.Dispose();
            Workspace.Dispose();
        }

        private HeadlessServer Track(HeadlessServer server)
        {
            current = server;
            return server;
        }

        /// <summary>
        /// Waits until the place of the player on the server has <paramref name="orientation"/>. The world writes the
        /// place of the player at the end of an update, after the action of the turn. A reload reads that place.
        /// </summary>
        private static void WaitForTurn(HeadlessServer server, Quaternion orientation)
        {
            var clock = Stopwatch.StartNew();
            while (Same(server.OnWorld(_ => server.Player.Orientation), orientation) <= 0.9999f)
            {
                if (clock.Elapsed > HeadlessServer.WaitLimit)
                    throw new TimeoutException(
                        $"The player has not turned after {HeadlessServer.WaitLimit.TotalSeconds} s.");
                Thread.Sleep(10);
            }
        }
    }
}

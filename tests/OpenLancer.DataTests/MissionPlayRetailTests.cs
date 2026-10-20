using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Resources;
using LibreLancer.Server;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of a mission play from a start save (sections 5.6 and 12.3 of the M6c design): a headless server loads
/// the start save of each checkpoint and each mission start of the 14 story scripts, with the mission observer of the
/// play (fork commit OL-0070). The trigger of the start reports Started or Ran. Three approximate starts load with their
/// test layer, and the triggers that wait at the start report Started or Ran. The tests count and compare triggers. They
/// hold no retail text.
/// </summary>
public sealed class MissionPlayRetailTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The limit from the start of the server to the changes of the observer.</summary>
    private static readonly TimeSpan ChangeLimit = TimeSpan.FromSeconds(20);

    private readonly string folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));

    static MissionPlayRetailTests() => AppHandler.ConsoleInit(silent: true);

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }

    private static IReadOnlyList<MissionAnalysis> Scripts()
    {
        var run = RetailMissions.Run();
        return run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!).ToList();
    }

    [Fact]
    public void EachCheckpointStartLoadsAndItsRestoreTriggerReports()
    {
        var data = RetailGame.Instance.Data;
        var count = 0;
        foreach (var analysis in Scripts())
        {
            foreach (var restore in DryRun.Checkpoints(analysis.Index))
            {
                var plan = StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.At(restore.Nickname));
                var changes = Load(data, plan, [restore.Nickname]);
                Assert.True(changes.Contains(restore.Nickname),
                    $"{analysis.Script.Slot!.Slot}: the restore trigger {count} gave no change.");
                count++;
            }
        }

        Assert.Equal(34, count);
    }

    [Fact]
    public void EachMissionStartLoadsAndItsStartTriggersReport()
    {
        var data = RetailGame.Instance.Data;
        var count = 0;
        foreach (var analysis in Scripts())
        {
            var plan = StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.MissionStart);
            var starts = analysis.Index.StartTriggers.Select(t => t.Nickname).ToList();
            var changes = Load(data, plan, starts);
            foreach (var start in starts)
                Assert.True(changes.Contains(start), $"{analysis.Script.Slot!.Slot}: a start trigger gave no change.");
            count++;
        }

        Assert.Equal(14, count);
    }

    /// <summary>One approximate start in M02, M04 and M07: the first trigger in file order whose plan can start and has
    /// a setup trigger of the world. One test layer holds the three patches, so the data loads once.</summary>
    [Fact]
    public void ThreeApproximateStartsLoadAndTheirActiveSetsReport()
    {
        var plans = new List<StartPlan>();
        foreach (var slot in new[] { "M02", "M04", "M07" })
        {
            var analysis = RetailMissions.Slot(slot);
            var index = analysis.Index;
            var exact = index.StartTriggers.Concat(DryRun.Checkpoints(index)).ToHashSet();
            plans.Add(index.Triggers.Where(t => !t.IsDuplicate && index.Reached.Contains(t) && !exact.Contains(t))
                .Select(t => StartPlan.Make(index, analysis.Script.Slot, MissionStartPoint.At(t.Nickname)))
                .First(p => p.CanStart && p.Setup.Count == 2));
        }

        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6cplay");
        var layer = Path.Combine(folder, "start", "layer");
        TestLayer.Write(layer, plans[0]);
        foreach (var plan in plans.Skip(1))
        {
            var patch = Path.Combine([layer, Mod.PatchesFolder, .. plan.Script.Split('\\')]);
            Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
            File.WriteAllText(patch, plan.SetupPatch);
        }
        var files = PlayView.Build(ModProfile.Load(workspace.ProfilePath), workspace.ModId, layer).CreateFileSystem();
        var data = new GameDataManager(new GameItemDb(files), new ServerResourceManager(null, files));
        data.LoadData(null);

        foreach (var plan in plans)
        {
            var changes = Load(data, plan, [plan.Setup[0].Nickname, .. plan.ActiveSet]);
            output.WriteLine($"{plan.Slot!.Slot}: {plan.ActiveSet.Count} triggers wait, {plan.Path.Count} on the path, " +
                             $"{changes.Count} changes.");
            Assert.True(changes.Contains(plan.Setup[0].Nickname), $"{plan.Slot.Slot}: the setup trigger gave no change.");
            foreach (var trigger in plan.ActiveSet)
                Assert.True(changes.Contains(trigger), $"{plan.Slot.Slot}: a trigger of the active set gave no change.");
        }
    }

    [Fact]
    public void AStartSaveOfAnUnknownMissionRefusesWithItsReason()
    {
        var data = RetailGame.Instance.Data;
        var file = Path.Combine(Directory.CreateDirectory(folder).FullName, "start.fl");
        var home = HomeBase(data.Items);
        File.WriteAllText(file, $"[Player]\r\nsystem = {home.System}\r\nbase = {home.Base}\r\n\r\n[StoryInfo]\r\nMission = ol_mission\r\n");

        var problem = PlaySave.Check(data.Items, new PlayStart.FromSave(file, null), null);

        Assert.Equal("The start save names the mission 'ol_mission', which the storyline of the game data does not have.",
            problem);
    }

    /// <summary>
    /// Writes the start save of a plan, checks it, and loads it on a new headless server with a mission observer, as
    /// the play host does. Gives the names of the triggers that reported a change, when each wanted trigger reported a
    /// change or after <see cref="ChangeLimit"/>.
    /// </summary>
    private HashSet<string> Load(GameDataManager data, StartPlan plan, IReadOnlyList<string> wanted)
    {
        Assert.True(plan.CanStart, plan.Reason);
        var file = Path.Combine(Directory.CreateDirectory(folder).FullName, "start.fl");
        File.WriteAllText(file, StartSaveText.OpenLancer(plan, Place(data.Items, plan.Place), DateTime.Now));
        var start = new PlayStart.FromSave(file, null);
        Assert.Null(PlaySave.Check(data.Items, start, null));
        using var headless = new HeadlessServer(data);
        var observer = new MissionObserver();
        headless.Server.MissionObserver = observer;
        headless.Start(PlaySave.Create(headless.Server, start, null));

        var changes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loaded = false;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < ChangeLimit)
        {
            foreach (var line in observer.Take())
            {
                loaded |= line.Reset && line.Mission.Equals(plan.Slot!.Controller, StringComparison.OrdinalIgnoreCase);
                changes.UnionWith(line.Changes.Select(c => c.Trigger));
            }
            if (loaded && wanted.All(changes.Contains))
                break;
            Thread.Sleep(20);
        }

        Assert.True(loaded, $"{plan.Slot!.Slot}: the server did not load the mission.");
        return changes;
    }

    /// <summary>The place of the start save: the place of the plan, or the home base of a new character for the Scene
    /// camera, which a headless test does not have.</summary>
    private static StartSavePlace Place(GameItemDb items, StartPlace place) => place.Kind switch
    {
        StartPlaceKind.Space => new StartSavePlace(place.System!, null, place.Position, place.Orientation),
        StartPlaceKind.Base when items.Bases.Get(place.Base!) is { System: { } system } dock =>
            StartSavePlace.AtBase(system, dock.Nickname),
        _ => HomeBase(items),
    };

    private static StartSavePlace HomeBase(GameItemDb items)
    {
        var dock = items.Bases.Get(items.Ini.NewCharDB.Factions[0].Base)!;
        return StartSavePlace.AtBase(dock.System!, dock.Nickname);
    }
}

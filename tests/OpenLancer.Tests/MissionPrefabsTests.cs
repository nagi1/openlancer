using System.Linq;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The behavior prefabs and their matcher (sections 8 and 14.5 of the M6a design), the acts (section 6.4) and the
/// completeness reports (section 13.5), on synthetic scripts.
/// </summary>
public sealed class MissionPrefabsTests
{
    private static MissionPrefabResult Match(string text) => MissionPrefabs.Retail.Match(Index(text));

    [Fact]
    public void TheCatalogHasEachPrefabOfTheDesign()
    {
        var names = MissionPrefabs.Retail.Definitions.Select(d => d.Name).ToList();

        Assert.Equal(["Checkpoint", "Offer mission", "Play space cutscene", "Branch", "Ambush", "Spawn wave",
            "Destroy targets", "Conversation", "Mission success", "Mission fail", "Keep applying", "Set reputation",
            "Lock or unlock gate", "Objective", "Watched state", "Escort", "Patrol"], names);
        Assert.Equal(10, MissionPrefabs.Retail.Events.Count);
    }

    [Fact]
    public void ASpawnTriggerWithACheckpointIsACheckpoint()
    {
        var result = Match("""
            [Trigger]
            nickname = t1
            Cnd_True = no_params
            Act_SpawnShip = ship1
            Act_Save = t2

            [Trigger]
            nickname = t2
            Cnd_SpaceEnter = sys_a
            Act_SpawnShip = ship1

            [MsnShip]
            nickname = ship1
            """);

        var checkpoint = Assert.Single(result.OfPrefab("checkpoint"));
        Assert.Equal("t1", checkpoint.Core!.Nickname);
        Assert.Equal("t2", Assert.Single(checkpoint.Role("restore")).Nickname);
        // The restore trigger is claimed, so no spawn wave holds it.
        Assert.Empty(result.OfPrefab("spawnWave"));
    }

    [Fact]
    public void ATriggerIsInAtMostOneInstanceAndTwoRunsGiveTheSameResult()
    {
        var text = """
            [Trigger]
            nickname = spawn1
            Cnd_True = no_params
            Act_SpawnShip = ship1

            [Trigger]
            nickname = spawn2
            Cnd_True = no_params
            Act_SpawnFormation = form1

            [Trigger]
            nickname = end1
            Cnd_Destroyed = wing, -1

            [MsnShip]
            nickname = ship1
            label = wing

            [MsnFormation]
            nickname = form1
            label = wing
            """;
        var first = Match(text);
        var second = Match(text);

        var claims = first.Instances.SelectMany(i => i.Claimed).ToList();
        Assert.Equal(claims.Count, claims.Distinct().Count());
        Assert.Equal(first.Instances.Select(i => i.Key), second.Instances.Select(i => i.Key));
        // The shared end trigger counts for the step of the later spawn wave too.
        var waves = first.OfPrefab("spawnWave").ToList();
        Assert.Equal(2, waves.Count);
        Assert.All(waves, w => Assert.Equal("end1", Assert.Single(w.Role("end")).Nickname));
        Assert.Contains(waves[0].Claimed, t => t.Nickname == "end1");
        Assert.DoesNotContain(waves[1].Claimed, t => t.Nickname == "end1");
    }

    [Fact]
    public void OneBranchForOneWatchedTrigger()
    {
        var result = Match("""
            [Trigger]
            nickname = state1
            Cnd_Timer = 5

            [Trigger]
            nickname = wt_on
            Cnd_WatchTrigger = state1, ON
            Act_DeactTrig = wt_off

            [Trigger]
            nickname = wt_on2
            Cnd_WatchTrigger = state1, ON
            Act_DeactTrig = wt_off

            [Trigger]
            nickname = wt_off
            Cnd_WatchTrigger = state1, OFF
            Act_DeactTrig = wt_on
            """);

        var branch = Assert.Single(result.OfPrefab("branch"));
        Assert.Equal("state1", branch.Core!.Nickname);
        Assert.Equal(2, branch.Role("on").Count);
        Assert.Single(branch.Role("off"));
        // The branch does not claim the watched trigger, so it stays a watched state.
        Assert.Equal("state1", Assert.Single(result.OfPrefab("watchedState")).Core!.Nickname);
    }

    [Fact]
    public void EachBeatPrefabMatchesItsPattern()
    {
        var result = Match("""
            [Trigger]
            nickname = offer
            Cnd_LocEnter = Bar, base_a
            Act_AddRTC = missions\\x\\offer_scene.ini

            [Trigger]
            nickname = accept
            Cnd_MsnResponse = accept

            [Trigger]
            nickname = decline
            Cnd_MsnResponse = reject

            [Trigger]
            nickname = clip
            Cnd_True = no_params
            Act_CallThorn = missions\\x\\clip.thn, ship1
            Act_ActTrig = clip_end

            [Trigger]
            nickname = clip_end
            Cnd_Timer = 3
            Act_RevertCam

            [MsnRandEnc]
            nickname = enc1

            [Trigger]
            nickname = arm
            Cnd_True = no_params
            Act_EnableEnc = enc1

            [Trigger]
            nickname = mark
            Cnd_True = no_params
            Act_MarkObj = target1, 1

            [Trigger]
            nickname = marked_dead
            Cnd_Destroyed = target1

            [Trigger]
            nickname = talk1
            Cnd_True = no_params
            Act_SendComm = Player, Player, line1
            Act_ActTrig = talk2

            [Trigger]
            nickname = talk2
            Cnd_CommComplete = line1
            Act_SendComm = Player, Player, line2

            [Trigger]
            nickname = win
            Cnd_True = no_params
            Act_ChangeState = SUCCEED

            [Trigger]
            nickname = lose
            Cnd_True = no_params
            Act_ChangeState = FAIL, 100

            [Trigger]
            nickname = keep
            repeatable = true
            Cnd_True = no_params
            Act_SetVibe = Player, ship1, 1

            [Trigger]
            nickname = sides
            Cnd_Timer = 2
            Act_SetRep = Player, faction_a, 1

            [Trigger]
            nickname = gate
            Cnd_Timer = 2
            Act_LockDock = Player, gate1, unlock

            [Trigger]
            nickname = goal
            Cnd_Timer = 2
            Act_SetNNObj = obj1

            [ObjList]
            nickname = escort_orders
            Follow = Player, 100

            [ObjList]
            nickname = patrol_orders
            GotoVec = goto, 1, 2, 3, 100
            GotoVec = goto, 4, 5, 6, 100
            """);

        Assert.Equal("accept", Assert.Single(result.OfPrefab("offer")).Core!.Nickname);
        Assert.Equal("offer", Assert.Single(Assert.Single(result.OfPrefab("offer")).Role("scene")).Nickname);
        Assert.Equal("clip_end", Assert.Single(Assert.Single(result.OfPrefab("spaceCutscene")).Role("revert")).Nickname);
        Assert.Equal("arm", Assert.Single(Assert.Single(result.OfPrefab("ambush")).Role("armedBy")).Nickname);
        Assert.Equal("marked_dead", Assert.Single(Assert.Single(result.OfPrefab("destroyTargets")).Role("done")).Nickname);
        Assert.Equal("talk2", Assert.Single(Assert.Single(result.OfPrefab("conversation")).Role("chain")).Nickname);
        Assert.Equal("win", Assert.Single(result.OfPrefab("success")).Core!.Nickname);
        Assert.Equal("lose", Assert.Single(result.OfPrefab("fail")).Core!.Nickname);
        Assert.Equal("keep", Assert.Single(result.OfPrefab("keepApplying")).Core!.Nickname);
        Assert.Equal("sides", Assert.Single(result.OfPrefab("setReputation")).Core!.Nickname);
        Assert.Equal("gate", Assert.Single(result.OfPrefab("lockGate")).Core!.Nickname);
        Assert.Equal("goal", Assert.Single(result.OfPrefab("objective")).Core!.Nickname);
        Assert.Equal("escort_orders", Assert.Single(result.OfPrefab("escort")).CoreSection!.Nickname);
        Assert.Equal("patrol_orders", Assert.Single(result.OfPrefab("patrol")).CoreSection!.Nickname);
    }

    [Fact]
    public void TheFieldsOfAnInstanceReadTheirValues()
    {
        var result = Match("""
            [Trigger]
            nickname = spawn1
            Cnd_Timer = 5
            Act_SpawnShip = ship1
            Act_SpawnFormation = form1
            Act_SetVibe = ship1, Player, -1

            [MsnShip]
            nickname = ship1

            [MsnFormation]
            nickname = form1
            """);

        var wave = Assert.Single(result.OfPrefab("spawnWave"));
        var ships = wave.Definition.Fields.Single(f => f.Id == "ships");
        Assert.Equal(["ship1", "form1"], wave.Values(ships).Select(v => v.Text));
        var when = wave.Definition.Fields.Single(f => f.Id == "spawnWhen");
        Assert.Equal("Starts 5 s after it becomes active.", Assert.Single(wave.Values(when)).Text);
    }

    [Fact]
    public void TheNamedEventOfATrigger()
    {
        var index = Index("[Trigger]\nnickname = t1\nCnd_DistVec = inside, Player, 0, 0, 0, 100\n" +
                          "\n[Trigger]\nnickname = t2\nCnd_SystemEnter = sys_a\n");

        Assert.Equal("enterArea", MissionPrefabs.Retail.EventOf(index.Triggers[0])!.Id);
        Assert.Equal("firstEnterSystem", MissionPrefabs.Retail.EventOf(index.Triggers[1])!.Id);
    }

    [Fact]
    public void ActsStartAtCheckpointsAndAtSystemChanges()
    {
        var acts = MissionActs.Propose(Index("""
            [Trigger]
            nickname = a1
            InitState = ACTIVE
            system = sys_a
            Cnd_True = no_params
            Act_ActTrig = a2
            Act_ActTrig = side

            [Trigger]
            nickname = side
            Cnd_Timer = 1

            [Trigger]
            nickname = a2
            system = sys_a
            Cnd_True = no_params
            Act_Save = a3
            Act_ActTrig = a3

            [Trigger]
            nickname = a3
            system = sys_b
            Cnd_True = no_params
            Act_ChangeState = SUCCEED

            [Trigger]
            nickname = lonely
            Cnd_True = no_params
            """));

        Assert.Equal(["a1", "a2", "a3"], acts.MainPath.Select(t => t.Nickname));
        Assert.Equal(3, acts.Acts.Count);
        Assert.Equal("sys_a", acts.Acts[0].System);
        // A trigger off the main path takes the act of its nearest main path trigger above it.
        Assert.Contains(acts.Acts[0].Triggers, t => t.Nickname == "side");
        Assert.Equal(1, acts.Acts[1].Checkpoints);
        Assert.Equal("lonely", Assert.Single(acts.NotStarted).Nickname);
    }

    [Fact]
    public void TheStepsOfTheMissionAndOfAnInstance()
    {
        using var folder = new MissionFolder();
        folder.Write(@"DATA\MISSIONS\M01A\m01a.ini", Story + """

            [Trigger]
            nickname = t_clip
            Cnd_True = no_params
            Act_CallThorn = missions\\clip.thn
            """);
        var analysis = MissionRun.Execute(folder.Inputs()).Analyses.Values.Single();

        var mission = MissionCompleteness.OfMission(analysis);
        Assert.Equal(7, mission.Total);
        // The script is base game, so its missing steps are hidden and count as done.
        Assert.True(mission.Complete);
        Assert.Equal(2, mission.HiddenCount);
        Assert.Equal("All 7 steps complete.", mission.Summary);
        Assert.Equal("2 base-game messages are hidden.", mission.HiddenLine);

        var clip = analysis.Prefabs!.OfPrefab("spaceCutscene").Single();
        var report = MissionCompleteness.OfInstance(analysis, clip);
        Assert.Equal(["Cutscene file", "Anchor", "Camera back"], report.Steps.Select(s => s.Name));
        Assert.Equal(CompletenessState.Hidden, report.Steps[2].State);
    }

    [Fact]
    public void AStepThatAModBreaksIsMissing()
    {
        const string script = @"DATA\MISSIONS\M01A\m01a.ini";
        using var baseFolder = new MissionFolder();
        baseFolder.Write(script, Story);
        using var folder = new MissionFolder();
        folder.Write(script, Story.Replace("Act_ChangeState = SUCCEED", "Act_ChangeState = FAIL"));
        var ledger = new TestLedger();
        ledger.Change(script, "mod1");

        var analysis = MissionRun.Execute(folder.Inputs(baseFolder, ledger, "mod1")).Analyses.Values.Single();
        var mission = MissionCompleteness.OfMission(analysis);

        var success = mission.Steps.Single(s => s.Name == "Mission success");
        Assert.Equal(CompletenessState.Missing, success.State);
        Assert.Equal("5 of 7 steps complete", mission.Summary);
    }
}

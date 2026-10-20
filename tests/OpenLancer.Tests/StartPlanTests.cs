using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The start plan of a play from a trigger (section 5.3 of the M6c design, build step 2.2): the replay, the active set,
/// the story state, the start place and the setup triggers. The scripts are synthetic, and all names are invented.
/// </summary>
public sealed class StartPlanTests
{
    /// <summary>A slot of the tests: start stage 4, accept stage 5.</summary>
    internal static readonly StorySlot Slot = new("M99", "Mission_99", @"MISSIONS\TEST\test.ini", 4, 5,
        new NextMission(NextMissionKind.AtOnce, 0), 0, 0, []);

    /// <summary>
    /// The offer at a base, the accept, the launch with two spawns, a kill that a later trigger waits for, a base scene
    /// and the chosen timer. A checkpoint of the launch restores at the next launch with a place line. A fail watcher
    /// waits beside the kill. One timer has no path.
    /// </summary>
    internal const string Script = """
        [Mission]

        [MsnShip]
        nickname = ol_ship_a
        system = ol_sys_a
        position = 100, 0, 200

        [MsnShip]
        nickname = ol_ship_c
        system = ol_sys_a
        position = 300, 0, 400

        [NNObjective]
        nickname = ol_obj_1
        type = ids, 500100

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = t_offer

        [Trigger]
        nickname = t_offer
        Cnd_BaseEnter = ol_base_a
        Act_SetTitle = 500001
        Act_SendComm = Player, Player, ol_line_1
        Act_ActTrig = t_accept

        [Trigger]
        nickname = t_accept
        Cnd_MsnResponse = accept
        Act_SetRep = Player, ol_faction_a, -0.9
        Act_ActTrig = t_launch

        [Trigger]
        nickname = t_launch
        Cnd_SpaceEnter = ol_sys_a
        Act_SpawnShip = ol_ship_a
        Act_SpawnShip = ol_ship_c
        Act_PlayMusic = ol_music_1
        Act_ActTrig = t_kill
        Act_ActTrig = t_fail
        Act_Save = t_restore, 1

        [Trigger]
        nickname = t_kill
        Cnd_Destroyed = ol_ship_a
        Act_PlayMusic = ol_music_2
        Act_SetNNObj = ol_obj_1
        Act_AddRTC = missions\ol\ol_scene.ini
        Act_AddRTC = missions\ol\ol_other.ini
        Act_ActTrig = t_scene

        [Trigger]
        nickname = t_scene
        Cnd_RTCDone = missions\ol\ol_scene.ini
        Act_ActTrig = t_chosen

        [Trigger]
        nickname = t_fail
        Cnd_Destroyed = ol_ship_c
        Act_ChangeState = FAIL, 1

        [Trigger]
        nickname = t_chosen
        Cnd_Timer = 5
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_restore
        Cnd_PlayerLaunch = no_params
        Act_SetInitialPlayerPos = 10, 20, 30, 1, 0, 0, 0
        Act_ActTrig = t_fail

        [Trigger]
        nickname = t_orphan
        Cnd_Timer = 2
        """;

    private static StartPlan Plan(string text, string? trigger, StartPlaceChoice place = StartPlaceChoice.Suggested,
        string? dock = null) =>
        StartPlan.Make(Index(text), Slot, trigger == null ? MissionStartPoint.MissionStart : MissionStartPoint.At(trigger),
            place, dock);

    /// <summary>The lines of a setup trigger as text.</summary>
    private static List<string> Lines(StartPlan plan, int setup) =>
        plan.Setup[setup].Lines.Select(l => $"{l.Key} = {string.Join(", ", l.Values)}").ToList();

    [Fact]
    public void AStartTriggerGivesAMissionStartWithoutATriggerSave()
    {
        var plan = Plan(Script, "t_start");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.MissionStart, plan.Kind);
        Assert.Empty(plan.SavedTriggers);
        Assert.Empty(plan.Setup);
        Assert.Equal(["t_offer"], plan.ActiveSet);
        Assert.Equal((4, false), (plan.Stage, plan.Accepted));
        Assert.Equal("Mission start (t_start)", plan.StartName);
    }

    [Fact]
    public void TheMissionStartGivesTheStartSet()
    {
        var plan = Plan(Script, null);

        Assert.Equal((StartKind.MissionStart, "Mission start"), (plan.Kind, plan.StartName));
        Assert.Equal(["t_offer"], plan.ActiveSet);
        Assert.Empty(plan.SavedTriggers);
    }

    [Fact]
    public void ARestoreTriggerGivesACheckpointStartWithOneTriggerSave()
    {
        var plan = Plan(Script, "t_restore");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.Checkpoint, plan.Kind);
        Assert.Equal(["t_restore"], plan.SavedTriggers);
        Assert.Empty(plan.Setup);
        Assert.Equal("Checkpoint t_restore", plan.StartName);
        // The story path reaches the checkpoint only through its Act_Save line, after the accept.
        Assert.Equal((5, true), (plan.Stage, plan.Accepted));
    }

    [Fact]
    public void ARestoreTriggerWithActSetInitialPlayerPosGivesASpaceStartAtItsPoint()
    {
        var place = Plan(Script, "t_restore").Place;

        Assert.Equal(StartPlaceKind.Space, place.Kind);
        Assert.Equal(new Vector3(10, 20, 30), place.Position);
        Assert.Equal(Quaternion.Identity, place.Orientation);
        // The point has no system. The last system condition of the story path gives it.
        Assert.Equal("ol_sys_a", place.System);
        Assert.Equal("Act_SetInitialPlayerPos of t_restore", place.Source);
    }

    [Fact]
    public void ARestoreTriggerWhoseConditionSavesAStateGivesAOneLineSetup()
    {
        var text = Script.Replace("Cnd_PlayerLaunch = no_params", "Cnd_Timer = 10");

        var plan = Plan(text, "t_restore");

        Assert.Equal(StartKind.Checkpoint, plan.Kind);
        var setup = Assert.Single(plan.Setup);
        Assert.Equal("ol_play_setup", setup.Nickname);
        Assert.Equal(["Cnd_True = no_params", "Act_ActTrig = t_restore"], Lines(plan, 0));
        Assert.Equal(["ol_play_setup"], plan.SavedTriggers);
    }

    [Fact]
    public void TheReplayRunsThePathAndStopsWhenTheChosenTriggerStarts()
    {
        var plan = Plan(Script, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Equal(StartKind.Approximate, plan.Kind);
        // The start trigger ran by itself, a heartbeat started the pending offer, and each active trigger of the path
        // ran by force.
        Assert.Equal(["t_start", "t_offer", "t_accept", "t_launch", "t_kill", "t_scene", "t_chosen"], plan.Path);
        // The fail watcher waits beside the chosen trigger.
        Assert.Equal(["t_fail", "t_chosen"], plan.ActiveSet);
        Assert.Equal("t_chosen (approximate)", plan.StartName);
    }

    [Fact]
    public void AnApproximateStartHoldsOnlyWorldAndStateActionsInReplayOrder()
    {
        var plan = Plan(Script, "t_chosen");

        Assert.Equal(2, plan.Setup.Count);
        // The state lines and the starts of the whole active set. The comm line, the starts and the save do not go.
        Assert.Equal(
        [
            "Cnd_True = no_params", "Act_SetRep = Player, ol_faction_a, -0.9", "Act_SetNNObj = ol_obj_1",
            "Act_ActTrig = ol_play_world", "Act_ActTrig = t_fail", "Act_ActTrig = t_chosen",
        ], Lines(plan, 0));
        // The world waits for space. The spawn of the ship that the path destroys goes, and only the last music stays.
        Assert.Equal("ol_play_world", plan.Setup[1].Nickname);
        Assert.Equal(["Cnd_SpaceEnter = no_params", "Act_SpawnShip = ol_ship_c", "Act_PlayMusic = ol_music_2"],
            Lines(plan, 1));
        Assert.Equal(["ol_play_setup"], plan.SavedTriggers);
    }

    [Fact]
    public void TheSaveKeysAreTheNetResultWithoutASceneThatThePathPlayed()
    {
        var state = Plan(Script, "t_chosen").State;

        Assert.Equal(500001, state.Title);
        Assert.Equal(0, state.Offer);
        // The path waits for the end of ol_scene, so it does not play again.
        Assert.Equal([(@"missions\ol\ol_other.ini", false)], state.Scenes);
    }

    [Fact]
    public void AnAcceptOnTheStoryPathGivesTheAcceptStageAndMissionAccepted()
    {
        Assert.Equal((5, true), (Plan(Script, "t_chosen").Stage, Plan(Script, "t_chosen").Accepted));
        // The accept trigger itself waits for the accept.
        Assert.Equal((4, false), (Plan(Script, "t_accept").Stage, Plan(Script, "t_accept").Accepted));
    }

    [Fact]
    public void TheLastPlaceConditionOfTheStoryPathGivesASpaceStartNearTheLastSpawn()
    {
        var place = Plan(Script, "t_chosen").Place;

        Assert.Equal(StartPlaceKind.Space, place.Kind);
        Assert.Equal("ol_sys_a", place.System);
        Assert.Equal(new Vector3(300, 0, 400 + StartPlaces.Offset), place.Position);
        Assert.Equal("Cnd_SpaceEnter of t_launch", place.Source);
    }

    [Fact]
    public void CndSpaceEnterWithNoParamsGivesNoPlaceAndTheSearchGoesUpTheStoryPath()
    {
        var text = Script.Replace("Cnd_SpaceEnter = ol_sys_a", "Cnd_SpaceEnter = no_params");

        var place = Plan(text, "t_chosen").Place;

        Assert.Equal(StartPlaceKind.Base, place.Kind);
        Assert.Equal("ol_base_a", place.Base);
        Assert.Equal("Cnd_BaseEnter of t_offer", place.Source);
    }

    [Fact]
    public void ABaseStartRebuildsTheWorldAtTheLaunch()
    {
        var plan = Plan(Script, "t_chosen", StartPlaceChoice.Base, "ol_base_b");

        Assert.Equal((StartPlaceKind.Base, "ol_base_b"), (plan.Place.Kind, plan.Place.Base));
        Assert.Equal("Cnd_PlayerLaunch = no_params", Lines(plan, 1)[0]);
    }

    [Fact]
    public void TheCameraChoiceGivesTheCamera()
    {
        Assert.Equal(StartPlaceKind.Camera, Plan(Script, "t_chosen", StartPlaceChoice.Camera).Place.Kind);
    }

    [Fact]
    public void AChosenTriggerWithCndTrueIsInTheActiveSet()
    {
        var text = Script.Replace("""
            nickname = t_chosen
            Cnd_Timer = 5
            """, """
            nickname = t_chosen
            Cnd_True = no_params
            """);

        var plan = Plan(text, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Contains("t_chosen", plan.ActiveSet);
        Assert.Contains("Act_ActTrig = t_chosen", Lines(plan, 0));
    }

    [Fact]
    public void ATriggerThatAStopOnAnotherBranchRemovesRefusesWithItsReason()
    {
        const string text = """
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_branch
            Act_ActTrig = s_mid

            [Trigger]
            nickname = s_branch
            Cnd_True = no_params
            Act_DeactTrig = s_mid

            [Trigger]
            nickname = s_mid
            Cnd_Timer = 3
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 3
            """;

        var plan = Plan(text, "s_chosen");

        Assert.False(plan.CanStart);
        Assert.Equal(StartRefusal.NotActive, plan.Refusal);
        Assert.Equal("Trigger 's_mid' on the path to 's_chosen' is not active at that point, because another trigger " +
                     "stops it first. Pick another trigger.", plan.Reason);
    }

    [Fact]
    public void APathThatPassesATriggerWithoutAConditionRefuses()
    {
        const string text = """
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_empty

            [Trigger]
            nickname = s_empty
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 3
            """;

        var plan = Plan(text, "s_chosen");

        Assert.Equal(StartRefusal.NoCondition, plan.Refusal);
        Assert.Equal("The path to trigger 's_chosen' passes trigger 's_empty', which has no condition. The game stops at " +
                     "that trigger. Add a condition to it.", plan.Reason);
    }

    [Fact]
    public void AnEmptyPathGivesOnlyTheChosenTriggerAndTheWarning()
    {
        var plan = Plan(Script, "t_orphan");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.Approximate, plan.Kind);
        Assert.Equal(["t_orphan"], plan.ActiveSet);
        Assert.Empty(plan.Path);
        Assert.Contains(StartPlan.NoPathWarning("t_orphan"), plan.Warnings);
        Assert.Single(plan.Setup);
        Assert.Equal(["Cnd_True = no_params", "Act_ActTrig = t_orphan"], Lines(plan, 0));
        // Without a path, nothing names a place.
        Assert.Equal(StartPlaceKind.Camera, plan.Place.Kind);
    }

    [Fact]
    public void ASetupNameThatTheScriptHasByNameOrByGameIdGetsASuffix()
    {
        var byName = Script + """


            [Trigger]
            nickname = OL_PLAY_SETUP
            Cnd_Timer = 1
            """;
        Assert.Equal("ol_play_setup_2", Plan(byName, "t_chosen").Setup[0].Nickname);

        // Two names with one game id: the game keeps only the first trigger of the id (TR4).
        var (first, second) = SameId();
        var byId = Script + $"""


            [Trigger]
            nickname = {first}
            Cnd_Timer = 1
            """;
        Assert.Equal($"{second}_2", StartSetup.FreeName(Index(byId), second, []));
        Assert.Equal("ol_free", StartSetup.FreeName(Index(byId), "ol_free", []));
        Assert.Equal("ol_free_2", StartSetup.FreeName(Index(byId), "ol_free", ["ol_free"]));
    }

    /// <summary>Two names of trigger form with one game id, found by a search.</summary>
    private static (string First, string Second) SameId()
    {
        var seen = new Dictionary<uint, string>();
        for (var i = 0; ; i++)
        {
            var name = $"ol_t{i}";
            if (seen.TryGetValue(FLHash.CreateID(name), out var other))
                return (other, name);
            seen[FLHash.CreateID(name)] = name;
        }
    }

    [Fact]
    public void TheSetupPassesTheMissionChecksWithoutAnError()
    {
        var plan = Plan(Script, "t_chosen");
        var document = Document(Script + "\r\n\r\n" + plan.SetupPatch.Replace("ol.new = true\r\n", ""));

        var errors = MissionChecks.Run(MissionIndex.Build(document, MissionSchema.Retail, MissionWorld.Empty,
                FullCoverage.Instance))
            .Where(p => p.Severity == DiagnosticSeverity.Error);

        Assert.Empty(errors);
    }

    [Fact]
    public void ASetupLineWithANewErrorRefusesTheStartWithItsMessage()
    {
        var index = Index(Script);
        var plan = Plan(Script, "t_chosen");
        var broken = plan with { Setup = [new SetupTrigger("ol_play_setup", [("Cnd_True", ["no_params"]), ("Act_SetNNObj", ["ol_obj_none"])])] };

        var checkedPlan = StartSetup.Check(broken, index);

        Assert.Equal(StartRefusal.SetupProblem, checkedPlan.Refusal);
        Assert.StartsWith("A setup trigger has a problem: ", checkedPlan.Reason);
        Assert.Contains("ol_obj_none", checkedPlan.Reason);
    }

    [Fact]
    public void ASetupLineWithTheErrorOfItsSourceLineDoesNotRefuse()
    {
        // The objective of the path does not exist. The game ignores the line and its copy in the same way.
        var text = Script.Replace("nickname = ol_obj_1", "nickname = ol_obj_other");

        var plan = Plan(text, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Contains("Act_SetNNObj = ol_obj_1", Lines(plan, 0));
    }

    [Fact]
    public void AScriptWithoutASlotRefuses()
    {
        var plan = StartPlan.Make(Index(Script), null, MissionStartPoint.At("t_chosen"));

        Assert.Equal((StartKind.TestScript, StartRefusal.NoSlot), (plan.Kind, plan.Refusal));
        Assert.Equal(StartPlan.NoSlotReason, plan.Reason);
    }

    [Fact]
    public void AMissingTriggerRefuses()
    {
        Assert.Equal(StartRefusal.NoTrigger, Plan(Script, "t_none").Refusal);
    }

    [Fact]
    public void TheSetupPatchHasTheTwoNewSectionsAtTheEnd()
    {
        var patch = Plan(Script, "t_chosen").SetupPatch;

        Assert.StartsWith("[Trigger]\r\nnickname = ol_play_setup\r\nol.new = true\r\nCnd_True = no_params\r\n", patch);
        Assert.Contains("\r\n\r\n[Trigger]\r\nnickname = ol_play_world\r\nol.new = true\r\nCnd_SpaceEnter = no_params\r\n",
            patch);
    }

    [Fact]
    public void ThePlanIsTheSameForTheSameIndex()
    {
        var index = Index(Script);
        var a = StartPlan.Make(index, Slot, MissionStartPoint.At("t_chosen"));
        var b = StartPlan.Make(index, Slot, MissionStartPoint.At("T_CHOSEN"));

        Assert.Equal(a.ActiveSet, b.ActiveSet);
        Assert.Equal(a.SetupPatch, b.SetupPatch);
        Assert.Equal(a.Start, b.Start);
    }

    [Fact]
    public void AStartPointFollowsARenameWithoutLetterCase()
    {
        var start = MissionStartPoint.At("t_chosen");

        Assert.Equal(MissionStartPoint.At("t_new"), start.Renamed("T_CHOSEN", "t_new"));
        Assert.Same(start, start.Renamed("t_other", "t_new"));
        Assert.Same(MissionStartPoint.MissionStart, MissionStartPoint.MissionStart.Renamed("t_chosen", "t_new"));
        Assert.Throws<ArgumentException>(() => MissionStartPoint.At(" "));
    }
}

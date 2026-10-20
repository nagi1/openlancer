using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>Deletes, renames and references (section 4.10 of the M6b design, decisions D81 and D668).</summary>
public sealed class MissionReferencesTests
{
    private const string Links = """
        [Mission]

        [Trigger]
        nickname = ol_a
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = ol_b
        Act_ActTrig = ol_c

        [Trigger]
        nickname = ol_b
        Cnd_WatchTrigger = ol_c, ON
        Act_DeactTrig = ol_c
        Act_Save = ol_c, 1
        Act_ActTrig = ol_d

        [Trigger]
        nickname = ol_c
        Cnd_True = no_params
        Act_ChangeState = SUCCEED
        """;

    [Fact]
    public void ARenameOfATriggerChangesEachLineThatNamesItInOneUndoEntry()
    {
        using var m = Create(Links);
        var before = m.State();

        m.Apply(new RenameTrigger(T("ol_c"), "ol_e"));

        Assert.Equal(["nickname = ol_b", "Cnd_WatchTrigger = ol_e, ON", "Act_DeactTrig = ol_e", "Act_Save = ol_e, 1",
            "Act_ActTrig = ol_d"], m.Lines("ol_b"));
        Assert.Equal("Act_ActTrig = ol_e", m.Lines("ol_a")![4]);
        Assert.Equal("Rename ol_c to ol_e", m.Session.UndoLabel);
        m.Session.Undo();
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void ARenameBackChangesExactlyTheRowsThatTheRenameChanged()
    {
        using var m = Create(Links);

        var result = m.Apply(new RenameTrigger(T("ol_c"), "ol_d"));
        Assert.Equal("Act_ActTrig = ol_d", m.Lines("ol_b")![4]);
        m.Apply(result.Inverse!);

        // The line that named ol_d before the rename still names ol_d.
        Assert.Equal(["nickname = ol_b", "Cnd_WatchTrigger = ol_c, ON", "Act_DeactTrig = ol_c", "Act_Save = ol_c, 1",
            "Act_ActTrig = ol_d"], m.Lines("ol_b"));
        Assert.True(m.Has("ol_c"));
    }

    [Fact]
    public void ADeleteWithKeepLeavesMissingLinesAndOneUndoRestoresAll()
    {
        using var m = Create(Links);

        var result = m.Apply(new RemoveTrigger(T("ol_c")));

        Assert.Equal(4, result.Missing.Count);
        var problems = m.Missions.Model(Script).Problems.Where(p => p.Rule == "M05" && p.Message.Contains("'ol_c'")).ToList();
        Assert.Equal(4, problems.Count);
        Assert.All(problems, p => Assert.EndsWith("This script has no such trigger. The game ignores this line. Pick a " +
                                                  "trigger of this script.", p.Message));
        m.Session.Undo();
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule == "M05" && p.Message.Contains("'ol_c'"));
    }

    [Fact]
    public void ADeleteWithRemoveRemovesTheLinesThatNameIt()
    {
        using var m = Create(Links);

        var result = m.Apply(new RemoveTrigger(T("ol_c")) { References = ReferencesMode.Remove });

        Assert.Empty(result.Missing);
        Assert.Equal(["nickname = ol_b", "Act_ActTrig = ol_d"], m.Lines("ol_b"));
        Assert.Equal(["nickname = ol_a", "InitState = ACTIVE", "Cnd_True = no_params", "Act_ActTrig = ol_b"], m.Lines("ol_a"));
    }

    [Fact]
    public void AnM3RenameOfASystemObjectChangesTheMissionLinesInTheSameEntry()
    {
        using var ws = Story();
        var session = ws.Session("act");

        var plan = ScenePlans.Rename(session, SceneWorkspace.Object("Li01_01"), "Li01_station");
        session.Execute(plan.Command!);

        Assert.Equal("Act_LockDock = Player, Li01_station, lock", Line(session, 3));
        Assert.Equal("Rename Li01_01 to Li01_station", session.UndoLabel);
        session.Undo();
        Assert.Equal("Act_LockDock = Player, Li01_01, lock", Line(session, 3));
        Assert.NotNull(session.Get(SceneWorkspace.Object("Li01_01")));
    }

    [Fact]
    public void AnM3DeleteLeavesMissingMissionLines()
    {
        using var ws = Story();
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [SceneWorkspace.Object("Li01_dock")]);

        var missing = Assert.Single(plan.MissionMissing);
        Assert.Equal(("Act_LockDock", true), (missing.Key, missing.BlocksSave));
        Assert.True(plan.NeedsConfirmation);
        Assert.Contains(plan.Report(session).Missing, x => x.Key == "Act_LockDock");
        session.Execute(plan.Command!);
        Assert.Contains(session.Missions.Model(StoryScript).Problems, p => p.Rule == "M06" && p.Message.Contains("Li01_dock"));
    }

    [Fact]
    public void AStrictCheckAfterASceneStepThatAddsAnObjectSeesTheNewObject()
    {
        using var ws = Story();
        var session = ws.Session("act");
        var create = ScenePlans.Create(session, "Li01", SceneTemplate.Solar, "depot", System.Numerics.Vector3.Zero, null);
        Assert.NotNull(create.Command);
        var created = create.Adds[0].Id.Key!;

        var batch = session.BeginBatch("Add a depot and lock it");
        batch.Run(create.Command!);
        var mission = session.Missions.Apply(new MissionBatch("Lock it",
            [new AddAction(new TriggerRef(StoryScript, "ol_lock"), LineSpec.Of("Act_LockDock", ("who", "Player"),
                ("gate", created), ("action", "lock")))]) { Options = MissionEditOptions.Strict });
        var result = batch.Commit();

        Assert.Equal(EditStatus.Applied, mission.Status);
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void ARenameOfAnItemChangesTheLinesThatNameIt()
    {
        using var m = Create();

        m.Apply(new RenameItem(Item("MsnShip", "ol_ship_1"), "ol_ship_a"));
        m.Apply(new RenameItem(Item("NPCShipArch", "ol_build_1", Ships), "ol_build_a"));
        m.Apply(new RenameItem(Item("ObjList", "ol_orders"), "ol_route"));

        Assert.Equal("Act_SpawnShip = ol_ship_a", m.Lines("ol_wave")![3]);
        Assert.Equal("Cnd_Destroyed = ol_ship_a, -1", m.Lines("ol_end")![1]);
        Assert.Equal("npc_ship_arch = ol_build_a", m.Lines(Item("NPC", "ol_npc_1"))![1]);
        Assert.Equal("nickname = ol_route", m.Lines(Item("ObjList", "ol_route"))![0]);
        Assert.Equal("Ship build 'ol_shared_build' is in a shared file that random encounters can use. The editor cannot " +
                     "rename it in a mission.",
            m.Refuse(new RenameItem(Item("NPCShipArch", "ol_shared_build", SharedShips), "x"), MissionErrors.SharedBuildRename)
                .Message);
    }

    [Fact]
    public void TheReferenceProviderFindsTheMissionLinesOfAWorldName()
    {
        using var m = Create();

        var uses = m.Missions.References.Uses(MissionReferences.ObjectKinds, "ol_station");

        var use = Assert.Single(uses);
        Assert.Equal(("Act_LockDock", 2, 1), (use.Key, use.Row, use.Value));
        Assert.Empty(m.Missions.References.Uses(MissionReferences.ObjectKinds, "ol_ship_1"));
        Assert.Single(m.Missions.References.Uses([MissionValueKind.ShipArch], "ol_arch"),
            u => u.Script.EndsWith(@"OL_TEST\npcships.ini", StringComparison.OrdinalIgnoreCase));
    }

    private const string StoryScript = @"DATA\MISSIONS\OL_REF\ol_ref.ini";

    /// <summary>The scene workspace with a script whose lines name objects of Li01.</summary>
    private static TestWorkspace Story()
    {
        var ws = SceneWorkspace.Create();
        ws.WriteBase(StoryScript, """
            [Mission]

            [Trigger]
            nickname = ol_lock
            InitState = ACTIVE
            Cnd_True = no_params
            Act_LockDock = Player, Li01_01, lock
            Act_LockDock = Player, Li01_dock, unlock
            Act_ChangeState = SUCCEED
            """);
        return ws;
    }

    private static string Line(EditSession session, int row)
    {
        var document = session.OpenDocument(StoryScript)!;
        return MissionEdits.Line(document.Working[MissionDraftLines.TriggerIndex(document, "ol_lock")][row]);
    }
}

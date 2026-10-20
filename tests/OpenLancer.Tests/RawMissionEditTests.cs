using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The raw INI edits of mission files (section 8.8 of the M6b design, decision D678).</summary>
public sealed class RawMissionEditTests
{
    private static string Text(MissionEdits m) =>
        RawText.Canonical(Script, m.Session.OpenDocument(Script)!.Working).Text;

    private static RawMissionPlan Plan(MissionEdits m, string start, string end) =>
        RawMissionEdit.PlanFile(m.Session, Script, start, end);

    private static MissionEditResult Run(MissionEdits m, string start, string end)
    {
        var plan = Plan(m, start, end);
        Assert.Empty(plan.Problems);
        var result = m.Missions.Apply(plan.Batch!);
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => e.Message)));
        return result;
    }

    [Fact]
    public void AnUnchangedTextGivesNoBatch()
    {
        using var m = Create();
        var text = Text(m);

        var plan = Plan(m, text, text.Replace("\n", "\r\n"));

        Assert.Null(plan.Batch);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void AChangedBodyANewSectionAndARemovedSectionAreOneUndoEntry()
    {
        using var m = Create();
        var start = Text(m);
        var before = m.State();
        var end = start.Replace("Cnd_Timer = 3", "Cnd_Timer = 8")
            .Replace("[ObjList]\nnickname = ol_orders\nGotoVec = goto_cruise, 0, 0, 1000, 100\n\n", "")
            .Replace("[Trigger]\nnickname = ol_end", "[Trigger]\nnickname = ol_extra\nCnd_True = no_params\n\n[Trigger]\nnickname = ol_end");

        var result = Run(m, start, end);

        Assert.Equal(["system = ol_system", "nickname = ol_wave", "Cnd_Timer = 8", "Act_SpawnShip = ol_ship_1",
            "Act_DeactTrig = ol_end", "Act_ActTrig = ol_end"], m.Lines("ol_wave"));
        Assert.False(m.Has(Item("ObjList", "ol_orders")));
        var document = m.Session.OpenDocument(Script)!;
        Assert.Equal(MissionDraftLines.TriggerIndex(document, "ol_wave") + 1, MissionDraftLines.TriggerIndex(document, "ol_extra"));
        Assert.Equal("Edit INI of ol_test.ini", result.Label);
        Assert.Equal("Edit INI of ol_test.ini", m.Session.UndoLabel);
        m.Session.Undo();
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void ARemovedTriggerLeavesMissingLines()
    {
        using var m = Create();
        var start = Text(m);
        var end = start.Replace("[Trigger]\nnickname = ol_end\nCnd_Destroyed = ol_ship_1, -1\nAct_LockDock = Player, ol_station, lock\nAct_ChangeState = SUCCEED", "");

        var result = Run(m, start, end);

        Assert.Equal(3, m.Missions.Model(Script).Problems.Count(p => p.Rule == "M05"));
        Assert.Equal(3, result.Missing.Count);
    }

    [Fact]
    public void AChangedNicknameStops()
    {
        using var m = Create();
        var start = Text(m);

        var plan = Plan(m, start, start.Replace("nickname = ol_end", "nickname = ol_finish"));

        var problem = Assert.Single(plan.Problems);
        Assert.Equal(RawMissionEdit.NicknameChanged, problem.Message);
        Assert.True(problem.Line > 0);
        Assert.Null(plan.Batch);
    }

    /// <summary>The three-way rule: W of a changed section changed after the start, so the plan stops. A change of W in
    /// another section stays.</summary>
    [Fact]
    public void AChangeOfWAfterTheStartStopsTheEditOfThatSectionOnly()
    {
        using var m = Create();
        var start = Text(m);
        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 5));

        var stop = Plan(m, start, start.Replace("Act_SpawnShip = ol_ship_1", "Act_SpawnShip = ol_ship_2"));
        Run(m, start, start.Replace("Cnd_Destroyed = ol_ship_1, -1", "Cnd_Destroyed = ol_ship_1, 2"));

        Assert.Equal(RawMissionEdit.ChangedWhileTyping, Assert.Single(stop.Problems).Message);
        Assert.Equal("Cnd_Timer = 5", m.Lines("ol_wave")[2]);
        Assert.Equal("Cnd_Destroyed = ol_ship_1, 2", m.Lines("ol_end")[1]);
    }

    /// <summary>A ship moves, because a formation reads the ships before it. A trigger keeps its place, with a
    /// note.</summary>
    [Fact]
    public void TheMoveRules()
    {
        using var m = Create();
        var start = Text(m);
        const string ship = "[MsnShip]\nnickname = ol_ship_3\nnpc = ol_npc_shared\n\n";
        const string end = "[Trigger]\nnickname = ol_end\nCnd_Destroyed = ol_ship_1, -1\nAct_LockDock = Player, ol_station, lock\nAct_ChangeState = SUCCEED";
        var moved = start.Replace(ship, "").Replace("[ObjList]", ship + "[ObjList]");
        var trigger = start.Replace("\n\n" + end, "").Replace("[Trigger]\nnickname = ol_start", end + "\n\n[Trigger]\nnickname = ol_start");

        Run(m, start, moved);
        var plan = Plan(m, Text(m), trigger.Replace(ship, "").Replace("[ObjList]", ship + "[ObjList]"));

        var document = m.Session.OpenDocument(Script)!;
        var form = document.IndexOf(new SectionIdentity("MsnFormation", "ol_form"));
        Assert.Equal(form + 1, document.IndexOf(new SectionIdentity("MsnShip", "ol_ship_3")));
        Assert.Null(plan.Batch);
        Assert.Equal([RawMissionEdit.MoveNote("Trigger")], plan.Notes);
    }

    [Fact]
    public void ACommentInAChangedSectionGivesTheNote()
    {
        using var m = Create();
        var start = Text(m);

        var plan = Plan(m, start, start.Replace("Cnd_Timer = 3", "Cnd_Timer = 4 ; a longer wait"));

        Assert.Equal([RawMissionEdit.CommentNote], plan.Notes);
        Assert.NotNull(plan.Batch);
    }

    [Fact]
    public void TextProblemsStopWithTheirLines()
    {
        using var m = Create();
        var start = Text(m);

        var plan = Plan(m, start, "stray line\n" + start.Replace("[ObjList]", "[Rocket]").Replace("Cnd_Timer = 3", "Cnd_Timer 3"));

        Assert.Equal([1, start.Split('\n').ToList().IndexOf("[ObjList]") + 2,
            start.Split('\n').ToList().IndexOf("Cnd_Timer = 3") + 2], plan.Problems.Select(p => p.Line));
        Assert.Contains("[Rocket] is not a section type of a mission file.", plan.Problems[1].Message);
        Assert.Null(plan.Batch);
    }

    /// <summary>The raw block of one section of the Inspector goes through the mission editor (decision D660).</summary>
    [Fact]
    public void TheRawBlockOfAMissionSectionGivesAMissionBatch()
    {
        using var m = Create();
        var ship = new SectionRef(Script, new SectionIdentity("MsnShip", "ol_ship_2"));
        var start = RawSectionEdit.Format(m.Session.Get(ship)!);

        var plan = RawSectionEdit.Plan(m.Session, ship, start, start + "\nradius = 50");

        Assert.Null(plan.Command);
        var batch = Assert.IsType<MissionBatch>(plan.Mission);
        Assert.Equal("Edit INI of [MsnShip] nickname = ol_ship_2", batch.Label);
        Assert.Equal(EditStatus.Applied, m.Missions.Apply(batch).Status);
        Assert.Equal("radius = 50", m.Lines(Item("MsnShip", "ol_ship_2"))[^1]);
        Assert.Equal(RawMissionEdit.NicknameChanged, Assert.Single(RawSectionEdit.Plan(m.Session, ship, start,
            start.Replace("nickname = ol_ship_2", "nickname = ol_ship_9")).Problems).Message);
    }

    /// <summary>The block of a page with two sections: an edit of the second keeps the first.</summary>
    [Fact]
    public void ABlockOfTwoSectionsChangesOnlyItsSections()
    {
        using var m = Create();
        var document = m.Session.OpenDocument(Script)!;
        var ids = new[] { new SectionIdentity("MsnShip", "ol_ship_1"), new SectionIdentity("NPC", "ol_npc_1") };
        var start = RawText.Canonical(Script, ids.Select(id => document.Working[document.IndexOf(id)]).ToList(), ids: ids).Text;

        var plan = RawMissionEdit.PlanBlock(m.Session, Script, ids, start, start.Replace("voice = ol_voice", "voice = ol_voice_2"));

        var command = Assert.IsType<ReplaceSectionBody>(Assert.Single(plan.Batch!.Commands));
        Assert.Equal(Item("NPC", "ol_npc_1"), command.Section);
    }
}

using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The values of the lines of the mission commands (section 4.3 of the M6b design).</summary>
public sealed class MissionValuesTests
{
    [Fact]
    public void NamedValuesGoToTheirPlacesAndAGapGetsTheDefaultOrTheNoneWord()
    {
        using var m = Create();

        m.Apply(new AddAction(T("ol_wave"), LineSpec.Of("Act_SpawnShip", ("position", MissionValue.Of(1, 2, 3)),
            ("ship", "ol_ship_2"))));
        m.Apply(new SetCondition(T("ol_end"), LineSpec.Of("Cnd_Destroyed", ("target", "ol_form"), ("mode", "SILENT"))));

        Assert.Equal("Act_SpawnShip = ol_ship_2, no_ol, 1, 2, 3", m.Lines("ol_wave")![^1]);
        Assert.Equal("Cnd_Destroyed = ol_form, -1, SILENT", m.Lines("ol_end")![1]);
    }

    [Fact]
    public void AGapWithoutADefaultRefuses()
    {
        using var m = Create();

        var gap = m.Refuse(new SetCondition(T("ol_wave"), LineSpec.Of("Cnd_Timer", ("seconds", 2), ("mode", "RUN_ON_BASE"))),
            MissionErrors.ValueMissing);
        var required = m.Refuse(new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig")), MissionErrors.ValueMissing);

        Assert.Equal("Longest delay needs a value, because a later value is set. Give Longest delay a value.", gap.Message);
        Assert.Equal("Trigger needs a value. Give Trigger a value.", required.Message);
    }

    [Fact]
    public void AWordGetsTheLetterCaseOfItsSetWithANote()
    {
        using var m = Create();

        var result = m.Apply(new SetConditionValue(T("ol_end"), "mode", "explode"));

        Assert.Equal("Cnd_Destroyed = ol_ship_1, -1, EXPLODE", m.Lines("ol_end")![1]);
        Assert.Equal(["'explode' became 'EXPLODE', the letter case that the game reads."], result.Notes);
        Assert.Equal("'WIN' is not a word of Result. Use one of these words: FAIL, SUCCEED, NO_STATE.",
            m.Refuse(new SetActionValue(Row("ol_end", 3), "state", "WIN"), MissionErrors.WordNotInSet).Message);
    }

    [Fact]
    public void AWholeNumberANumberAndAPositionCheckTheirKind()
    {
        using var m = Create();

        Assert.Equal("The game reads only the whole part of Count. Use a whole number.",
            m.Refuse(new SetConditionValue(T("ol_end"), "count", 1.5), MissionErrors.WholeNumber).Message);
        Assert.Equal("'soon' is not a number. Delay needs a number.",
            m.Refuse(new SetConditionValue(T("ol_wave"), "seconds", "soon"), MissionErrors.ValueKind).Message);
        Assert.Equal("'true' is not a number. Delay needs a number.",
            m.Refuse(new SetConditionValue(T("ol_wave"), "seconds", true), MissionErrors.ValueKind).Message);
        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", "4.5"));
        Assert.Equal("Cnd_Timer = 4.5", m.Lines("ol_wave")![2]);
    }

    [Fact]
    public void AValueThatAPatchLineCannotHoldRefuses()
    {
        using var m = Create();

        var comma = m.Refuse(new RetargetLink(Row("ol_start", 3), "a,b"), MissionErrors.PatchText);
        m.Refuse(new SetConditionValue(T("ol_end"), "target", "a;b"), MissionErrors.PatchText);

        Assert.Equal("A value cannot hold a comma or a semicolon. The game splits or cuts the line there. Remove the " +
                     "character.", comma.Message);
    }

    [Fact]
    public void AValueOfABinaryScriptKeepsTheTypeOfTheLineOfB()
    {
        using var m = Create(Text.Replace("Cnd_Timer = 3", "Cnd_Timer = 3.5"), binary: true);

        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 4));

        var document = m.Session.OpenDocument(Script)!;
        var wave = document.Working.Single(s => SectionIdentityKey(s) == "ol_wave");
        Assert.IsType<SingleValue>(wave[2][0]);
        Assert.Equal(4f, (float)(SingleValue)wave[2][0]);
        var saved = m.Session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
    }

    [Fact]
    public void ValuesByPositionGetOnlyThePatchChecks()
    {
        using var m = Create();

        m.Apply(new SetActionLine(Row("ol_end", 3), LineSpec.Raw("Act_ChangeState", "succeed", "x")));

        Assert.Equal("Act_ChangeState = succeed, x", m.Lines("ol_end")![3]);
    }

    private static string? SectionIdentityKey(Section section) => OpenLancer.Data.Missions.TriggerRules.Nickname(section);
}

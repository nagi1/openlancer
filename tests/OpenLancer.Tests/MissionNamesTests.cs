using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The names of the mission commands (section 4.3 of the M6b design, decision D683).</summary>
public sealed class MissionNamesTests
{
    [Fact]
    public void AMadeNameIsTheScriptStemTheKindAndTheNextFreeNumber()
    {
        using var m = Create();

        m.Apply(new AddTrigger(TestScript) { Nickname = "ol_test_trigger_1" });
        var made = m.Apply(new AddTrigger(TestScript));
        var npc = m.Apply(new AddItem(TestScript, "npc"));
        var encounter = m.Apply(new AddItem(TestScript, "random_encounter"));

        Assert.Equal(T("ol_test_trigger_2").ToString(), made.Select);
        Assert.Equal(Item("NPC", "ol_test_npc_1").ToString(), npc.Select);
        Assert.Equal(Item("MsnRandEnc", "ol_test_encounter_1").ToString(), encounter.Select);
    }

    [Fact]
    public void ANameHasAtMost47CharactersAndARandomEncounterAtMost31()
    {
        using var m = Create();

        m.Apply(new AddTrigger(TestScript) { Nickname = new string('a', 47) });
        m.Refuse(new AddTrigger(TestScript) { Nickname = new string('b', 48) }, MissionErrors.NameInvalid);
        m.Apply(new AddItem(TestScript, "random_encounter") { Nickname = new string('c', 31) });
        var error = m.Refuse(new AddItem(TestScript, "random_encounter") { Nickname = new string('d', 32) },
            MissionErrors.NameInvalid);

        Assert.Equal($"'{new string('d', 32)}' is not a valid name. Use letters, digits, '_' and '-', with at most 31 " +
                     "characters. A name cannot be a number.", error.Message);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("-3")]
    [InlineData("1e5")]
    [InlineData("ol name")]
    [InlineData("ol.name")]
    public void ANameThatIsANumberOrHasOtherCharactersRefuses(string name)
    {
        using var m = Create();

        m.Refuse(new AddTrigger(TestScript) { Nickname = name }, MissionErrors.NameInvalid);
    }

    [Fact]
    public void ARenameBackToTheNameOfBSkipsTheFormRule()
    {
        var longName = "ol_" + new string('x', 50);
        using var m = Create(Text.Replace("ol_end", longName));

        m.Apply(new RenameTrigger(T(longName), "ol_short"));
        m.Apply(new RenameTrigger(T("ol_short"), longName));

        Assert.True(m.Has(longName));
        Assert.Equal(EditStatus.NoChange, m.Missions.Apply(new SetTriggerStart(T(longName), false)).Status);
    }

    [Fact]
    public void NamesCompareWithoutLetterCase()
    {
        using var m = Create();

        m.Refuse(new RenameTrigger(T("ol_wave"), "OL_END"), MissionErrors.NameTaken);
        var caseOnly = m.Refuse(new RenameTrigger(T("ol_wave"), "OL_WAVE"), MissionErrors.NameInvalid);
        m.Apply(new AddTrigger(TestScript) { Nickname = "ol_new" });
        m.Apply(new RenameTrigger(T("OL_NEW"), "OL_NEW"));

        Assert.Equal("'OL_WAVE' differs from 'ol_wave' only in letter case. The game finds names without letter case, so a " +
                     "patch cannot change only the letter case. Pick another name.", caseOnly.Message);
        Assert.Equal("nickname = OL_NEW", m.Lines("ol_new")![0]);
    }
}

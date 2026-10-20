using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using Xunit;
using static OpenLancer.Tests.OlReplaceMergeTests;
using static OpenLancer.Tests.PatchWriterTests;

namespace OpenLancer.Tests;

/// <summary>
/// The patch writer for mission files (section 6 of the M6b design): <c>ol.replace</c> with <c>ol.base</c> for the
/// mission types whose line order counts, <c>ol.before</c> for new and moved ship and formation sections, and the
/// single sections. Each patch passes the self-check of the writer.
/// </summary>
public sealed class PatchWriterMissionTests
{
    private static readonly string WaveHash = SectionHash.Of(ScriptSections()[3]);

    [Fact]
    public void AChangeOfATriggerWithALineBeforeTheNicknameWritesAllLinesWithReplace()
    {
        var patch = Write(Script.Replace("Cnd_True = no_params", "Cnd_Timer = 5"));

        Assert.Equal(Lines("[Trigger]", "system = ol_system", "nickname = ol_wave", "ol.replace = true",
            $"ol.base = {WaveHash}", "InitState = ACTIVE", "Cnd_Timer = 5", "Act_ActTrig = ol_next"), patch);
    }

    [Fact]
    public void AnUnchangedSectionWritesNothing()
    {
        Assert.Equal("", Write(Script));
        var patch = Write(Script.Replace("Cnd_Timer = 3", "Cnd_Timer = 4"));

        Assert.Equal(Lines("[Trigger]", "nickname = ol_next", "ol.replace = true",
            $"ol.base = {SectionHash.Of(ScriptSections()[4])}", "Cnd_Timer = 4", "Act_ChangeState = SUCCEED"), patch);
    }

    [Fact]
    public void TheMissionSectionPatchesLineByLine()
    {
        var patch = Write(Script.Replace(@"missions\ol_test\ol_npcships.ini", @"missions\ol_test\ol_other.ini"));

        Assert.Equal(Lines("[Mission]", @"npc_ship_file = missions\ol_test\ol_other.ini"), patch);
    }

    [Fact]
    public void TheDeleteNewAndRenameForms()
    {
        var w = ScriptSections();
        w.RemoveAt(4);
        w.Add(Text("[Trigger]\nnickname = ol_added\nCnd_True = no_params\n").Sections[0]);
        var deleteAndNew = Write(w);
        Assert.Equal(Lines("[Trigger]", "nickname = ol_next", "ol.delete = true", "", "[Trigger]", "ol.new = true",
            "nickname = ol_added", "Cnd_True = no_params"), deleteAndNew);

        var renamed = ScriptSections();
        renamed[4] = Text("[Trigger]\nnickname = ol_later\nCnd_Timer = 3\nAct_ChangeState = SUCCEED\n").Sections[0];
        Assert.Equal(Lines("[Trigger]", "nickname = ol_next", "ol.rename = ol_later"),
            Write(renamed, Rename("ol_later", "ol_next")));

        // A rename with a changed body writes the old key line, the rename, the replace and the base.
        var changed = ScriptSections();
        changed[3] = Text("[Trigger]\nsystem = ol_system\nnickname = ol_wave_2\nCnd_Timer = 1\n").Sections[0];
        Assert.Equal(Lines("[Trigger]", "system = ol_system", "nickname = ol_wave", "ol.rename = ol_wave_2",
            "ol.replace = true", $"ol.base = {WaveHash}", "Cnd_Timer = 1"), Write(changed, Rename("ol_wave_2", "ol_wave")));
    }

    [Fact]
    public void ANewShipThatAFormationTakesGoesBeforeIt()
    {
        var w = ScriptSections();
        w.Insert(2, Text("[MsnShip]\nnickname = ol_ship_b\nnpc = ol_npc\n").Sections[0]);
        w[3] = Text("[MsnFormation]\nnickname = ol_form\nship = ol_ship_a\nship = ol_ship_b\n").Sections[0];

        var patch = Write(w);

        Assert.Equal(Lines("[MsnFormation]", "nickname = ol_form", "ol.replace = true",
            $"ol.base = {SectionHash.Of(ScriptSections()[2])}", "ship = ol_ship_a", "ship = ol_ship_b", "",
            "[MsnShip]", "ol.new = true", "ol.before = MsnFormation, ol_form", "nickname = ol_ship_b", "npc = ol_npc"), patch);
    }

    [Fact]
    public void AMovedFormationIsADeleteWithItsBaseAndANewSectionBeforeTheNextSection()
    {
        const string formationFirst = """
            [Mission]
            npc_ship_file = x.ini

            [MsnFormation]
            nickname = ol_form
            ship = ol_ship_a

            [MsnShip]
            nickname = ol_ship_a
            npc = ol_npc

            [Trigger]
            nickname = ol_wave
            Cnd_True = no_params
            """;
        var b = Doc(formationFirst);
        var w = b.Sections.ToList();
        (w[1], w[2]) = (w[2], w[1]);

        var result = PatchWriter.Write(ScriptPath, b, w, _ => []);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(Lines("[MsnFormation]", "nickname = ol_form", "ol.delete = true",
            $"ol.base = {SectionHash.Of(b.Sections[1])}", "", "[MsnFormation]", "ol.new = true",
            "ol.before = Trigger, ol_wave", "nickname = ol_form", "ship = ol_ship_a"), result.Text);
    }

    [Fact]
    public void AnOldFormSectionOfTheLoadedPatchBecomesAReplaceWithOneNote()
    {
        using var ws = Workspace();
        ws.Mod("act", new Dictionary<string, string> { [@"patches\" + ScriptPath] = "[Trigger]\nnickname = ol_next\nCnd_Timer = 7\n" });
        ws.WriteProfile("act");
        var session = ws.Session("act");
        var mission = new SectionRef(ScriptPath, new SectionIdentity("Mission", null));

        session.Execute(EditCommand.SetLinesFromText(mission, "npc_ship_file", [@"missions\ol_test\ol_b.ini"]));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        var note = Assert.Single(result.Problems);
        Assert.Equal(DiagnosticSeverity.Warning, note.Severity);
        Assert.Equal($"The editor wrote [Trigger] nickname = ol_next of {ScriptPath} with ol.replace. The game gets " +
                     "the same lines.", note.Message);
        var text = ws.ReadModPatch(ScriptPath);
        Assert.Contains("[Trigger]\r\nnickname = ol_next\r\nol.replace = true\r\n", text);
        Assert.Contains("Cnd_Timer = 7\r\nAct_ChangeState = SUCCEED\r\n", text);
        Assert.Equal("7", session.Get(new SectionRef(ScriptPath, new SectionIdentity("Trigger", "ol_next")))!
            .Lines("Cnd_Timer")[0].Values[0].ToString());
    }

    [Fact]
    public void ATriggerWithALineBeforeTheNicknameIsEditableAndSavesItsOrder()
    {
        using var ws = Workspace();
        ws.Mod("act", new Dictionary<string, string>());
        ws.WriteProfile("act");
        var wave = new SectionRef(ScriptPath, new SectionIdentity("Trigger", "ol_wave"));
        using (var session = ws.Session("act"))
        {
            Assert.Null(session.Get(wave)!.ReadOnlyReason);
            session.Execute(EditCommand.SetLinesFromText(wave, "Cnd_True", []));
            session.Execute(EditCommand.SetLinesFromText(wave, "Cnd_Timer", ["5"]));
            Assert.True(session.Save().Saved);
        }

        var reopened = ws.Session("act");
        Assert.Equal(["system", "nickname", "InitState", "Act_ActTrig", "Cnd_Timer"],
            reopened.Get(wave)!.Entries.Select(e => e.Name));
    }

    /// <summary>Rule P2 of section 6.3 of the M6b design: the writer keeps the loaded hash of <c>ol.base</c>, also when
    /// a lower mod changed the section after the edit. Only the user decides about the new base.</summary>
    [Fact]
    public void AWriteKeepsTheLoadedBaseOfASectionWhoseBaseChanged()
    {
        var oldHash = SectionHash.Of(ScriptSections()[4]);
        var b = Doc(Script.Replace("Cnd_Timer = 3", "Cnd_Timer = 4"));
        Assert.NotEqual(oldHash, SectionHash.Of(b.Sections[4]));
        var loaded = Lines("[Trigger]", "nickname = ol_next", "ol.replace = true", $"ol.base = {oldHash}", "Cnd_Timer = 8",
            "Act_ChangeState = SUCCEED");
        var source = PatchSource.Parse("patch.ini", Encoding.ASCII.GetBytes(loaded), ScriptPath);
        var w = b.WithSections(b.Sections);
        var diagnostics = new List<Diagnostic>();
        IniPatcher.Apply(w, source.Sections, new PatchContext("act", "patch.ini", ScriptPath, null, new PatchLedger(),
            diagnostics, keepPlaceholders: true));
        Assert.Single(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);

        // No edit of the section: the patch keeps its text.
        var untouched = PatchWriter.Write(ScriptPath, b, w.Sections, _ => [], source);
        Assert.True(untouched.Succeeded, string.Join("\n", untouched.Problems));
        Assert.Equal(loaded, untouched.Text);

        // An edit of the section keeps the old hash too.
        var edited = w.Sections.ToList();
        edited[4] = Text("[Trigger]\nnickname = ol_next\nCnd_Timer = 9\nAct_ChangeState = SUCCEED\n").Sections[0];
        var patch = PatchWriter.Write(ScriptPath, b, edited, _ => [], source);
        Assert.True(patch.Succeeded, string.Join("\n", patch.Problems));
        Assert.Equal(Lines("[Trigger]", "nickname = ol_next", "ol.replace = true", $"ol.base = {oldHash}",
            "Cnd_Timer = 9", "Act_ChangeState = SUCCEED"), patch.Text);
    }

    [Fact]
    public void ASaveOfAnotherSectionKeepsTheOldBaseSoTheWarningStays()
    {
        using var ws = Workspace();
        var oldHash = SectionHash.Of(ScriptSections()[4]);
        ws.Mod("lower", new Dictionary<string, string>
        {
            [@"patches\" + ScriptPath] = "[Trigger]\nnickname = ol_next\nCnd_Timer = 4\n",
        });
        ws.Mod("act", new Dictionary<string, string>
        {
            [@"patches\" + ScriptPath] =
                $"[Trigger]\nnickname = ol_next\nol.replace = true\nol.base = {oldHash}\nCnd_Timer = 8\n",
        });
        ws.WriteProfile("lower", "act");
        var session = ws.Session("act");
        static bool BaseWarning(Diagnostic d) =>
            d.Severity == DiagnosticSeverity.Warning && d.Message.StartsWith("Mod 'act' replaces [Trigger] nickname = ol_next");
        Assert.Contains(session.EditView.Diagnostics, BaseWarning);
        var mission = new SectionRef(ScriptPath, new SectionIdentity("Mission", null));

        session.Execute(EditCommand.SetLinesFromText(mission, "npc_ship_file", [@"missions\ol_test\ol_b.ini"]));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Contains($"ol.base = {oldHash}\r\n", ws.ReadModPatch(ScriptPath));
        Assert.Contains(session.EditView.Diagnostics, BaseWarning);
    }

    /// <summary>A change of the order of lines with different names, in a mission type whose line order counts, writes
    /// the section with <c>ol.replace</c>. The self-check compares the order of all lines.</summary>
    [Theory]
    [InlineData("MsnShip", "nickname = ol_s\nlabel = ol_l\nnpc = ol_npc\n", "nickname = ol_s\nnpc = ol_npc\nlabel = ol_l\n")]
    [InlineData("MsnFormation", "nickname = ol_f\nship = ol_a\nlabel = ol_l\n", "nickname = ol_f\nlabel = ol_l\nship = ol_a\n")]
    [InlineData("Dialog", "nickname = ol_d\nline = ol_a, ol_b, dx_1\nsystem = ol_sys\n",
        "nickname = ol_d\nsystem = ol_sys\nline = ol_a, ol_b, dx_1\n")]
    [InlineData("Trigger", "nickname = ol_t\nAct_A = 1\nAct_B = 2\n", "nickname = ol_t\nAct_B = 2\nAct_A = 1\n")]
    public void AChangeOfTheOrderOfLinesWithDifferentNamesReachesThePatch(string type, string before, string after)
    {
        var b = Doc($"[Mission]\nnpc_ship_file = x.ini\n\n[{type}]\n{before}");
        var w = Doc($"[Mission]\nnpc_ship_file = x.ini\n\n[{type}]\n{after}").Sections;

        var result = PatchWriter.Write(ScriptPath, b, w, _ => []);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Contains("ol.replace = true", result.Text);
        var merged = b.WithSections(b.Sections);
        var diagnostics = new List<Diagnostic>();
        IniPatcher.Apply(merged, IniDocument.Parse(ScriptPath, result.ToBytes()).Sections,
            new PatchContext("act", ScriptPath, ScriptPath, null, new PatchLedger(), diagnostics, keepPlaceholders: true));
        Assert.Empty(diagnostics);
        Assert.Equal(w[1].Select(e => e.Name), merged.Sections[1].Select(e => e.Name));
        // Outside the mission files, only the types of the upstream entry handlers read their lines in order.
        Assert.Equal(!SectionKeys.IsOrderSensitive(type),
            SectionComparer.Equivalent(b.Sections[1], w[1], false, @"DATA\other.ini"));
        Assert.False(SectionComparer.Equivalent(b.Sections[1], w[1], false, ScriptPath));
    }

    private static string Write(string w) => Write(Doc(w).Sections);

    private static string Write(IReadOnlyList<Section> w,
        IReadOnlyDictionary<SectionIdentity, SectionIdentity>? renamed = null)
    {
        var result = PatchWriter.Write(ScriptPath, Doc(Script), w, _ => [], renamed: renamed);
        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        return result.Text;
    }

    private static Dictionary<SectionIdentity, SectionIdentity> Rename(string to, string from) =>
        new() { [new SectionIdentity("Trigger", to)] = new SectionIdentity("Trigger", from) };

    private static IniDocument Doc(string text) =>
        IniDocument.Parse(ScriptPath, Encoding.ASCII.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n")));
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Ini;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The mission directives of the merge (sections 6.2 to 6.5 of the M6b design): <c>ol.replace</c>, <c>ol.base</c>,
/// <c>ol.before</c> and the single sections of a mission file. The script uses invented names.
/// </summary>
public sealed class OlReplaceMergeTests
{
    internal const string ScriptPath = @"DATA\MISSIONS\OL_TEST\ol_test.ini";

    internal const string Script = """
        [Mission]
        npc_ship_file = missions\ol_test\ol_npcships.ini

        [MsnShip]
        nickname = ol_ship_a
        npc = ol_npc

        [MsnFormation]
        nickname = ol_form
        ship = ol_ship_a

        [Trigger]
        system = ol_system
        nickname = ol_wave
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = ol_next

        [Trigger]
        nickname = ol_next
        Cnd_Timer = 3
        Act_ChangeState = SUCCEED
        """;

    [Fact]
    public void AReplaceKeepsTheOrderThePlaceAndTheLinesBeforeTheKeyLine()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("""
            [Trigger]
            system = ol_system
            nickname = ol_wave
            ol.replace = true
            Cnd_Timer = 5
            Act_ActTrig = ol_next
            Act_ActTrig = ol_other
            """)));

        Assert.Empty(overlay.Diagnostics);
        var sections = TestWorkspace.Sections(overlay, ScriptPath);
        Assert.Equal(["Mission", "MsnShip", "MsnFormation", "Trigger", "Trigger"], sections.Select(s => s.Name));
        Assert.Equal(["system = ol_system", "nickname = ol_wave", "Cnd_Timer = 5", "Act_ActTrig = ol_next",
            "Act_ActTrig = ol_other"], Lines(sections[3]));
    }

    [Fact]
    public void AReplaceFindsItsTargetByTheFirstKeyLineWhereverItIs()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("""
            [Trigger]
            Act_ChangeState = FAIL
            ol.replace = true
            nickname = OL_NEXT
            Cnd_Timer = 9
            """)));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["Act_ChangeState = FAIL", "nickname = OL_NEXT", "Cnd_Timer = 9"],
            Lines(TestWorkspace.Sections(overlay, ScriptPath)[4]));
    }

    [Theory]
    [InlineData("[Mission]\nol.replace = true\nnpc_ship_file = x.ini", "ol.replace is only for sections whose line order counts.")]
    [InlineData("[Trigger]\nnickname = ol_new\nol.new = true\nol.replace = true", "ol.replace cannot be used with ol.new")]
    [InlineData("[Trigger]\nnickname = ol_next\nol.delete = true\nol.replace = true", "ol.replace cannot be used with ol.new")]
    [InlineData("[Trigger]\nnickname = ol_next\nol.remove = Cnd_Timer\nol.replace = true", "ol.replace cannot be used with ol.new")]
    [InlineData("[Trigger]\nnickname = ol_next\nol.parent = ol_wave\nol.replace = true", "ol.replace cannot be used with ol.new")]
    [InlineData("[Trigger]\nnickname = ol_next\nol.base = 0123456789abcdef\nCnd_Timer = 1", "ol.base is only for a section of a mission file with ol.replace or ol.delete.")]
    [InlineData("[Trigger]\nnickname = ol_next\nol.replace = true\nol.base = a\nol.base = b", "ol.base needs one line with one value")]
    [InlineData("[MsnShip]\nnickname = ol_ship_a\nol.before = MsnFormation, ol_form\nnpc = x", "ol.before is only for a new section of a mission file.")]
    [InlineData("[MsnShip]\nnickname = ol_ship_b\nol.new = true\nol.before = MsnFormation", "ol.before needs one line with two values")]
    [InlineData("[MsnShip]\nnickname = ol_ship_b\nol.new = true\nol.before = MsnFormation, ol_missing", "ol.before = MsnFormation, ol_missing: DATA\\MISSIONS\\OL_TEST\\ol_test.ini has no such section.")]
    public void TheMergeRefusesAWrongUseOfTheMissionDirectives(string patch, string message)
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch(patch)));

        var error = Assert.Single(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void AReplaceOutsideAMissionFileIsAnError()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\other.ini", "[Trigger]\nnickname = ol_t\nCnd_True = no_params\n");
        var overlay = ws.Build(ws.Mod("m", new Dictionary<string, string>
        {
            [@"patches\DATA\other.ini"] = "[Trigger]\nnickname = ol_t\nol.replace = true\nCnd_Timer = 1",
        }));

        var error = Assert.Single(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("ol.replace is only for sections whose line order counts.", error.Message);
    }

    [Fact]
    public void AReplaceWithARenameReplacesTheBodyThenGivesTheNewKey()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("""
            [Trigger]
            system = ol_system
            nickname = ol_wave
            ol.rename = ol_wave_2
            ol.replace = true
            Cnd_Timer = 2
            """)));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["system = ol_system", "nickname = ol_wave_2", "Cnd_Timer = 2"],
            Lines(TestWorkspace.Sections(overlay, ScriptPath)[3]));
    }

    [Fact]
    public void TheBaseHashIsQuietWhenTheBaseIsTheSameAndWarnsWhenItChanged()
    {
        var hash = SectionHash.Of(ScriptSections()[4]);
        var patch = $"[Trigger]\nnickname = ol_next\nol.replace = true\nol.base = {hash}\nCnd_Timer = 8\n";

        using (var ws = Workspace())
        {
            var overlay = ws.Build(ws.Mod("act", Patch(patch)));
            Assert.Empty(overlay.Diagnostics);
        }

        using (var ws = Workspace())
        {
            var lower = ws.Mod("lower", Patch("[Trigger]\nnickname = ol_next\nCnd_Timer = 4\n"));
            var overlay = ws.Build(lower, ws.Mod("act", Patch(patch)));
            var warning = Assert.Single(overlay.Diagnostics, d => d.Message.StartsWith("Mod 'act'"));
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Equal($"Mod 'act' replaces [Trigger] nickname = ol_next of {ScriptPath}. The base of this section " +
                         "changed after the edit, and its changes go away. Compare the two, then keep your lines or use " +
                         "the new base.", warning.Message);
            Assert.Equal(["nickname = ol_next", "Cnd_Timer = 8"], Lines(TestWorkspace.Sections(overlay, ScriptPath)[4]));
        }
    }

    [Fact]
    public void TheDeleteOfAMoveChecksTheBaseToo()
    {
        using var ws = Workspace();
        var lower = ws.Mod("lower", Patch("[MsnFormation]\nnickname = ol_form\nship = ol_ship_x\n"));
        var hash = SectionHash.Of(ScriptSections()[2]);
        var overlay = ws.Build(lower, ws.Mod("act", Patch($"[MsnFormation]\nnickname = ol_form\nol.delete = true\nol.base = {hash}\n")));

        Assert.Contains(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning &&
                                                  d.Message.StartsWith("Mod 'act' replaces [MsnFormation] nickname = ol_form"));
        Assert.DoesNotContain(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ABinarySectionAndTheSameTextSectionGiveTheSameHash()
    {
        const string text = "[Trigger]\nnickname = ol_t\nCnd_Timer = 3\nAct_Pos = 1.5, -2, 7\nflag = True\nname = ol_Name\n";
        var parsed = IniDocument.Parse("t.ini", Encoding.ASCII.GetBytes(text)).Sections[0];
        using var stream = new MemoryStream();
        IniWriter.WriteBini(stream, [Copy(parsed, binary: true)]);
        var binary = IniDocument.Parse("t.ini", stream.ToArray()).Sections[0];
        var asFloat = IniDocument.Parse("t.ini", Encoding.ASCII.GetBytes(text.Replace("Cnd_Timer = 3", "Cnd_Timer = 3.0")))
            .Sections[0];
        var changed = IniDocument.Parse("t.ini", Encoding.ASCII.GetBytes(text.Replace("ol_Name", "ol_name"))).Sections[0];

        Assert.Equal(SectionHash.Of(parsed), SectionHash.Of(binary));
        Assert.Equal(SectionHash.Of(parsed), SectionHash.Of(asFloat));
        Assert.NotEqual(SectionHash.Of(parsed), SectionHash.Of(changed));
        Assert.Matches("^[0-9a-f]{16}$", SectionHash.Of(parsed));
    }

    [Fact]
    public void AHashThatTheTextParserReadsAsANumberStillMatches()
    {
        var section = ScriptSections()[4];
        var hash = SectionHash.Of(section);
        var parsed = IniDocument.ParseTextLine("ol.base = " + hash)!;

        Assert.True(SectionHash.Matches(section, SectionHash.TextOf(parsed[0])));
        Assert.False(SectionHash.Matches(section, "1234567890123456"));
    }

    /// <summary>The text parser reads a hash of decimal digits as a number. The writer keeps the text of the hash,
    /// and the merge compares the hash through the parser.</summary>
    [Fact]
    public void AHashOfDecimalDigitsKeepsItsText()
    {
        var builder = new OpenLancer.Data.Editing.PatchSectionBuilder(null, binary: true, inB: true, keepText: true);

        Assert.Null(builder.Base("8336520551505279"));
        Assert.Equal(["ol.base = 8336520551505279"], builder.Lines);
        var parsed = IniDocument.ParseTextLine(builder.Lines[0])!;
        Assert.IsNotType<StringValue>(parsed[0], exactMatch: false);
        Assert.Equal("8336520551505279", SectionHash.TextOf(parsed[0]));
    }

    [Fact]
    public void BeforePutsANewShipBeforeItsFormation()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("m", Patch("""
            [MsnShip]
            nickname = ol_ship_b
            ol.new = true
            ol.before = MsnFormation, OL_FORM
            npc = ol_npc
            """)));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["Mission", "MsnShip ol_ship_a", "MsnShip ol_ship_b", "MsnFormation ol_form", "Trigger ol_wave",
            "Trigger ol_next"], Describe(TestWorkspace.Sections(overlay, ScriptPath)));
    }

    /// <summary><c>ol.before</c> finds its section by the first <c>nickname</c> line, as <c>ol.replace</c> does and as
    /// the game reads the section.</summary>
    [Fact]
    public void BeforeFindsItsSectionByTheFirstKeyLine()
    {
        var script = Script.Replace("nickname = ol_form\n", "nickname = ol_form\nnickname = ol_form_b\n");
        Assert.NotEqual(Script, script);
        const string patch = "[MsnShip]\nnickname = ol_ship_b\nol.new = true\nol.before = MsnFormation, {0}\nnpc = ol_npc\n";

        using (var ws = Workspace(script))
        {
            var overlay = ws.Build(ws.Mod("m", Patch(string.Format(patch, "ol_form"))));
            Assert.Empty(overlay.Diagnostics);
            Assert.Equal("MsnShip", TestWorkspace.Sections(overlay, ScriptPath)[2].Name);
        }

        using (var ws = Workspace(script))
        {
            var overlay = ws.Build(ws.Mod("m", Patch(string.Format(patch, "ol_form_b"))));
            var error = Assert.Single(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Contains("ol.before = MsnFormation, ol_form_b: DATA\\MISSIONS\\OL_TEST\\ol_test.ini has no such section.",
                error.Message);
        }
    }

    [Fact]
    public void AMissionPatchFindsTheOneMissionSectionAndFailsWithTwo()
    {
        using (var ws = Workspace())
        {
            var overlay = ws.Build(ws.Mod("m", Patch("[Mission]\nnpc_ship_file = missions\\ol_test\\ol_other.ini\n")));
            Assert.Empty(overlay.Diagnostics);
            Assert.Equal(["npc_ship_file = missions\\ol_test\\ol_other.ini"], Lines(TestWorkspace.Sections(overlay, ScriptPath)[0]));
        }

        using (var ws = Workspace(Script + "\n[Mission]\nnpc_ship_file = x.ini\n"))
        {
            var overlay = ws.Build(ws.Mod("m", Patch("[Mission]\nnpc_ship_file = y.ini\n")));
            var error = Assert.Single(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Contains("matches 2 sections", error.Message);
        }
    }

    internal static TestWorkspace Workspace(string script = Script)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(ScriptPath, script);
        return ws;
    }

    internal static List<Section> ScriptSections() =>
        IniDocument.Parse(ScriptPath, Encoding.ASCII.GetBytes(Script)).Sections;

    private static Dictionary<string, string> Patch(string text) => new() { [@"patches\" + ScriptPath] = text };

    internal static string[] Lines(Section section) =>
        section.Select(e => $"{e.Name} = {string.Join(", ", e.Select(v => v.ToString()))}").ToArray();

    private static string[] Describe(IEnumerable<Section> sections) =>
        sections.Select(s => s.Name == "Mission" ? s.Name : $"{s.Name} {s["nickname"]![0]}").ToArray();

    /// <summary>A copy of a section whose booleans are text, as a binary file stores them.</summary>
    private static Section Copy(Section section, bool binary)
    {
        var copy = new Section(section.Name);
        foreach (var entry in section)
        {
            var line = new Entry(copy, entry.Name, entry.Count);
            foreach (var value in entry)
                line.Add(binary && value is BooleanValue b ? new StringValue(b.ToBoolean() ? "True" : "False") : value);
            copy.Add(line);
        }
        return copy;
    }
}

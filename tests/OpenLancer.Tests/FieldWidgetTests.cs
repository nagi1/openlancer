using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Ini;
using OpenLancer.Editor.Ui.Fields;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The models of the shared field widgets (section 11 of the M6a design): the list field with its three codecs, the
/// completeness report, the help texts, the canonical line writer, the raw text with its line map, the lint marks and the
/// state of the raw INI editor.
/// </summary>
public sealed class FieldWidgetTests
{
    private static IReadOnlyList<ListFieldLine> Lines(string text) =>
        IniDocument.Parse("test.ini", Encoding.ASCII.GetBytes("[Section]\n" + text)).Sections[0]
            .Select(ListFieldLine.Of).ToList();

    private static readonly ListFieldSpec Members = new("members", "Members", "The ships of the formation.",
        ListFieldCodec.RepeatedKey, ["ship"], [new ListColumn("Ship", 0, ListColumnKind.Reference, "ships")]);

    private static readonly ListFieldSpec Ships = new("ships", "Ships", null, ListFieldCodec.ValueList,
        ["Cnd_NPCSystemEnter"], [new ListColumn("Ship", 0, ListColumnKind.Reference, "ships")], Prefix: 1);

    private static readonly ListFieldSpec Orders = new("orders", "Orders", null, ListFieldCodec.MixedKeys,
        ["GotoVec", "Delay"],
        [
            new ListColumn("Movement", 0, ListColumnKind.Token, Tokens: ["goto", "goto_cruise"], Key: "GotoVec"),
            new ListColumn("Point", 1, ListColumnKind.Position, Key: "GotoVec"),
            new ListColumn("Range", 4, ListColumnKind.WholeNumber, Key: "GotoVec"),
            new ListColumn("Time", 0, ListColumnKind.Number, Min: 0, Max: 60, Key: "Delay"),
        ]);

    [Fact]
    public void ARepeatedKeyGivesOneRowForEachLine()
    {
        var model = ListFieldModel.Read(Members, Lines("nickname = f\nship = a\nship = b\nlabel = x\n"));

        Assert.Equal(["a", "b"], model.Rows.Select(r => r.Cell(0)));
        // An unchanged row writes its source values with the same value types.
        var written = model.Write();
        Assert.Same(model.Rows[0].Source, written[0].Values);
    }

    [Fact]
    public void AValueListKeepsItsPrefix()
    {
        var model = ListFieldModel.Read(Ships, Lines("Cnd_NPCSystemEnter = sys_a, s1, s2, s3\n"));

        Assert.Equal(["s1", "s2", "s3"], model.Rows.Select(r => r.Cell(0)));
        var line = Assert.Single(model.Remove(1).Write());
        Assert.Equal("Cnd_NPCSystemEnter = sys_a, s1, s3", IniLines.Line(line.Key, line.Values));
    }

    [Fact]
    public void MixedKeysKeepTheFileOrderAndTheColumnsOfEachKey()
    {
        var model = ListFieldModel.Read(Orders, Lines("nickname = ol\nGotoVec = goto, 1, 2, 3, 100\nDelay = 5\n" +
                                                      "GotoVec = goto_cruise, 4, 5, 6, 200\n"));

        Assert.Equal(["GotoVec", "Delay", "GotoVec"], model.Rows.Select(r => r.Key));
        Assert.Equal(3, Orders.ColumnsOf("GotoVec").Count);
        Assert.Single(Orders.ColumnsOf("delay"));
    }

    [Fact]
    public void TheOperationsGiveNewRowsAndLeaveTheOldOnes()
    {
        var model = ListFieldModel.Read(Members, Lines("ship = a\nship = b\nship = c\n"));

        var added = model.Add(1, new ListFieldRow("ship", ["n"], null));
        var removed = model.Remove(0);
        var moved = model.Move(0, 2);
        var set = model.Set(1, 0, "x");

        Assert.Equal(["a", "b", "c"], model.Rows.Select(r => r.Cell(0)));
        Assert.Equal(["a", "n", "b", "c"], added.Rows.Select(r => r.Cell(0)));
        Assert.Equal(["b", "c"], removed.Rows.Select(r => r.Cell(0)));
        Assert.Equal(["b", "c", "a"], moved.Rows.Select(r => r.Cell(0)));
        Assert.Equal(["a", "x", "c"], set.Rows.Select(r => r.Cell(0)));
        Assert.True(set.Rows[1].Changed);
        Assert.False(set.Rows[0].Changed);
        Assert.Same(model, model.Move(0, 0));
        Assert.Same(model, model.Remove(7));
        Assert.Equal(["ship = a", "ship = x", "ship = c"], set.Write().Select(l => IniLines.Line(l.Key, l.Values)));
    }

    [Fact]
    public void TheListFieldViewRebuildsAnEditThroughTheOperations()
    {
        var model = ListFieldModel.Read(Members, Lines("ship = a\nship = b\nship = c\n"));

        // The rows after a drag of c to the top, a new row at the end and a change of a.
        var rows = new List<(ListFieldRow, int)>
        {
            (model.Rows[2], 2), (model.Rows[0] with { Cells = ["a2"], Changed = true }, 0), (model.Rows[1], 1),
            (new ListFieldRow("ship", ["d"], null) { Changed = true }, -1),
        };
        var result = ListField.Rebuild(model, rows);

        Assert.Equal(["c", "a2", "b", "d"], result.Rows.Select(r => r.Cell(0)));
        // A removed row goes.
        Assert.Equal(["a", "c"], ListField.Rebuild(model, [(model.Rows[0], 0), (model.Rows[2], 2)]).Rows.Select(r => r.Cell(0)));
    }

    [Fact]
    public void TheCellChecksGiveTheRowAndTheColumn()
    {
        var model = ListFieldModel.Read(Orders, Lines("GotoVec = GOTO, 1.5, 2, 3, 100.5\nDelay = 70\nDelay = x\n"));
        var problems = model.Check(null);

        Assert.Contains(problems, p => p is { Row: 0, Column: 2 } && p.Message.StartsWith("The game reads only the whole part of the range."));
        Assert.Contains(problems, p => p is { Row: 1, Column: 0, Severity: DiagnosticSeverity.Warning } &&
                                       p.Message == "70 is outside 0 to 60. Use a value from 0 to 60.");
        Assert.Contains(problems, p => p is { Row: 2, Column: 0, Severity: DiagnosticSeverity.Error } &&
                                       p.Message == "'x' is not a number. Write a number.");
        // A word of a set that compares without letter case is not a problem.
        Assert.DoesNotContain(problems, p => p is { Row: 0, Column: 0 });

        var cased = new ListFieldSpec("who", "Who", null, ListFieldCodec.RepeatedKey, ["who"],
            [new ListColumn("Who", 0, ListColumnKind.Token, Tokens: ["Player"], CaseSensitive: true)]);
        var problem = Assert.Single(ListFieldModel.Read(cased, Lines("who = player\n")).Check(null));
        Assert.Equal("'player' must be 'Player'. The game compares this word with letter case, so the line does nothing. " +
                     "Write 'Player'.", problem.Message);

        var reference = Assert.Single(ListFieldModel.Read(Members, Lines("ship = a\nship = b\n"))
            .Check((list, value) => value == "a", list => "Ship"));
        Assert.Equal(1, reference.Row);
        Assert.Equal("'b' is not in the list of the ship entries. The game ignores this value. Pick a value from the list.",
            reference.Message);
    }

    [Fact]
    public void TheCompletenessReportCountsHiddenStepsAsDone()
    {
        var report = new CompletenessReport("Item",
        [
            new CompletenessStep("A", CompletenessState.Done, "r"),
            new CompletenessStep("B", CompletenessState.Missing, "r", IsError: true),
            new CompletenessStep("C", CompletenessState.Hidden, "r"),
        ]);

        Assert.Equal("2 of 3 steps complete", report.Summary);
        Assert.True(report.HasError);
        Assert.Equal("1 base-game message is hidden.", report.HiddenLine);
        Assert.Equal("All 2 steps complete.", new CompletenessReport("x",
            [new CompletenessStep("A", CompletenessState.Done, "r"), new CompletenessStep("B", CompletenessState.Hidden, "r")]).Summary);
    }

    [Fact]
    public void TheFirstSentenceOfAHelpText()
    {
        var help = new HelpText("Title", "One unit of logic. It waits.", "One section.");

        Assert.Equal("One unit of logic.", help.FirstSentence);
        Assert.Equal("No period", HelpText.FirstSentenceOf("No period"));
        Assert.Equal("Version 1.5 is new.", HelpText.FirstSentenceOf("Version 1.5 is new. More."));
    }

    [Fact]
    public void TheCanonicalLineWriterKeepsTheOldOutput()
    {
        var section = IniDocument.Parse("t.ini", Encoding.ASCII.GetBytes(
            "[Section]\nkey = 1, 2.5, text\nBreakFormation\nkey = = start, last, \nhash = 2314954114\n")).Sections[0];

        Assert.Equal(["[Section]", "key = 1, 2.5, text", "BreakFormation", "key = = start, last, ", "hash = 2314954114"],
            IniLines.SectionLines(section));
        Assert.Equal("[Section]\r\nkey = 1, 2.5, text\r\nBreakFormation\r\nkey = = start, last, \r\nhash = 2314954114\r\n",
            IniLines.Text([section]));
    }

    [Fact]
    public void TheRawTextRoundTripsTheRetailForms()
    {
        var text = "[Trigger]\nnickname = t1\nBreakFormation\nAct_GiveObjList = = wing, ol\nAct_PlayMusic = a, b, \n" +
                   "Act_CallThorn = missions\\\\m01\\\\clip.thn\n";
        var sections = IniDocument.Parse("t.ini", Encoding.ASCII.GetBytes(text)).Sections;

        var raw = RawText.Canonical("t.ini", sections);

        Assert.Empty(raw.ReadOnlySections);
        Assert.Equal(@"Act_CallThorn = missions\\m01\\clip.thn", raw.Lines[5]);
        Assert.True(RawText.RoundTrips("t.ini", sections[0]));
    }

    [Fact]
    public void AStringWithACommaOrASemicolonMakesTheSectionReadOnly()
    {
        var section = new Section("Trigger");
        var line = new Entry(section, "Act_DebugMsg");
        line.Add(new StringValue("a, b"));
        section.Add(line);
        var semicolon = new Section("Trigger");
        var other = new Entry(semicolon, "Act_DebugMsg");
        other.Add(new StringValue("a;b"));
        semicolon.Add(other);

        Assert.False(RawText.RoundTrips("t.ini", section));
        var raw = RawText.Canonical("t.ini", [section, semicolon]);
        Assert.Equal(RawText.ReadOnlyReason, raw.ReadOnlySections[0]);
        Assert.True(raw.ReadOnlySections.ContainsKey(1));
    }

    [Fact]
    public void TheFileTextKeepsItsCommentsAndTheMapPointsAtTheEntries()
    {
        var bytes = Encoding.ASCII.GetBytes(";[Trigger]\r\n; a removed trigger\r\n[Trigger]\r\nnickname = t1 ; the first\r\n" +
                                            "\r\n; a comment\r\nCnd_True = no_params\r\n\r\n[Trigger]\r\nnickname = t2\r\n");
        var sections = IniDocument.Parse("t.ini", bytes).Sections;

        var raw = RawText.Of("t.ini", sections, false, bytes, fileText: true);

        Assert.True(raw.IsFileText);
        Assert.Equal(";[Trigger]", raw.Lines[0]);
        Assert.Equal(2, raw.HeaderLine(0));
        Assert.Equal(6, raw.EntryLine(0, 1));
        Assert.Equal(9, raw.EntryLine(1, 0));
        Assert.Equal((0, 1), raw.At(6));
        Assert.Equal((0, -1), raw.At(5));
        Assert.Equal((-1, -1), raw.At(0));
        // A changed file, or a binary file, gives the canonical text.
        Assert.False(RawText.Of("t.ini", sections, false, bytes, fileText: false).IsFileText);
    }

    [Fact]
    public void TheLintMarksFollowTheLinesAndTheToggle()
    {
        var sections = IniDocument.Parse("t.ini", Encoding.ASCII.GetBytes("[Trigger]\nnickname = t1\nCnd_True = no_params\n")).Sections;
        var raw = RawText.Canonical("t.ini", sections);
        var id = SectionIdentity.Identify(sections)[0];
        var line = Diagnostic.Warning("Mission", "A line.") with { Target = new SectionRef("t.ini", id), EntryIndex = 1 };
        var whole = Diagnostic.Error("Mission", "A section.") with { Target = new SectionRef("t.ini", id) };
        var baseGame = Diagnostic.Warning("Mission", "Base.") with
        {
            Target = new SectionRef("t.ini", id), EntryIndex = 1, Origin = MessageOrigin.BaseGame,
        };

        var marks = IniLint.Marks(raw, [line, whole, baseGame], showBaseGame: false);

        Assert.Equal([0, 2], marks.Select(m => m.Line));
        Assert.Equal(DiagnosticSeverity.Error, marks[0].Severity);
        Assert.Equal("A line.", marks[1].Message);
        var all = IniLint.Marks(raw, [baseGame], showBaseGame: true);
        Assert.True(Assert.Single(all).BaseGame);
    }

    [Fact]
    public void TheRawEditorAddsTheMarksAgainAfterANewText()
    {
        var state = new RawIniEditorState();
        IReadOnlyList<RawMark> marks = [new RawMark(1, DiagnosticSeverity.Error, "A problem.", false)];

        Assert.Equal((true, true), state.Plan("text", marks));
        Assert.Equal((false, false), state.Plan("text", marks));
        // The native editor clears the marks with a new text, so the marks come again.
        Assert.Equal((true, true), state.Plan("new text", marks));
        Assert.Equal((false, true), state.Plan("new text", []));
    }

    [Fact]
    public void TheRawEditorFallsBackWhenAnExportIsMissing()
    {
        Assert.True(RawIniEditor.HasExports(_ => true));
        Assert.False(RawIniEditor.HasExports(name => name != "igExtTextEditorAddMarker"));
        Assert.Contains("Build the natives again.", RawIniEditor.NeedsNewNatives);
    }
}

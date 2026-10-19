using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The whole mission files of the active mod (section 6.7 of the M6b design, decision D680): a document of the kind
/// "whole file". B is the file of the mod as it is on disk, W gets the edits, and a save writes the whole file as text.
/// The script uses invented names.
/// </summary>
public sealed class WholeFileScriptTests
{
    internal const string ScriptPath = @"DATA\MISSIONS\OL_TEST\ol_test.ini";
    internal const string WholeFile = @"files\" + ScriptPath;

    internal const string Script = """
        ; An OpenLancer test script.
        [Mission]
        npc_ship_file = missions\ol_test\ol_npcships.ini

        ; The first wave.
        [Trigger]
        system = ol_system
        nickname = ol_wave
        Cnd_True = no_params ; at once
        Act_ActTrig = ol_next

        [Trigger]
        nickname = ol_next
        Cnd_Timer = 3
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = ol_twin
        Cnd_True = no_params

        [Trigger]
        nickname = ol_twin
        Cnd_Timer = 1
        """;

    internal static readonly SectionRef Wave = Trigger("ol_wave");
    internal static readonly SectionRef Next = Trigger("ol_next");

    [Fact]
    public void EachSectionOfAWholeMissionFileOfTheModIsEditable()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var secondTwin = new SectionRef(ScriptPath, new SectionIdentity("Trigger", "ol_twin", null, 1));

        Assert.Null(session.Get(Wave)!.ReadOnlyReason);
        Assert.Null(session.Get(secondTwin)!.ReadOnlyReason);
        Assert.Equal((ValueOrigin.ActiveMod, "act"), session.Origin(Next, "Cnd_Timer"));
        session.Execute(EditCommand.SetLinesFromText(secondTwin, "Cnd_Timer", ["2"]));
        Assert.Equal(["2"], Values(session, secondTwin, "Cnd_Timer"));
    }

    [Fact]
    public void ASaveWritesTheWholeFileAndKeepsTheCommentsAndTheOtherLines()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Next, "Cnd_Timer", ["5"]));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Empty(result.Problems);
        Assert.Equal(OnDisk(Script).Replace("Cnd_Timer = 3", "Cnd_Timer = 5"), ws.ReadModFile(WholeFile));
        Assert.False(session.IsDirty);
        Assert.False(Directory.Exists(Path.Combine(ws.ModFolder("act"), Mod.PatchesFolder)));
        session.Dispose();
        Assert.Equal(["5"], Values(ws.Session("act"), Next, "Cnd_Timer"));
    }

    [Fact]
    public void UndoPutsBackTheFileAndASaveWithoutANetChangeWritesNothing()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var before = TestWorkspace.Files(ws.ModFolder("act"));

        session.Execute(EditCommand.SetLinesFromText(Wave, "Cnd_True", []));
        session.Execute(EditCommand.SetLinesFromText(Wave, "Cnd_Timer", ["2"]));
        Assert.Equal(["system", "nickname", "Act_ActTrig", "Cnd_Timer"], session.Get(Wave)!.Entries.Select(e => e.Name));
        session.Undo();
        session.Undo();
        var result = session.Save();

        Assert.True(result.Saved);
        Assert.Empty(result.Written);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act")));
    }

    [Fact]
    public void ABinaryWholeFileBecomesTextWithANote()
    {
        using var ws = Workspace(new Dictionary<string, string>());
        var file = Path.Combine(ws.ModFolder("act"), WholeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var stream = File.Create(file))
            IniWriter.WriteBini(stream, IniFile.ParseFile(file, new MemoryStream(Encoding.ASCII.GetBytes(Script))).ToArray());
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Next, "Cnd_Timer", ["5"]));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        var note = Assert.Single(result.Problems);
        Assert.Equal(DiagnosticSeverity.Warning, note.Severity);
        Assert.StartsWith("The save writes the binary file ", note.Message);
        var text = ws.ReadModFile(WholeFile);
        Assert.StartsWith("[Mission]", text);
        Assert.Contains("Cnd_Timer = 5", text);
        Assert.Equal(["5"], Values(session, Next, "Cnd_Timer"));
    }

    [Fact]
    public void TheSaveRefusesAWholeMissionFileInAModInsideTheRepo()
    {
        using var ws = Workspace();
        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "<Solution />");
        var bytes = File.ReadAllBytes(Path.Combine(ws.ModFolder("act"), WholeFile));
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Next, "Cnd_Timer", ["5"]));

        var result = session.Save();

        Assert.False(result.Saved);
        var error = Assert.Single(result.Problems);
        Assert.Equal($"The mod 'act' is in the OpenLancer repo {ws.Root}. The editor writes no whole mission file into " +
                     "it, because the content scan cannot tell such a file from a copy of a retail script (decision D70).",
            error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(ws.ModFolder("act"), WholeFile)));
    }

    [Fact]
    public void AWholeFileOutsideTheMissionFilesStaysReadOnly()
    {
        using var ws = EditSessionTests.Workspace(new() { [@"files\" + GoodsPath] = Goods });
        var session = ws.Session("act");

        Assert.Contains("replaces", session.Get(Good("g1"))!.ReadOnlyReason);
        Assert.Throws<InvalidOperationException>(() => session.Execute(Set(Good("g1"), "price", new Int32Value(1))));
    }

    /// <summary>A save of a whole file and an editor file stops after its commit point. The next open completes it, so
    /// the mod has both files of the save.</summary>
    [Fact]
    public void AnOpenCompletesASaveOfAWholeFileThatStoppedAfterItsCommitPoint()
    {
        for (var k = 0; k <= 2; k++)
        {
            using var ws = Workspace();
            var session = ws.Session("act");
            session.Execute(EditCommand.SetLinesFromText(Next, "Cnd_Timer", ["5"]));
            session.Execute(EditCommand.SetModFile(ModFileDocumentTests.GraphPath, ModFileDocumentTests.GraphText));
            Assert.Throws<EditorStopped>(() => session.Save(false, done =>
            {
                if (done == k)
                    throw new EditorStopped();
            }));
            session.Abandon();

            var reopened = ws.Session("act");

            Assert.Empty(reopened.OpenProblems);
            Assert.Equal(["5"], Values(reopened, Next, "Cnd_Timer"));
            Assert.Equal(ModFileDocumentTests.GraphText, reopened.ModFileText(ModFileDocumentTests.GraphPath));
            Assert.Equal(OnDisk(Script).Replace("Cnd_Timer = 3", "Cnd_Timer = 5"), ws.ReadModFile(WholeFile));
        }
    }

    internal static TestWorkspace Workspace(Dictionary<string, string>? files = null) =>
        EditSessionTests.Workspace(files ?? new() { [WholeFile] = Script });

    /// <summary>The text of a file that the workspace writes: each line ends with CR LF.</summary>
    internal static string OnDisk(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    internal static SectionRef Trigger(string nickname) => new(ScriptPath, new SectionIdentity("Trigger", nickname));

    private sealed class EditorStopped : Exception;
}

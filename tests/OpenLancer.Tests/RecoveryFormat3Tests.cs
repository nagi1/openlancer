using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;
using static OpenLancer.Tests.ModFileDocumentTests;
using static OpenLancer.Tests.WholeFileScriptTests;

namespace OpenLancer.Tests;

/// <summary>
/// Format 3 of the recovery file (section 6.8 of the M6b design): the editor files of the mod with the kind
/// <c>modfile</c>, and the whole mission files with the kind <c>file</c>, beside the patches. The restore runs with the
/// edit origin Recovery.
/// </summary>
public sealed class RecoveryFormat3Tests
{
    private static void Edit(EditSession session)
    {
        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
        session.Execute(MissionTestData.Raw(EditCommand.SetLinesFromText(Next, "Cnd_Timer", ["5"])));
        session.Execute(MissionTestData.Raw(EditCommand.SetModFile(GraphPath, GraphText)));
    }

    [Fact]
    public void TheRecoveryFileKeepsEachKindAndTheRestoreIsOneEntry()
    {
        using var direct = WholeFileScriptTests.Workspace();
        var directSession = direct.Session("act");
        Edit(directSession);
        Assert.True(directSession.Save().Saved);
        using var ws = WholeFileScriptTests.Workspace();
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        var session = ws.Session("act");
        Edit(session);
        Assert.Empty(session.WriteRecovery());
        session.Dispose();

        var json = JsonNode.Parse(File.ReadAllText(RecoveryFile.PathOf(ws.ModFolder("act"))))!;
        Assert.Equal(3, (int)json["formatVersion"]!);
        Assert.Equal([("file", ScriptPath), ("modfile", GraphPath), ("patch", GoodsPath)],
            json["files"]!.AsArray().Select(f => ((string)f!["kind"]!, (string)f["path"]!)).OrderBy(f => f.Item1));

        var restored = ws.Session("act");
        Assert.True(restored.HasRecovery);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act"), exclude: SaveTransaction.FolderName));
        Assert.Empty(restored.RestoreRecovery());
        Assert.Equal(EditOrigin.Recovery, restored.UndoOrigin);
        Assert.Equal(["300"], Values(restored, Good("g1"), "price"));
        Assert.Equal(["5"], Values(restored, Next, "Cnd_Timer"));
        Assert.Equal(GraphText, restored.ModFileText(GraphPath));

        restored.Undo();
        Assert.Equal(["100"], Values(restored, Good("g1"), "price"));
        Assert.Equal(["3"], Values(restored, Next, "Cnd_Timer"));
        Assert.Null(restored.ModFileText(GraphPath));
        Assert.False(restored.CanUndo);
        restored.Redo();
        Assert.True(restored.Save().Saved);
        Assert.Equal(TestWorkspace.Files(direct.ModFolder("act")), TestWorkspace.Files(ws.ModFolder("act")));
    }

    [Fact]
    public void ASessionWithOnlyPatchesStillWritesFormat2()
    {
        using var ws = WholeFileScriptTests.Workspace();
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));

        Assert.Empty(session.WriteRecovery());

        var json = JsonNode.Parse(File.ReadAllText(RecoveryFile.PathOf(ws.ModFolder("act"))))!;
        Assert.Equal(2, (int)json["formatVersion"]!);
    }

    [Fact]
    public void AFormat2FileWithAnEditorFileIsNotAValidRecoveryFile()
    {
        using var ws = WholeFileScriptTests.Workspace();
        var file = RecoveryFile.PathOf(ws.ModFolder("act"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $$"""
            { "formatVersion": 2, "files": [ { "path": "{{GraphPath.Replace(@"\", @"\\")}}", "kind": "modfile", "text": "x" } ] }
            """);
        var session = ws.Session("act");

        var problem = Assert.Single(session.RestoreRecovery());

        Assert.EndsWith("this is not a recovery file of this editor version.", problem.Message);
        Assert.Null(session.ModFileText(GraphPath));
    }

    [Theory]
    [InlineData(@"patches\DATA\EQUIPMENT\goods.ini")]
    [InlineData(@"..\x.ini")]
    public void AnEditorFileOutsideItsFoldersIsNotAValidRecoveryFile(string path)
    {
        using var ws = WholeFileScriptTests.Workspace();
        var file = RecoveryFile.PathOf(ws.ModFolder("act"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $$"""
            { "formatVersion": 3, "files": [ { "path": "{{path.Replace(@"\", @"\\")}}", "kind": "modfile", "text": "x" } ] }
            """);
        var session = ws.Session("act");

        Assert.EndsWith("this is not a recovery file of this editor version.", Assert.Single(session.RestoreRecovery()).Message);
    }

    [Fact]
    public void FilesThatChangedAfterTheRecoveryFileDoNotRestore()
    {
        using var ws = WholeFileScriptTests.Workspace(new() { [WholeFile] = Script, [GraphPath] = GraphText });
        var session = ws.Session("act");
        session.Execute(MissionTestData.Raw(EditCommand.SetLinesFromText(Next, "Cnd_Timer", ["5"])));
        session.Execute(MissionTestData.Raw(EditCommand.SetModFile(GraphPath, "[Graph]\r\nformat = 1\r\n")));
        Assert.Empty(session.WriteRecovery());
        session.Dispose();
        File.AppendAllText(Path.Combine(ws.ModFolder("act"), GraphPath), "; changed\r\n");
        File.AppendAllText(Path.Combine(ws.ModFolder("act"), WholeFile), "; changed\r\n");

        var restored = ws.Session("act");
        var problems = restored.RestoreRecovery();

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal("The mod changed after the editor wrote the recovery file, so the edits " +
                                               "do not apply to it.", p.Message));
        Assert.Equal(["3"], Values(restored, Next, "Cnd_Timer"));
        Assert.False(restored.CanUndo);
        Assert.True(restored.HasRecovery);
    }
}

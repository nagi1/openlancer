using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The editor files of the mod as files of the edit session (section 6.8 of the M6b design): graph files and mission
/// prefabs. The edit history, the save transaction and the recovery file cover them, as they cover the patches.
/// </summary>
public sealed class ModFileDocumentTests
{
    internal const string GraphPath = @"graphs\DATA\MISSIONS\OL_TEST\ol_test.ini";
    internal const string GraphText = "[Graph]\r\nscript = DATA\\MISSIONS\\OL_TEST\\ol_test.ini\r\nformat = 1\r\n";

    [Fact]
    public void ACommandSetsTheTextOfAnEditorFileAndUndoAndRedoPutItBack()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Assert.Null(session.ModFileText(GraphPath));

        session.Execute(EditCommand.SetModFile(GraphPath, GraphText));

        Assert.Equal(GraphText, session.ModFileText(GraphPath));
        Assert.True(session.IsDirty);
        Assert.Equal($"Set {GraphPath}", session.UndoLabel);
        // The edited paths hold the key of the editor file, so a dirty session always has an edited path.
        Assert.Equal([ModFileDocument.KeyPrefix + GraphPath], session.EditedPaths);
        session.Undo();
        Assert.Null(session.ModFileText(GraphPath));
        session.Redo();
        Assert.Equal(GraphText, session.ModFileText("graphs/DATA/MISSIONS/OL_TEST/ol_test.ini"));
    }

    [Fact]
    public void TheChangeOfAnEditAnUndoAndARedoNamesTheEditorFiles()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        session.Execute(EditCommand.SetModFile(GraphPath, GraphText));
        session.Undo();
        session.Redo();
        session.Execute(Set(Good("g1"), "price", new Int32Value(300)));

        Assert.Equal(4, changes.Count);
        Assert.All(changes.Take(3), c => Assert.Equal([GraphPath], c.ModFiles));
        Assert.All(changes.Take(3), c => Assert.Empty(c.Sections));
        Assert.Empty(changes[3].ModFiles);
    }

    /// <summary>A save that deletes an editor file stops after its commit point. The next open completes it, so the
    /// file is gone.</summary>
    [Fact]
    public void AnOpenCompletesASaveThatDeletesAnEditorFile()
    {
        for (var k = 0; k <= 2; k++)
        {
            using var ws = Workspace(new() { [GraphPath] = GraphText });
            var file = Path.Combine(ws.ModFolder("act"), GraphPath);
            var session = ws.Session("act");
            session.Execute(EditCommand.SetModFile(GraphPath, null));
            session.Execute(Set(Good("g1"), "price", new Int32Value(300)));
            var stop = k;
            Assert.Throws<EditorStopped>(() => session.Save(false, done =>
            {
                if (done == stop)
                    throw new EditorStopped();
            }));
            session.Abandon();

            var reopened = ws.Session("act");

            Assert.Empty(reopened.OpenProblems);
            Assert.False(File.Exists(file));
            Assert.Null(reopened.ModFileText(GraphPath));
            Assert.Equal(["300"], Values(reopened, Good("g1"), "price"));
        }
    }

    [Fact]
    public void ASaveWritesTheFileAndAFileWithoutTextGoesWithTheEmptyFoldersBelowItsFolder()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var file = Path.Combine(ws.ModFolder("act"), "graphs", "DATA", "MISSIONS", "OL_TEST", "ol_test.ini");

        session.Execute(EditCommand.SetModFile(GraphPath, GraphText + "; Ünïcode\r\n"));
        var saved = session.Save();

        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(Assert.Single(saved.Written)));
        Assert.Equal(Encoding.UTF8.GetBytes(GraphText + "; Ünïcode\r\n"), File.ReadAllBytes(file));
        Assert.False(session.IsDirty);

        session.Execute(EditCommand.SetModFile(GraphPath, null));
        var deleted = session.Save();

        Assert.True(deleted.Saved, string.Join("\n", deleted.Problems));
        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(Assert.Single(deleted.Deleted)));
        Assert.False(File.Exists(file));
        // The empty folders below graphs\ go, as below patches\.
        Assert.False(Directory.Exists(Path.Combine(ws.ModFolder("act"), "graphs", "DATA")));
        Assert.Null(session.ModFileText(GraphPath));
    }

    [Fact]
    public void ATextThatEqualsTheFileWritesNothing()
    {
        using var ws = Workspace(new() { [GraphPath] = GraphText });
        var session = ws.Session("act");
        var text = session.ModFileText(GraphPath);
        Assert.Equal(ws.ReadModFile(GraphPath), text);

        session.Execute(EditCommand.SetModFile(GraphPath, "changed"));
        session.Execute(EditCommand.SetModFile(GraphPath, text));
        var result = session.Save();

        Assert.True(result.Saved);
        Assert.Empty(result.Written);
        Assert.Empty(result.Deleted);
    }

    [Fact]
    public void ABatchChangesAPatchAndAnEditorFileInOneEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var batch = session.BeginBatch("Price and graph");
        batch.Run(Set(Good("g1"), "price", new Int32Value(300)));
        batch.Run(EditCommand.SetModFile(GraphPath, GraphText));
        var result = batch.Commit();

        Assert.Equal([GoodsPath], result.Files);
        Assert.Equal("Price and graph", session.UndoLabel);
        session.Undo();
        Assert.Null(session.ModFileText(GraphPath));
        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        session.Redo();
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Equal(2, saved.Written.Count);
        Assert.Contains("price = 300", ws.ReadModPatch(GoodsPath));
        Assert.Equal(GraphText, ws.ReadModFile(GraphPath));
    }

    [Theory]
    [InlineData(@"patches\DATA\EQUIPMENT\goods.ini")]
    [InlineData("mod.json")]
    [InlineData(@"graphs\x.txt")]
    [InlineData(@"graphs\..\patches\x.ini")]
    [InlineData(@"C:\graphs\x.ini")]
    [InlineData(@"\graphs\x.ini")]
    [InlineData(@"prefabs\x.ini")]
    [InlineData("graphs")]
    public void AnEditorFileIsInTheFoldersOfTheEditorFiles(string path)
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        Assert.Null(ModFileDocument.Normalize(path));
        Assert.Throws<ArgumentException>(() => session.Execute(EditCommand.SetModFile(path, "x")));
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData("graphs/DATA/MISSIONS/M02/m02.ini", @"graphs\DATA\MISSIONS\M02\m02.ini")]
    [InlineData(@"prefabs\missions\ol_gate_ambush.ini", @"prefabs\missions\ol_gate_ambush.ini")]
    public void ThePathOfAnEditorFileHasItsNormalForm(string path, string normal) =>
        Assert.Equal(normal, ModFileDocument.Normalize(path));

    [Fact]
    public void AFileThatChangedOutsideTheEditorStopsTheSave()
    {
        using var ws = Workspace(new() { [GraphPath] = GraphText });
        var session = ws.Session("act");
        session.Execute(EditCommand.SetModFile(GraphPath, "[Graph]\r\nformat = 1\r\n"));
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), GraphPath), "changed outside");

        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Single(result.ChangedOnDisk);
        Assert.Equal("changed outside", ws.ReadModFile(GraphPath));
        Assert.True(session.Save(overwrite: true).Saved);
        Assert.Equal("[Graph]\r\nformat = 1\r\n", ws.ReadModFile(GraphPath));
    }

    [Fact]
    public void ASessionWithoutAnActiveModChangesNoEditorFile()
    {
        using var ws = Workspace();
        var session = ws.Session(null);

        Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.SetModFile(GraphPath, GraphText)));
        Assert.Null(session.ModFileText(GraphPath));
    }

    internal static TestWorkspace Workspace(Dictionary<string, string>? modFiles = null) =>
        EditSessionTests.Workspace(modFiles ?? new());

    private sealed class EditorStopped : Exception;
}

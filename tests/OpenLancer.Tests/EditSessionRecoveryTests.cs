using System;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The recovery file keeps unsaved edits when the window closes without a prompt (decision D8, without local patch
/// 0017).
/// </summary>
public sealed class EditSessionRecoveryTests
{
    private static TestWorkspace Workspace() =>
        EditSessionTests.Workspace(new() { [@"patches\" + GoodsPath] = "; prices\n[Good]\nnickname = g1\nprice = 120\n" });

    private static EditSession Edit(TestWorkspace ws)
    {
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "volume", new Int32Value(3)));
        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(11)));
        session.Execute(EditCommand.SetText(StringKind.String, "n", "N"));
        return session;
    }

    /// <summary>The window closes: the editor writes the recovery file, then the session releases the lock of the
    /// mod.</summary>
    private static void Close(EditSession session)
    {
        Assert.Empty(session.WriteRecovery());
        session.Dispose();
    }

    [Fact]
    public void AnOpenRestoresTheUnsavedEditsOfTheRecoveryFile()
    {
        using var direct = Workspace();
        Assert.True(Edit(direct).Save().Saved);
        using var ws = Workspace();
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        Close(Edit(ws));
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));
        Assert.True(File.Exists(recovery));

        // The window closes. The next session finds the edits.
        var session = ws.Session("act");

        Assert.True(session.HasRecovery);
        Assert.False(session.IsDirty);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act"), exclude: SaveTransaction.FolderName));
        Assert.Empty(session.RestoreRecovery());
        Assert.False(session.HasRecovery);
        Assert.False(File.Exists(recovery));
        Assert.True(session.IsDirty);
        Assert.Equal("Restore unsaved edits", session.UndoLabel);
        Assert.Equal(["3"], Values(session, Good("g1"), "volume"));
        Assert.Equal(["11"], Values(session, Gun("gun1"), "damage"));
        Assert.Equal("N", session.Text(new StringPlaceholder(StringKind.String, "n")));

        Assert.True(session.Save().Saved);
        Assert.Equal(TestWorkspace.Files(direct.ModFolder("act")), TestWorkspace.Files(ws.ModFolder("act")));

        // An undo of the restore gives the files of the mod before the edits.
        session.Undo();
        Assert.True(session.Save().Saved);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act")));
    }

    /// <summary>The edits apply to the files that they changed. When those files change, the restore refuses.</summary>
    [Fact]
    public void AnOldRecoveryFileIsNotRestored()
    {
        using var ws = Workspace();
        Close(Edit(ws));
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));
        var recoveryBytes = File.ReadAllBytes(recovery);
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath), "[Good]\r\nnickname = g1\r\nprice = 125\r\n");

        var session = ws.Session("act");

        Assert.Contains("changed after", Assert.Single(session.RestoreRecovery()).Message);
        Assert.Equal(["1"], Values(session, Good("g1"), "volume"));
        Assert.Equal(["10"], Values(session, Gun("gun1"), "damage"));
        Assert.False(session.CanUndo);
        Assert.True(session.HasRecovery);
        Assert.Empty(session.WriteRecovery());
        Assert.Equal(recoveryBytes, File.ReadAllBytes(recovery));

        session.DiscardRecovery();
        Assert.False(File.Exists(recovery));
        Assert.Empty(TestWorkspace.SaveFiles(ws.ModFolder("act")));
        session.Execute(Set(Good("g2"), "price", new Int32Value(1)));
        Assert.Empty(session.WriteRecovery());
        Assert.True(File.Exists(recovery));
    }

    /// <summary>
    /// While the recovery file of an earlier session waits for a decision, the session takes no edits. So no edit is
    /// ever outside a recovery file, and the earlier edits stay.
    /// </summary>
    [Fact]
    public void ASessionTakesNoEditsUntilTheUserDecidesOnTheRecoveryFile()
    {
        using var ws = Workspace();
        Close(Edit(ws));
        var recoveryBytes = File.ReadAllBytes(RecoveryFile.PathOf(ws.ModFolder("act")));

        var session = ws.Session("act");

        Assert.True(session.HasRecovery);
        Assert.False(session.CanEdit);
        Assert.Contains("Restore or discard", session.Get(Good("g2"))!.ReadOnlyReason);
        Assert.Throws<InvalidOperationException>(() => session.Execute(Set(Good("g2"), "price", new Int32Value(7))));
        Assert.Throws<InvalidOperationException>(() => session.Begin("Type"));
        Assert.Empty(session.WriteRecovery());
        Assert.True(session.Save().Saved);
        Assert.Equal(recoveryBytes, File.ReadAllBytes(RecoveryFile.PathOf(ws.ModFolder("act"))));

        Assert.Empty(session.RestoreRecovery());
        Assert.True(session.CanEdit);
        Assert.Null(session.Get(Good("g2"))!.ReadOnlyReason);
        session.Execute(Set(Good("g2"), "price", new Int32Value(7)));
    }

    /// <summary>The text of the recovery file applies to the strings.json that it changed. When that file changes, the
    /// restore refuses.</summary>
    [Fact]
    public void ARestoreRefusesTextWhoseFileChanged()
    {
        using var ws = Workspace();
        var first = ws.Session("act");
        first.Execute(EditCommand.SetText(StringKind.String, "k", "A"));
        Close(first);
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), "strings.json"), "{ \"strings\": { \"z\": \"Z\" } }");

        var session = ws.Session("act");

        Assert.Contains("changed after", Assert.Single(session.RestoreRecovery()).Message);
        Assert.Null(session.Text(new StringPlaceholder(StringKind.String, "k")));
        Assert.Equal("Z", session.Text(new StringPlaceholder(StringKind.String, "z")));
        Assert.True(session.HasRecovery);
    }

    /// <summary>A recovery file with a text key that strings.json cannot hold is not valid, so nothing is restored.</summary>
    [Fact]
    public void ARecoveryFileWithAnInvalidTextKeyIsNotRestored()
    {
        using var ws = Workspace();
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));
        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
        File.WriteAllText(recovery, "{ \"formatVersion\": 1, \"patches\": [], \"text\": { \"strings\": { \"bad key\": \"x\" } } }");

        var session = ws.Session("act");

        Assert.Contains("not valid", Assert.Single(session.RestoreRecovery()).Message);
        Assert.False(session.CanUndo);
        Assert.True(session.HasRecovery);
    }

    /// <summary>
    /// A change below the active mod stops the save, and the edits go to the recovery file. When the editor cannot
    /// write that file, the save tells that an open of the profile loses the edits.
    /// </summary>
    [Fact]
    public void AStoppedSaveTellsWhenTheRecoveryFileCannotKeepTheEdits()
    {
        using var ws = Workspace();
        var session = Edit(ws);
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));
        // A folder in the place of the recovery file stops its write.
        Directory.CreateDirectory(recovery);
        File.WriteAllText(Path.Combine(ws.Base, GoodsPath), Goods.Replace("price = 200", "price = 3"));

        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Error && p.Source == GoodsPath);
        var errors = result.Problems.Where(p => p.Severity == DiagnosticSeverity.Error && p.Source == recovery).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains("are lost", errors[1].Message);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void ASaveOrAnUndoOfAllEditsDeletesTheRecoveryFile()
    {
        using var ws = Workspace();
        var session = Edit(ws);
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));

        Assert.Empty(session.WriteRecovery());
        Assert.True(session.Save().Saved);
        Assert.False(File.Exists(recovery));

        session.Execute(Set(Good("g2"), "price", new Int32Value(1)));
        Assert.Empty(session.WriteRecovery());
        Assert.True(File.Exists(recovery));
        session.Undo();
        Assert.Empty(session.WriteRecovery());
        Assert.False(File.Exists(recovery));
    }
}

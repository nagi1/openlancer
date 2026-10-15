using System;
using System.IO;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// One editor session at a time edits a mod (step 0 B). A second session on the same mod, as a second editor opens it,
/// is read-only and changes no file of the first session.
/// </summary>
public sealed class ModLockTests
{
    private static string Act(TestWorkspace ws, string relative = "") => Path.Combine(ws.ModFolder("act"), relative);

    private static string SaveFolder(TestWorkspace ws) => Act(ws, SaveTransaction.FolderName);

    [Fact]
    public void ASecondSessionOnTheModIsReadOnlyAndNamesTheFirstSession()
    {
        using var ws = Workspace();
        var first = ws.Session("act");
        first.Execute(Set(Good("g1"), "price", new Int32Value(130)));
        Assert.True(first.Save().Saved);

        // The save completes, and the lock of the first session stays.
        var second = ws.SecondSession("act");

        Assert.True(first.CanEdit);
        Assert.False(second.CanEdit);
        Assert.StartsWith($"Another editor has mod 'act' open (process {Environment.ProcessId}, since ",
            second.ReadOnlyReason);
        Assert.EndsWith("). Close it, then open the profile again.", second.ReadOnlyReason);
        Assert.Equal(second.ReadOnlyReason, second.Get(Good("g1"))!.ReadOnlyReason);
        Assert.Throws<InvalidOperationException>(() => second.Execute(Set(Good("g2"), "price", new Int32Value(1))));
        var refused = second.Save();
        Assert.False(refused.Saved);
        Assert.Equal(second.ReadOnlyReason, Assert.Single(refused.Problems).Message);
        Assert.Equal(["130"], Values(second, Good("g1"), "price"));
    }

    /// <summary>
    /// The open of a second session keeps the staged files of a save of the first session and its recovery file. A
    /// discard or a recovery write of the second session does nothing.
    /// </summary>
    [Fact]
    public void ASecondSessionKeepsTheFilesOfTheFirstSession()
    {
        using var ws = Workspace();
        var first = ws.Session("act");
        first.Execute(Set(Good("g1"), "price", new Int32Value(130)));
        Assert.Empty(first.WriteRecovery());
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));
        var recoveryBytes = File.ReadAllBytes(recovery);
        // A save of the first session wrote a staged file, and did not write its journal yet.
        var staged = Path.Combine(SaveFolder(ws), "0.new");
        File.WriteAllText(staged, "[Good]\r\nnickname = g1\r\nprice = 130\r\n");

        var second = ws.SecondSession("act");

        Assert.True(File.Exists(staged));
        Assert.False(second.HasRecovery);
        Assert.Empty(second.RestoreRecovery());
        second.DiscardRecovery();
        Assert.Empty(second.WriteRecovery());
        Assert.Equal(recoveryBytes, File.ReadAllBytes(recovery));
        Assert.Equal(["0.new", RecoveryFile.FileName], TestWorkspace.SaveFiles(ws.ModFolder("act")));
    }

    /// <summary>
    /// Another editor has a lower mod open and did not complete its save. The open does not complete that save,
    /// because it can run now, and the session is read-only, because the lower files can be half saved.
    /// </summary>
    [Fact]
    public void AnOpenDoesNotCompleteTheSaveOfALowerModThatAnotherEditorHasOpen()
    {
        using var ws = Workspace();
        ws.Mod("low", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nprice = 150" });
        ws.WriteProfile("low", "act");
        var low = ws.ModFolder("low");
        var lowEditor = ws.Session("low");
        var lowGoods = Path.Combine(low, "patches", GoodsPath);
        Assert.Throws<EditorStopped>(() => SaveTransaction.Commit(low,
            [new FileChange(lowGoods, Encoding.ASCII.GetBytes("[Good]\r\nnickname = g2\r\nprice = 160\r\n"))], [],
            done =>
            {
                if (done == 0)
                    throw new EditorStopped();
            }));

        var session = ws.Session("act");

        Assert.True(SaveTransaction.HasJournal(low));
        Assert.Equal(["0.new", "journal"], TestWorkspace.SaveFiles(low));
        Assert.StartsWith("A save of mod 'low' is not complete. Another editor has mod 'low' open (process ",
            Assert.Single(session.OpenProblems).Message);
        Assert.False(session.CanEdit);
        Assert.Equal(["150"], Values(session, Good("g2"), "price"));

        // The editor of the lower mod closes. The next open completes its save.
        lowEditor.Dispose();
        session.Dispose();
        var again = ws.Session("act");
        Assert.Empty(again.OpenProblems);
        Assert.True(again.CanEdit);
        Assert.Equal(["160"], Values(again, Good("g2"), "price"));
    }

    /// <summary>
    /// The end of a session releases the lock and deletes the lock files. A crash leaves the lock files, but they
    /// block nothing: the next session takes edits and completes the save that the crash stopped.
    /// </summary>
    [Fact]
    public void AfterTheEndOrACrashOfASessionTheNextSessionTakesEdits()
    {
        using var ws = Workspace();
        var first = ws.Session("act");
        Assert.True(File.Exists(Path.Combine(SaveFolder(ws), ModLock.FileName)));
        Assert.True(File.Exists(Path.Combine(SaveFolder(ws), ModLock.InfoName)));

        first.Dispose();

        Assert.False(Directory.Exists(SaveFolder(ws)));
        var second = ws.Session("act");
        Assert.True(second.CanEdit);
        second.Execute(Set(Good("g1"), "price", new Int32Value(130)));
        Assert.Throws<EditorStopped>(() => second.Save(false, done =>
        {
            if (done == 0)
                throw new EditorStopped();
        }));

        second.Abandon();

        Assert.True(File.Exists(Path.Combine(SaveFolder(ws), ModLock.FileName)));
        Assert.True(File.Exists(Path.Combine(SaveFolder(ws), ModLock.InfoName)));
        var third = ws.Session("act");
        Assert.True(third.CanEdit);
        Assert.Empty(third.OpenProblems);
        Assert.False(SaveTransaction.HasJournal(ws.ModFolder("act")));
        Assert.Equal(["130"], Values(third, Good("g1"), "price"));
        Assert.Empty(TestWorkspace.SaveFiles(ws.ModFolder("act")));
    }

    /// <summary>
    /// A session without its lock after a dispose or a crash takes no edits and does not save. It does not read, write
    /// or delete the recovery file, because another editor can have the mod open now.
    /// </summary>
    [Fact]
    public void AClosedSessionChangesNoFileOfTheMod()
    {
        using var ws = Workspace();
        foreach (var close in new Action<EditSession>[] { s => s.Dispose(), s => s.Abandon() })
        {
            // A session with unsaved edits. The recovery file keeps the first edit.
            var session = ws.Session("act");
            session.Execute(Set(Good("g1"), "price", new Int32Value(130)));
            Assert.Empty(session.WriteRecovery());
            session.Execute(Set(Good("g1"), "price", new Int32Value(140)));
            var files = TestWorkspace.Files(ws.ModFolder("act"));

            close(session);

            Assert.False(session.CanEdit);
            Assert.Equal("The edit session is closed. Open the profile again.", session.ReadOnlyReason);
            Assert.Throws<InvalidOperationException>(() => session.Execute(Set(Good("g2"), "price", new Int32Value(1))));
            Assert.Throws<InvalidOperationException>(() => session.Begin("Set price"));
            var refused = session.Save();
            Assert.False(refused.Saved);
            Assert.Equal(session.ReadOnlyReason, Assert.Single(refused.Problems).Message);
            Assert.Empty(session.WriteRecovery());
            Assert.Equal(files, TestWorkspace.Files(ws.ModFolder("act")));

            // The next session has the recovery file. When it closes before a restore or a discard, it keeps the file.
            var next = ws.Session("act");
            Assert.True(next.HasRecovery);

            close(next);

            Assert.False(next.HasRecovery);
            Assert.Empty(next.RestoreRecovery());
            next.DiscardRecovery();
            Assert.Equal(files, TestWorkspace.Files(ws.ModFolder("act")));

            var last = ws.Session("act");
            last.DiscardRecovery();
            last.Dispose();
        }
    }

    /// <summary>An open that throws after it took the lock releases the lock before the exception leaves it.</summary>
    [Fact]
    public void AnOpenThatThrowsAfterTheLockLeavesTheLockFree()
    {
        using var ws = Workspace(new() { ["strings.json"] = "{ \"strings\": { \"k\": \"K\" } }" });
        var profile = ModProfile.Load(ws.ProfilePath);
        var strings = Act(ws, "strings.json");
        var valid = File.ReadAllBytes(strings);
        // The session reads the text of the active mod after it took the lock.
        File.WriteAllText(strings, "{ not json");

        Assert.Throws<ModLoadException>(() =>
            EditSession.Open(profile, "act", TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator));

        Assert.False(Directory.Exists(SaveFolder(ws)));
        File.WriteAllBytes(strings, valid);
        Assert.True(ws.Session("act").CanEdit);
    }

    private sealed class EditorStopped : Exception;
}

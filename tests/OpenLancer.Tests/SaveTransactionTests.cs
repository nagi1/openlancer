using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Strings;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// A save writes all its files or none, also when the editor stops in the middle of it (review findings F1 and F2).
/// </summary>
public sealed class SaveTransactionTests
{
    /// <summary>
    /// The save of <see cref="Edit"/>, in this order: replace goods.ini, add market_misc.ini, delete
    /// weapon_equip.ini, replace strings.json.
    /// </summary>
    private const int Steps = 4;

    /// <summary>The [BaseGood] that <see cref="Edit"/> adds.</summary>
    private static readonly SectionRef AddedBaseGood = new(MarketPath, new SectionIdentity("BaseGood", null, null, 1));

    /// <summary>A workspace whose active mod patches goods.ini and weapon_equip.ini, and has a strings.json.</summary>
    private static TestWorkspace Workspace() => EditSessionTests.Workspace(new()
    {
        [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 120",
        [@"patches\" + WeaponsPath] = "[Gun]\nnickname = gun1\ndamage = 20",
        ["strings.json"] = "{ \"strings\": { \"old\": \"Old\" } }",
    });

    private static EditSession Edit(TestWorkspace ws)
    {
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "price", new Int32Value(130)));
        session.Execute(EditCommand.AddSection(MarketPath, "BaseGood",
            [new EntryView("base", [new StringValue("b2")]), new EntryView("marketgood", [new StringValue("g1"), new Int32Value(0)])]));
        // Back to the value of B: the patch of weapon_equip.ini is then empty, so the save deletes it.
        session.Execute(Set(Gun("gun1"), "damage", new Int32Value(10)));
        session.Execute(EditCommand.SetText(StringKind.String, "g1_name", "Good one"));
        return session;
    }

    private static string Act(TestWorkspace ws, string relative) => Path.Combine(ws.ModFolder("act"), relative);

    private static string Journal(string modFolder) => Path.Combine(modFolder, SaveTransaction.FolderName, "journal");

    /// <summary>The files of the active mod after a complete save of <see cref="Edit"/>.</summary>
    private static SortedDictionary<string, string> CompleteSave()
    {
        using var ws = Workspace();
        var result = Edit(ws).Save();
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Equal(3, result.Written.Count);
        Assert.Single(result.Deleted);
        return TestWorkspace.Files(ws.ModFolder("act"));
    }

    /// <summary>Saves <see cref="Edit"/>, and stops the editor after <paramref name="k"/> moves and deletes. The
    /// session then closes the handle of its lock and deletes no file, as a crash does.</summary>
    private static void SaveAndStop(EditSession session, int k)
    {
        Assert.Throws<EditorStopped>(() => session.Save(false, done =>
        {
            if (done == k)
                throw new EditorStopped();
        }));
        session.Abandon();
    }

    /// <summary>
    /// The editor stops after the commit point, after k moves and deletes, for k = 0 to all of them. The next open
    /// completes the save, so the files are the complete new set, and the session shows each saved file.
    /// </summary>
    [Fact]
    public void AnOpenCompletesASaveThatStoppedAfterItsCommitPoint()
    {
        var expected = CompleteSave();
        for (var k = 0; k <= Steps; k++)
        {
            using var ws = Workspace();
            SaveAndStop(Edit(ws), k);

            Assert.True(File.Exists(Journal(ws.ModFolder("act"))), $"k = {k}");
            Assert.NotEqual(expected, TestWorkspace.Files(ws.ModFolder("act")));
            // The staged files are outside patches\, so the half-saved mod still loads in the tools (F2). The merge
            // tells that the save is not complete.
            var halfSaved = Data.Mods.Mod.Load(ws.ModFolder("act"));
            Assert.Contains(ws.Build(halfSaved).Diagnostics,
                d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("is not complete"));

            var reopened = ws.Session("act");

            Assert.Equal(expected, TestWorkspace.Files(ws.ModFolder("act")));
            Assert.Empty(reopened.OpenProblems);
            Assert.True(reopened.CanEdit);
            Assert.Equal(["130"], Values(reopened, Good("g1"), "price"));
            Assert.Equal(["b2"], Values(reopened, AddedBaseGood, "base"));
            Assert.Null(reopened.Get(Gun("gun1"))!.ReadOnlyReason);
            Assert.Equal(["10"], Values(reopened, Gun("gun1"), "damage"));
            Assert.Equal("Good one", reopened.Text(new StringPlaceholder(StringKind.String, "g1_name")));
            Assert.Equal("Old", reopened.Text(new StringPlaceholder(StringKind.String, "old")));
            Assert.DoesNotContain(reopened.EditView.Diagnostics, d => d.Message.Contains("is not complete"));
        }
    }

    /// <summary>The editor stops before the journal exists. The next open deletes the staged files, and the mod stays
    /// as it was.</summary>
    [Fact]
    public void AnOpenDeletesTheFilesOfASaveThatStoppedBeforeItsJournal()
    {
        using var ws = Workspace();
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        var folder = Path.Combine(ws.ModFolder("act"), SaveTransaction.FolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "0.new"), "[Good]\r\nnickname = g1\r\n");
        File.WriteAllText(Path.Combine(folder, "journal.oltmp"), "olsave journal 1\nwrite 0.new patches\\x.ini\n");

        var session = ws.Session("act");

        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act")));
        Assert.Empty(TestWorkspace.SaveFiles(ws.ModFolder("act")));
        Assert.Equal(["120"], Values(session, Good("g1"), "price"));
        // The lock of the session keeps the folder until the session ends.
        session.Dispose();
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>
    /// The open completes the save of a lower mod too, so B shows the saved files of the lower mod. The open holds the
    /// lock of the lower mod only for that recovery, so an editor of the lower mod can open it while the session runs.
    /// </summary>
    [Fact]
    public void AnOpenCompletesTheSaveOfALowerMod()
    {
        using var ws = Workspace();
        ws.Mod("low", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nprice = 150" });
        ws.WriteProfile("low", "act");
        var low = ws.ModFolder("low");
        Assert.Throws<EditorStopped>(() => SaveTransaction.Commit(low,
        [
            new FileChange(Path.Combine(low, "patches", GoodsPath), Encoding.ASCII.GetBytes("[Good]\r\nnickname = g2\r\nprice = 160\r\n")),
            new FileChange(Path.Combine(low, "patches", WeaponsPath), Encoding.ASCII.GetBytes("[Gun]\r\nnickname = gun1\r\nrange = 99\r\n")),
        ], [], done =>
        {
            if (done == 0)
                throw new EditorStopped();
        }));

        var session = ws.Session("act");

        Assert.Empty(session.OpenProblems);
        Assert.Equal(["160"], Values(session, Good("g2"), "price"));
        Assert.Equal((ValueOrigin.LowerMod, "low"), session.Origin(Good("g2"), "price"));
        // The new patch of the lower mod is in B, under the patch of the active mod.
        Assert.Equal(["99"], Values(session, Gun("gun1"), "range"));
        Assert.Equal((ValueOrigin.LowerMod, "low"), session.Origin(Gun("gun1"), "range"));
        Assert.Equal(["20"], Values(session, Gun("gun1"), "damage"));
        Assert.False(File.Exists(Journal(low)));

        // The session of the active mod stays open. The recovery released the lock of the lower mod and deleted its
        // lock files.
        Assert.True(session.CanEdit);
        Assert.False(Directory.Exists(Path.Combine(low, SaveTransaction.FolderName)));
        var lowEditor = ws.Session("low");
        Assert.True(lowEditor.CanEdit);
        lowEditor.Execute(Set(Good("g2"), "price", new Int32Value(170)));
    }

    /// <summary>
    /// A move that fails after the commit point does not undo the save. The next save completes the files first.
    /// </summary>
    [Fact]
    public void TheNextSaveCompletesASaveWhoseMoveFailed()
    {
        var expected = CompleteSave();
        using var ws = Workspace();
        var session = Edit(ws);

        var result = session.Save(false, done =>
        {
            if (done == 2)
                throw new IOException("The file is in use.");
        });

        Assert.True(result.Saved);
        Assert.True(result.Incomplete);
        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Warning && p.Message.Contains("in use"));
        Assert.False(session.IsDirty);
        Assert.NotEqual(expected, TestWorkspace.Files(ws.ModFolder("act")));

        Assert.Equal(20, TestWorkspace.Sections(session.EditView, WeaponsPath)[0]["damage"]![0].ToInt32());

        var next = session.Save();
        Assert.True(next.Saved);
        Assert.False(next.Incomplete);
        Assert.Equal(expected, TestWorkspace.Files(ws.ModFolder("act")));
        // The edit view shows the completed save: the patch of weapon_equip.ini is gone.
        Assert.Equal(10, TestWorkspace.Sections(session.EditView, WeaponsPath)[0]["damage"]![0].ToInt32());
    }

    /// <summary>
    /// After the editor stopped, a file of the save changed: a backup or the user wrote it. The recovery keeps each
    /// such file and reports it. A step that is complete runs again without a change (review finding F2).
    /// </summary>
    [Fact]
    public void ARecoveryKeepsTheFilesThatChangedAfterTheSaveStopped()
    {
        using var ws = Workspace();
        SaveAndStop(Edit(ws), 3);
        // The delete of weapon_equip.ini is complete. A backup puts the file back with other content.
        var weapons = Act(ws, Path.Combine("patches", WeaponsPath));
        Directory.CreateDirectory(Path.GetDirectoryName(weapons)!);
        File.WriteAllText(weapons, "[Gun]\r\nnickname = gun1\r\ndamage = 77\r\n");
        // The move of strings.json is not done yet. The user changes the file.
        var strings = Act(ws, "strings.json");
        File.WriteAllText(strings, "{ \"strings\": { \"mine\": \"Mine\" } }");
        var goods = File.ReadAllBytes(Act(ws, Path.Combine("patches", GoodsPath)));

        var session = ws.Session("act");

        Assert.Equal([weapons, strings], session.OpenProblems.Select(p => p.Source));
        Assert.All(session.OpenProblems, p => Assert.Equal(DiagnosticSeverity.Warning, p.Severity));
        Assert.Equal("[Gun]\r\nnickname = gun1\r\ndamage = 77\r\n", File.ReadAllText(weapons));
        Assert.Equal("{ \"strings\": { \"mine\": \"Mine\" } }", File.ReadAllText(strings));
        Assert.Equal(goods, File.ReadAllBytes(Act(ws, Path.Combine("patches", GoodsPath))));
        Assert.Empty(TestWorkspace.SaveFiles(ws.ModFolder("act")));
        Assert.True(session.CanEdit);
    }

    /// <summary>
    /// The staged file of a move is gone. The move counts as done only when its target has the new content, so the
    /// recovery reports it.
    /// </summary>
    [Fact]
    public void ARecoveryReportsAMoveWhoseStagedFileIsGone()
    {
        using var ws = Workspace();
        SaveAndStop(Edit(ws), 0);
        File.Delete(Act(ws, Path.Combine(SaveTransaction.FolderName, "1.new")));

        var session = ws.Session("act");

        var market = Act(ws, Path.Combine("patches", MarketPath));
        Assert.Equal(market, Assert.Single(session.OpenProblems).Source);
        Assert.False(File.Exists(market));
        Assert.Null(session.Get(AddedBaseGood));
        Assert.Equal(["130"], Values(session, Good("g1"), "price"));
    }

    /// <summary>
    /// After the editor stopped, a folder took the place of a new file of the save. The recovery keeps the folder,
    /// reports it, and completes the other steps. So the folder does not stop each open of the profile.
    /// </summary>
    [Fact]
    public void ARecoveryKeepsAFolderInThePlaceOfANewFile()
    {
        using var ws = Workspace();
        SaveAndStop(Edit(ws), 0);
        var market = Act(ws, Path.Combine("patches", MarketPath));
        Directory.CreateDirectory(market);

        var session = ws.Session("act");

        var problem = Assert.Single(session.OpenProblems);
        Assert.Equal((DiagnosticSeverity.Warning, market), (problem.Severity, problem.Source));
        Assert.Contains("is a folder", problem.Message);
        Assert.True(Directory.Exists(market));
        Assert.True(session.CanEdit);
        Assert.Equal(["130"], Values(session, Good("g1"), "price"));
        Assert.False(File.Exists(Act(ws, Path.Combine("patches", WeaponsPath))));
        Assert.Empty(TestWorkspace.SaveFiles(ws.ModFolder("act")));
    }

    /// <summary>
    /// An open cannot complete the save of the active mod, because a file is in use. The profile still opens. The
    /// session is read-only and tells why. The next open completes the save.
    /// </summary>
    [Fact]
    public void AnOpenThatCannotCompleteTheSaveOfTheActiveModIsReadOnly()
    {
        var expected = CompleteSave();
        using var ws = Workspace();
        SaveAndStop(Edit(ws), 0);

        EditSession session;
        using (new FileStream(Act(ws, Path.Combine("patches", WeaponsPath)), FileMode.Open, FileAccess.Read, FileShare.None))
            session = ws.Session("act");

        var problem = Assert.Single(session.OpenProblems);
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("mod 'act' is not complete", problem.Message);
        Assert.False(session.CanEdit);
        Assert.Equal(problem.Message, session.Get(Good("g2"))!.ReadOnlyReason);
        Assert.Throws<InvalidOperationException>(() => session.Execute(Set(Good("g2"), "price", new Int32Value(1))));
        Assert.False(session.Save().Saved);

        session.Dispose();
        var again = ws.Session("act");
        Assert.Empty(again.OpenProblems);
        Assert.Equal(expected, TestWorkspace.Files(ws.ModFolder("act")));
    }

    /// <summary>
    /// A save of a lower mod that the open cannot complete makes the session read-only, because B can be half saved. A
    /// later mod does not change the edit view, so the session stays editable.
    /// </summary>
    [Fact]
    public void AnOpenThatCannotCompleteTheSaveOfAnotherModStopsTheEditsOnlyForALowerMod()
    {
        using var ws = Workspace();
        ws.Mod("other", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nprice = 150" });
        var other = ws.ModFolder("other");
        var otherGoods = Path.Combine(other, "patches", GoodsPath);
        Assert.Throws<EditorStopped>(() => SaveTransaction.Commit(other,
            [new FileChange(otherGoods, Encoding.ASCII.GetBytes("[Good]\r\nnickname = g2\r\nprice = 160\r\n"))], [], done =>
            {
                if (done == 0)
                    throw new EditorStopped();
            }));

        using (new FileStream(otherGoods, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ws.WriteProfile("act", "other");
            var later = ws.Session("act");
            Assert.Contains("mod 'other' is not complete", Assert.Single(later.OpenProblems).Message);
            Assert.True(later.CanEdit);
            later.Dispose();

            ws.WriteProfile("other", "act");
            var lower = ws.Session("act");
            Assert.Contains("mod 'other' is not complete", Assert.Single(lower.OpenProblems).Message);
            Assert.False(lower.CanEdit);
            Assert.Contains("mod 'other' is not complete", lower.Get(Good("g1"))!.ReadOnlyReason);
        }
    }

    /// <summary>A journal that names a file outside the mod folder is refused, and nothing moves or goes.</summary>
    [Fact]
    public void AJournalOutsideTheModFolderIsRefused()
    {
        using var ws = Workspace();
        var folder = Path.Combine(ws.ModFolder("act"), SaveTransaction.FolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "0.new"), "x");
        var staged = ContentHash.Of("x"u8.ToArray());
        File.WriteAllText(Path.Combine(folder, "journal"), $"olsave journal 1\nwrite 0.new - {staged} ..\\..\\outside.ini\n");

        Assert.Throws<InvalidDataException>(() => SaveTransaction.Recover(ws.ModFolder("act"), []));
        Assert.False(File.Exists(Path.Combine(ws.Root, "outside.ini")));
        Assert.True(File.Exists(Path.Combine(folder, "0.new")));

        // A delete outside the mod folder: the file has the hash of the journal, so only the folder check keeps it.
        var outside = Path.Combine(ws.Root, "outside.ini");
        File.WriteAllText(outside, "keep");
        File.WriteAllText(Path.Combine(folder, "journal"),
            $"olsave journal 1\ndelete {ContentHash.OfFile(outside)} ..\\..\\outside.ini\n");
        Assert.Throws<InvalidDataException>(() => SaveTransaction.Recover(ws.ModFolder("act"), []));
        Assert.True(File.Exists(outside));

        // A staged file is always in the .olsave folder.
        File.WriteAllText(Path.Combine(ws.Root, "outside.new"), "x");
        File.WriteAllText(Path.Combine(folder, "journal"), $"olsave journal 1\nwrite ..\\..\\..\\outside.new - {staged} patches\\x.ini\n");
        Assert.Throws<InvalidDataException>(() => SaveTransaction.Recover(ws.ModFolder("act"), []));
        Assert.True(File.Exists(Path.Combine(ws.Root, "outside.new")));
    }

    /// <summary>A file that the save must replace is read-only. The save writes nothing.</summary>
    [Fact]
    public void AReadOnlyFileStopsTheSaveBeforeItsJournal()
    {
        using var ws = Workspace();
        var session = Edit(ws);
        var weapons = Act(ws, Path.Combine("patches", WeaponsPath));
        File.SetAttributes(weapons, FileAttributes.ReadOnly);
        try
        {
            var before = TestWorkspace.Files(ws.ModFolder("act"));

            var result = session.Save();

            Assert.False(result.Saved);
            Assert.Contains("read-only", Assert.Single(result.Problems).Message);
            Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act")));
            Assert.True(session.IsDirty);
        }
        finally
        {
            File.SetAttributes(weapons, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// A folder in the place of a target, or a file in the place of a folder of a target, makes a move fail for good.
    /// The save finds it before its journal and writes nothing.
    /// </summary>
    [Fact]
    public void APathThatCannotTakeTheFileStopsTheSaveBeforeItsJournal()
    {
        using var ws = Workspace();
        var session = Edit(ws);
        Directory.CreateDirectory(Act(ws, Path.Combine("patches", MarketPath)));
        var before = TestWorkspace.Files(ws.ModFolder("act"));

        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains("is a folder", Assert.Single(result.Problems).Message);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act")));
        Assert.Empty(TestWorkspace.SaveFiles(ws.ModFolder("act")));
        Assert.True(session.IsDirty);

        File.WriteAllText(Act(ws, @"patches\DATA\SHIPS"), "a file");
        var error = Assert.Throws<IOException>(() => SaveTransaction.Commit(ws.ModFolder("act"),
            [new FileChange(Act(ws, Path.Combine("patches", ShipsPath)), [1])], []));
        Assert.Contains("is a file", error.Message);
        Assert.Empty(TestWorkspace.SaveFiles(ws.ModFolder("act")));
    }

    private sealed class EditorStopped : Exception;
}

using System.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The write guard of the edit session (decisions D204 and D214): an active mod or a profile file in the base folder
/// makes the session read-only, and the session takes no lock and writes no file there.
/// </summary>
public sealed class EditSessionGuardTests
{
    private static EditSession Open(string profilePath, string? activeMod) =>
        EditSession.Open(ModProfile.Load(profilePath), activeMod, TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

    [Fact]
    public void AnActiveModInTheBaseFolderOpensReadOnlyWithTheReason()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"mods\act\mod.json", """{ "id": "act", "formatVersion": 1 }""");
        Directory.CreateDirectory(Path.GetDirectoryName(ws.ProfilePath)!);
        File.WriteAllText(ws.ProfilePath, """{ "base": "../base", "mods": [ "../base/mods/act" ] }""");

        using var session = Open(ws.ProfilePath, "act");

        Assert.False(session.CanEdit);
        Assert.Equal(GameTexts.ActiveModInGame, session.ReadOnlyReason);
    }

    [Fact]
    public void AnActiveModInTheBaseFolderTakesNoLockAndWritesNoFile()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"mods\act\mod.json", """{ "id": "act", "formatVersion": 1 }""");
        Directory.CreateDirectory(Path.GetDirectoryName(ws.ProfilePath)!);
        File.WriteAllText(ws.ProfilePath, """{ "base": "../base", "mods": [ "../base/mods/act" ] }""");
        var before = FakeGame.Snapshot(ws.Base);

        using (Open(ws.ProfilePath, "act"))
        {
            Assert.False(Directory.Exists(Path.Combine(ws.Base, "mods", "act", SaveTransaction.FolderName)));
        }

        Assert.Equal(before, FakeGame.Snapshot(ws.Base));
    }

    [Fact]
    public void AProfileFileInTheBaseFolderOpensReadOnlyWithTheReason()
    {
        using var ws = new TestWorkspace();
        ws.Mod("act", new());
        var profile = Path.Combine(ws.Base, "inside.json");
        File.WriteAllText(profile, """{ "base": ".", "mods": [ "../mods/act" ] }""");

        using var session = Open(profile, "act");

        Assert.Equal(GameTexts.ProfileInGame, session.ReadOnlyReason);
        Assert.False(Directory.Exists(Path.Combine(ws.ModFolder("act"), SaveTransaction.FolderName)));
    }

    [Fact]
    public void TheSaveJournalOfAModInTheBaseFolderStaysAndTheOpenReportsIt()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"mods\act\mod.json", """{ "id": "act", "formatVersion": 1 }""");
        ws.WriteBase(@"mods\act\.olsave\journal", "olsave journal 1\n");
        Directory.CreateDirectory(Path.GetDirectoryName(ws.ProfilePath)!);
        File.WriteAllText(ws.ProfilePath, """{ "base": "../base", "mods": [ "../base/mods/act" ] }""");
        var before = FakeGame.Snapshot(ws.Base);

        using (var session = Open(ws.ProfilePath, "act"))
        {
            Assert.Equal(GameTexts.SaveNotRecovered("act", modInGame: true), Assert.Single(session.OpenProblems).Message);
        }

        Assert.Equal(before, FakeGame.Snapshot(ws.Base));
    }

    [Fact]
    public void AProfileFileInTheBaseFolderCompletesNoSaveOfAModOutsideIt()
    {
        using var ws = new TestWorkspace();
        ws.Mod("act", new());
        var journal = Path.Combine(ws.ModFolder("act"), SaveTransaction.FolderName, "journal");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "olsave journal 1\n");
        var profile = Path.Combine(ws.Base, "inside.json");
        File.WriteAllText(profile, """{ "base": ".", "mods": [ "../mods/act" ] }""");

        using var session = Open(profile, "act");

        Assert.Equal(GameTexts.SaveNotRecovered("act", modInGame: false), Assert.Single(session.OpenProblems).Message);
        Assert.True(SaveTransaction.HasJournal(ws.ModFolder("act")));
    }

    [Fact]
    public void AModOutsideTheBaseFolderStillTakesItsLock()
    {
        using var ws = EditSessionTests.Workspace();

        using var session = Open(ws.ProfilePath, "act");

        Assert.True(session.CanEdit);
        Assert.True(File.Exists(Path.Combine(ws.ModFolder("act"), SaveTransaction.FolderName, ModLock.FileName)));
    }
}

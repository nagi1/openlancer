using System;
using System.IO;
using OpenLancer.Data.PlayCopies;
using Xunit;
using static OpenLancer.Tests.PlayGame;

namespace OpenLancer.Tests;

/// <summary><c>oltool install</c>, <c>uninstall</c> and <c>playcopy</c> on a fake game (section 8.9 of the M11 design).
/// The tests run the oltool of the test folder. It starts no program.</summary>
public sealed class OltoolInstallTests : IDisposable
{
    private readonly PlayGame game = new();

    public OltoolInstallTests() =>
        // Freelancer 1.0 by the file version of Common.dll, so the embedded file lists give the target vanilla-1.1.
        game.Game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(new Version(1, 0, 1223, 11)));

    public void Dispose() => game.Dispose();

    private static (int Exit, string Output, string Errors) Run(params string[] arguments) =>
        OltoolExportTests.Run(arguments);

    [Fact]
    public void TheUsageNamesTheCommands()
    {
        var (exit, _, errors) = Run("install");

        Assert.Equal(2, exit);
        Assert.Contains("install <profile.json> --game <folder> [--into <play copy>] [--full-copy]", errors);
        Assert.Contains("playcopy check <play copy>", errors);
        Assert.Equal(2, Run("install", "p.json").Exit);
        Assert.Equal(2, Run("playcopy", "check").Exit);
        Assert.Equal(2, Run("uninstall").Exit);
    }

    [Fact]
    public void TheExitCodesTellTheResult()
    {
        var profile = game.Profile(game.Export.GoodsMod(GamePlayRig.GoodsPatch));

        var installed = Run("install", profile, "--game", game.Root);

        Assert.True(installed.Exit == 0, installed.Errors);
        Assert.Contains(PlayCopyTexts.Make("Freelancer 1.0", game.CopyFolder), installed.Output);
        Assert.Contains($"Play copy: {game.CopyFolder}", installed.Output);
        Assert.Contains($"Program: {game.CopyPath(ProgramPath)}", installed.Output);
        Assert.Contains("price = 5600", File.ReadAllText(game.CopyPath(ExportGame.GoodsPath)));
        var clean = Run("playcopy", "check", game.CopyFolder);
        Assert.True(clean.Exit == 0, clean.Errors);
        Assert.Contains("0 differences", clean.Output);

        using (var stream = new FileStream(game.Game.PathOf(SoundPath), FileMode.Append, FileAccess.Write))
            stream.Write(" and more"u8);
        var changed = Run("playcopy", "check", game.CopyFolder);
        Assert.Equal(1, changed.Exit);
        Assert.Contains("changed after OpenLancer made the play copy", changed.Errors);

        var uninstalled = Run("uninstall", game.CopyFolder);
        Assert.True(uninstalled.Exit == 0, uninstalled.Errors);
        Assert.Equal(File.ReadAllBytes(game.Game.PathOf(ExportGame.GoodsPath)),
            File.ReadAllBytes(game.CopyPath(ExportGame.GoodsPath)));

        var removed = Run("playcopy", "remove", game.CopyFolder);
        Assert.True(removed.Exit == 0, removed.Errors);
        Assert.False(Directory.Exists(game.CopyFolder));
        var notACopy = Run("playcopy", "remove", game.Game.Folder);
        Assert.Equal(2, notACopy.Exit);
        Assert.Contains("is not an OpenLancer play copy", notACopy.Errors);
        Assert.True(Directory.Exists(game.Root));
        Assert.Equal(2, Run("install", profile, "--game", Path.Combine(game.Game.Folder, "none")).Exit);
    }

    [Fact]
    public void ARefusedModGivesExitOne()
    {
        var profile = game.Profile(game.Export.Mod("runtime", [], """, "tier": 2"""));

        var (exit, _, errors) = Run("install", profile, "--game", game.Root);

        Assert.Equal(1, exit);
        Assert.Contains("Mod 'runtime' is a tier-2 mod.", errors);
        Assert.False(Directory.Exists(game.CopyFolder));
    }

    [Fact]
    public void APlayCopyInAModFolderOrTheProfileFolderIsRefused()
    {
        var mod = game.Export.GoodsMod(GamePlayRig.GoodsPatch);
        var profile = game.Profile(mod);

        foreach (var (folder, other) in new[]
                 {
                     (Path.Combine(mod.Folder, "play"), mod.Folder),
                     (Path.Combine(Path.GetDirectoryName(profile)!, "play"), Path.GetDirectoryName(profile)!),
                 })
        {
            game.Track(folder);

            var (exit, _, errors) = Run("install", profile, "--game", game.Root, "--into", folder, "--full-copy");

            Assert.Equal(2, exit);
            Assert.Contains(PlayCopyTexts.OverlapsProtected(folder, other), errors);
            Assert.False(Directory.Exists(folder));
        }
    }

    [Fact]
    public void AFullCopyGoesIntoTheFolderOfInto()
    {
        var profile = game.Profile(game.Export.GoodsMod(GamePlayRig.GoodsPatch));
        var folder = Path.Combine(game.Game.Folder, "full test copy");
        game.Track(folder);

        var (exit, output, errors) = Run("install", profile, "--game", game.Root, "--into", folder, "--full-copy");

        Assert.True(exit == 0, errors);
        Assert.Contains($"Play copy: {folder}", output);
        Assert.Equal(PlayCopyMode.Full, PlayCopyRecord.Read(folder)!.Mode);
        Assert.False(SameFile(game.Game.PathOf(ModelPath), Path.Combine(folder, ModelPath)));
    }
}

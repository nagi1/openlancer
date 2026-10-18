using System.IO;
using OpenLancer.Data.Games;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>Root finding of the game link (section 6.1 of the M11a design): which folder holds the game.</summary>
public sealed class GameRootTests
{
    [Fact]
    public void AFolderWithExeFreelancerIniIsTheRoot()
    {
        using var game = new FakeGame();

        var report = Detect(game.Root);

        Assert.Equal(game.Root, report.Root);
        Assert.Equal(game.Root, report.Folder);
    }

    [Fact]
    public void TheExeFolderGivesItsParent()
    {
        using var game = new FakeGame();

        Assert.Equal(game.Root, Detect(game.PathOf("EXE")).Root);
    }

    [Fact]
    public void TheDataFolderGivesItsParent()
    {
        using var game = new FakeGame();

        Assert.Equal(game.Root, Detect(game.PathOf("DATA")).Root);
    }

    [Fact]
    public void AFolderTwoLevelsDownGivesTheRoot()
    {
        using var game = new FakeGame();

        Assert.Equal(game.Root, Detect(game.PathOf(@"DATA\EQUIPMENT")).Root);
    }

    [Fact]
    public void AFolderWithBaseGivesBase()
    {
        // The developer layout: GameData\base.
        using var game = new FakeGame("base");

        Assert.Equal(game.Root, Detect(game.Folder).Root);
    }

    [Fact]
    public void OneGameOneLevelDownIsTheRoot()
    {
        using var game = new FakeGame();
        Directory.CreateDirectory(Path.Combine(game.Folder, "Other"));

        Assert.Equal(game.Root, Detect(game.Folder).Root);
    }

    [Fact]
    public void TwoGamesOneLevelDownNameBoth()
    {
        using var game = new FakeGame("Freelancer");
        CopyFolder(game.Root, Path.Combine(game.Folder, "Freelancer HD"));

        var report = Detect(game.Folder);

        Assert.Equal(GameKind.NotAGame, report.Kind);
        Assert.Equal(GameTexts.TwoGames("Freelancer, Freelancer HD"), report.Error);
    }

    [Fact]
    public void ADroppedFileCountsAsItsFolder()
    {
        using var game = new FakeGame();

        Assert.Equal(game.Root, Detect(game.PathOf(@"EXE\freelancer.ini")).Root);
    }

    [Fact]
    public void AnEmptyFolderIsNotAGame()
    {
        using var game = new FakeGame();
        var empty = Path.Combine(game.Folder, "empty");
        Directory.CreateDirectory(empty);

        var report = Detect(empty);

        Assert.Equal(GameKind.NotAGame, report.Kind);
        Assert.Null(report.Root);
        Assert.Equal(GameTexts.NotAGameFolder, report.Error);
        Assert.Equal(GameSeverity.Error, report.Severity);
        Assert.False(report.FolderMissing);
    }

    [Fact]
    public void AMissingFolderIsNotFound()
    {
        var report = Detect(Path.Combine(Path.GetTempPath(), "openlancer-tests", "no-such-folder-m11a"));

        Assert.Equal(GameTexts.FolderMissing, report.Error);
        Assert.True(report.FolderMissing);
    }

    [Fact]
    public void LibrelancerDataIsRefused()
    {
        using var game = new FakeGame();
        var folder = Path.Combine(game.Folder, "lr");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "librelancer.ini"), "[Librelancer]\n");

        Assert.Equal(GameTexts.LibrelancerData, Detect(folder).Error);
    }

    [Fact]
    public void TheDiscLayoutGetsTheDiscText()
    {
        using var game = new FakeGame();
        var disc = Path.Combine(game.Folder, "disc");
        var copy = Path.Combine(game.Folder, "copy");
        Directory.CreateDirectory(disc);
        File.WriteAllBytes(Path.Combine(disc, "SETUP.EXE"), []);
        File.WriteAllBytes(Path.Combine(disc, "CAB1.CAB"), []);
        Directory.CreateDirectory(Path.Combine(copy, "GAME", "EXE"));
        File.WriteAllBytes(Path.Combine(copy, "GAME", "EXE", "FREELA_1.EXE"), []);

        Assert.Equal(GameTexts.Disc, Detect(disc).Error);
        Assert.Equal(GameTexts.Disc, Detect(copy).Error);
    }

    [Fact]
    public void TheCaseOfNamesDoesNotMatter()
    {
        using var game = new FakeGame();
        var ini = File.ReadAllText(game.PathOf(@"EXE\freelancer.ini"));
        game.Delete(@"EXE\freelancer.ini");
        Directory.Move(game.PathOf("EXE"), game.PathOf("exe_"));
        Directory.Move(game.PathOf("exe_"), game.PathOf("exe"));
        File.WriteAllText(game.PathOf(@"exe\FREELANCER.INI"), ini);

        var report = Detect(game.Root);

        Assert.Equal(game.Root, report.Root);
        Assert.True(report.IsGame);
    }

    private static GameReport Detect(string folder) =>
        GameDetector.Detect(folder, FakeGame.Builds(), TestContext.Current.CancellationToken);

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}

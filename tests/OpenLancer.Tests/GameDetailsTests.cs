using System.IO;
using System.Linq;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Games;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// What the first-run screen and the Game link window show of a game folder check: each note once, and the rows of the
/// Details table with short hashes. No test uses a game file.
/// </summary>
public sealed class GameDetailsTests
{
    private const string Hash = "5b4ee64cf64bb9c5525cee786f54380170dbde5617d1aaf5ef3655237c9cfaeb";

    private static GameReport Report(params GameFinding[] findings) =>
        new(@"C:\Games\Freelancer", @"C:\Games\Freelancer", GameKind.Vanilla, "1.0", VersionProof.Hash, false, null,
            null, null, "vanilla-1.0", "vanilla-1.1", DataState.NotChanged, "vanilla-1.0", 10, 0, [], [], findings);

    private static GameFinding Info(int i) => new(GameSeverity.Info, $"Note {i}.");

    [Fact]
    public void TheTwoNotesOfAGameDataCopyShowOnceAboveDetails()
    {
        // The case of the user: game data of 1.0 without Freelancer.exe gives two info notes. They showed above
        // Details and again in Details.
        using var game = new FakeGame();
        var report = game.Detect(FakeGame.Builds(("vanilla-1.0", game.Manifest())));

        var (shown, more) = GameDetails.Split(GameDetails.Notes(report));

        Assert.Equal(2, shown.Count);
        Assert.Contains(shown, f => f.Text == GameTexts.GameDataOnly);
        Assert.Contains(shown, f => f.Text == GameTexts.OnePointZeroTarget);
        Assert.Empty(more);
    }

    [Fact]
    public void EachFindingIsOneNote()
    {
        var report = Report(Info(1), Info(2), Info(1));

        Assert.Equal([Info(1), Info(2)], GameDetails.Notes(report));
    }

    [Fact]
    public void TheNotesInDetailsAreTheNotesThatDoNotShowAboveIt()
    {
        var notes = GameDetails.Notes(Report(Enumerable.Range(1, 7).Select(Info).ToArray()));

        var (shown, more) = GameDetails.Split(notes);

        Assert.Equal(GameDetails.MaxNotes, shown.Count);
        Assert.Equal([Info(6), Info(7)], more);
        Assert.Empty(shown.Intersect(more));
        Assert.Equal(notes, shown.Concat(more));
    }

    [Fact]
    public void TheErrorOfAFolderWithoutAGameIsInTheStateLineOnly()
    {
        using var game = new FakeGame();
        var empty = Path.Combine(game.Folder, "empty");
        Directory.CreateDirectory(empty);
        var noGame = GameDetector.Detect(empty, FakeGame.Builds(), TestContext.Current.CancellationToken);
        var missing = GameDetector.Detect(Path.Combine(game.Folder, "missing"), FakeGame.Builds(),
            TestContext.Current.CancellationToken);

        // The state line says "Not valid: <error>", so no note repeats the error.
        Assert.False(noGame.IsGame);
        Assert.Equal($"Not valid: {noGame.Error}", GameLook.State(noGame, false));
        Assert.DoesNotContain(GameDetails.Notes(noGame), f => f.Text == noGame.Error);
        // The state line of a missing folder says "Not found", so its error stays a note.
        Assert.Equal("Not found", GameLook.State(missing, false));
        Assert.Contains(GameDetails.Notes(missing), f => f.Text == missing.Error);
    }

    [Theory]
    [InlineData(@"EXE\Common.dll: file version 1.0.1223.11.", @"EXE\Common.dll", "file version 1.0.1223.11.")]
    [InlineData(@"EXE\dacom.ini [Libraries]: 14 active lines: a.dll, b.dll.", @"EXE\dacom.ini [Libraries]",
        "14 active lines: a.dll, b.dll.")]
    [InlineData(@"INI files: 120 compared with vanilla-1.0: 0 changed, 0 missing, 0 extra.", "INI files",
        "120 compared with vanilla-1.0: 0 changed, 0 missing, 0 extra.")]
    [InlineData("[Resources] names Controls.dll. [Libraries] does not have flplusplus.dll or FLSharp.dll.", "",
        "[Resources] names Controls.dll. [Libraries] does not have flplusplus.dll or FLSharp.dll.")]
    [InlineData("Controls.dll has no text. So Controls.dll is not the marker: see the check.", "",
        "Controls.dll has no text. So Controls.dll is not the marker: see the check.")]
    public void AnEvidenceLineGivesItsCheckAndItsResult(string line, string check, string result)
    {
        Assert.Equal(new DetailRow(check, result, null), GameDetails.EvidenceRow(line));
    }

    [Fact]
    public void AHashShowsItsShortFormAndTheRowKeepsTheFullValue()
    {
        var row = GameDetails.EvidenceRow($@"EXE\Common.dll: 1769984 bytes, SHA-256 {Hash}, the hash of vanilla-1.0.");

        Assert.Equal(@"EXE\Common.dll", row.Check);
        Assert.Equal("1769984 bytes, SHA-256 5b4ee64c...faeb, the hash of vanilla-1.0.", row.Result);
        Assert.Equal(Hash, row.Hash);
    }

    [Theory]
    [InlineData(Hash, "5b4ee64c...faeb")]
    [InlineData("5b4ee64cfaeb", "5b4ee64cfaeb")]
    public void AShortHashKeepsTheFirstEightAndTheLastFourDigits(string hash, string expected)
    {
        Assert.Equal(expected, GameDetails.ShortHash(hash));
    }

    [Theory]
    [InlineData("data/equipment/goods.ini (changed)", "data/equipment/goods.ini", "changed")]
    [InlineData("exe/dacom.ini (missing)", "exe/dacom.ini", "missing")]
    public void AnIniFileRowNamesTheFileAndItsChange(string line, string check, string result)
    {
        Assert.Equal(new DetailRow(check, result, null), GameDetails.FileRow(line));
    }

    [Fact]
    public void EachEvidenceRowOfAGameNamesItsCheckAndEachCodeFileHasItsHash()
    {
        using var game = new FakeGame();
        game.Write(@"DATA\EQUIPMENT\goods.ini", "[Good]\nnickname = g1\nprice = 200\n");
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        game.Write(@"DATA\EQUIPMENT\goods.ini", "[Good]\nnickname = g1\nprice = 300\n");
        var report = game.Detect(builds);

        var rows = GameDetails.Rows(report);

        Assert.Equal(report.Evidence.Count + report.DataFiles.Count, rows.Count);
        Assert.All(rows, r => Assert.NotEqual("", r.Check));
        var hashed = rows.Where(r => r.Hash != null).ToList();
        Assert.Equal(GameDetector.CodeFiles.Length, hashed.Count);
        Assert.All(hashed, r => Assert.Contains(GameDetails.ShortHash(r.Hash!), r.Result));
        Assert.Equal(new DetailRow("data/equipment/goods.ini", "changed", null), rows[^1]);
    }
}

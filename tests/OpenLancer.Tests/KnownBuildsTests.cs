using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The build manifests of the detector (section 6.6 of the M11a design) and the version reader.</summary>
public sealed class KnownBuildsTests
{
    [Fact]
    public void TheEmbeddedRetailManifestHasEachFileOfTheDisc()
    {
        var builds = KnownBuilds.Embedded;

        Assert.Contains("vanilla-1.0", builds.Ids);
        var rows = builds.Rows("vanilla-1.0");
        Assert.Equal(DataDigest.RetailV1FileCount, rows.Count);
        Assert.Equal(1255, rows.Keys.Count(p => p.EndsWith(".ini", StringComparison.Ordinal)));
        Assert.All(GameDetector.CodeFiles, file => Assert.True(rows.ContainsKey(DataDigest.ManifestPath(file))));
    }

    [Fact]
    public void TheFileNameIsTheBuildId()
    {
        var builds = KnownBuilds.FromText(new Dictionary<string, string> { ["vanilla-1.1"] = "", ["hde-0.7.1"] = "" });

        Assert.Equal(["hde-0.7.1", "vanilla-1.1"], builds.Ids);
    }

    [Fact]
    public void ALineWithTheWrongColumnCountIsAnErrorWithItsLineNumber()
    {
        var builds = KnownBuilds.FromText(new Dictionary<string, string>
        {
            ["vanilla-1.1"] = $"exe/common.dll\t10\t{new string('a', 64)}\nexe/server.dll\t20\n",
        });

        var error = Assert.Throws<InvalidDataException>(() => builds.Rows("vanilla-1.1"));

        Assert.StartsWith("vanilla-1.1.tsv line 2: ", error.Message);
    }

    [Theory]
    [InlineData("exe/common.dll\tten\taaaa", "is not a number")]
    [InlineData("exe/common.dll\t10\tnot-a-hash", "is not a SHA-256")]
    public void ABadSizeOrHashIsAnError(string line, string message)
    {
        var builds = KnownBuilds.FromText(new Dictionary<string, string> { ["b"] = line });

        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => builds.Rows("b")).Message);
    }

    [Fact]
    public void ALineOfDataDigestReadsBack()
    {
        var hash = new string('B', 64);
        var line = DataDigest.Line(@"DATA\EQUIPMENT\Goods.ini", 1234, hash);

        var rows = KnownBuilds.Parse("test", line + "\r\n");

        Assert.Equal(new ManifestRow("data/equipment/goods.ini", 1234, hash.ToLowerInvariant()),
            rows["data/equipment/goods.ini"]);
    }

    [Fact]
    public void OnePointOneUsesTheFilesOfOnePointZero()
    {
        var hash = new string('a', 64);
        var other = new string('b', 64);
        var builds = KnownBuilds.FromText(new Dictionary<string, string>
        {
            ["vanilla-1.0"] = $"exe/common.dll\t1\t{hash}\ndata/a.ini\t2\t{hash}\n",
            ["vanilla-1.1"] = $"exe/common.dll\t3\t{other}\n",
        });

        var known = builds.Reference("vanilla-1.1")!.Value;

        Assert.Equal("vanilla-1.1", known.Reference);
        Assert.True(known.FromParent);
        Assert.Equal(3, known.Rows["exe/common.dll"].Size);
        Assert.Equal(2, known.Rows["data/a.ini"].Size);
        Assert.Null(builds.Reference("hde-0.7.1"));
    }

    [Fact]
    public void AVersionBlockGivesItsFileVersion()
    {
        Assert.Equal(new Version(1, 0, 1223, 11), PeVersion.FromVersionBlock(FakeGame.VersionBlock(new Version(1, 0, 1223, 11))));
    }

    [Fact]
    public void AVersionBlockWithoutTheSignatureGivesNull()
    {
        var block = FakeGame.VersionBlock(new Version(1, 0, 1223, 11));
        block[40] = 0;

        Assert.Null(PeVersion.FromVersionBlock(block));
        Assert.Null(PeVersion.FromVersionBlock(null));
    }

    [Fact]
    public void AFakeDllWithTheRetailVersionGivesThatVersion()
    {
        using var game = new FakeGame();
        game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(new Version(1, 0, 1223, 11)));

        Assert.Equal(new Version(1, 0, 1223, 11), PeVersion.Of(game.PathOf(@"EXE\Common.dll")));
    }

    [Fact]
    public void ADllThatIsNotAPeFileGivesNoVersion()
    {
        using var game = new FakeGame();

        Assert.Null(PeVersion.Of(game.PathOf(@"EXE\Server.dll")));
        Assert.Null(PeVersion.Of(game.PathOf(@"EXE\no-such.dll")));
    }

    [Fact]
    public void ADllWithoutAVersionBlockGivesNoVersion()
    {
        using var game = new FakeGame();
        // The writer always writes a version block, so the test removes the signature of its structure.
        var bytes = FakeGame.VersionDll(new Version(1, 0, 1223, 11));
        var signature = new byte[] { 0xBD, 0x04, 0xEF, 0xFE };
        var index = bytes.AsSpan().IndexOf(signature);
        Assert.True(index > 0);
        bytes[index] = 0;
        game.WriteBytes(@"EXE\Common.dll", bytes);

        Assert.Null(PeVersion.Of(game.PathOf(@"EXE\Common.dll")));
    }
}

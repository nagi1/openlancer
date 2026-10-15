using System;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// <see cref="ModOverlay.Replace"/> (section 12.3 of the M4 design): a hot reload gives the file system of the game
/// the files of a new overlay. The base folder provider stays.
/// </summary>
public sealed class ModOverlayReplaceTests
{
    private const string GoodsPath = @"DATA\goods.ini";
    private const string BaseOnlyPath = @"DATA\base_only.ini";
    private const string OnlyAPath = @"DATA\only_a.ini";

    [Fact]
    public void AFileSystemShowsTheFilesOfTheNewOverlay()
    {
        using var ws = Workspace();
        var a = ws.Build(ws.Mod("a", new()
        {
            [@"patches\" + GoodsPath] = "[Good]\nnickname = alpha\nprice = 111\n",
            [@"files\" + OnlyAPath] = "[Only]\nnickname = a\n",
        }));
        var b = ws.Build(ws.Mod("b", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = alpha\nprice = 222\n" }));
        var live = a.CreateFileSystem();
        var baseProvider = live.FileProviders[0];
        Assert.True(live.FileExists(OnlyAPath));

        b.Replace(live);

        Assert.Equal(Bytes(b, GoodsPath), live.ReadAllBytes(GoodsPath));
        Assert.Equal(222, Price(live));
        Assert.Equal(File.ReadAllBytes(Path.Combine(ws.Base, BaseOnlyPath)), live.ReadAllBytes(BaseOnlyPath));
        Assert.False(live.FileExists(OnlyAPath));
        Assert.Throws<FileNotFoundException>(() => live.Open(OnlyAPath));
        Assert.Equal(2, live.FileProviders.Count);
        Assert.Same(baseProvider, live.FileProviders[0]);
    }

    [Fact]
    public void AFileSystemThatIsNotAnOverlayIsRefused()
    {
        using var ws = Workspace();
        var overlay = ws.Build(ws.Mod("a", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = alpha\nprice = 111\n" }));
        var plain = FileSystem.FromPath(ws.Base);
        var before = plain.FileProviders.ToList();

        Assert.Throws<InvalidOperationException>(() => overlay.Replace(plain));
        Assert.Equal(before, plain.FileProviders);
    }

    [Fact]
    public void AnOverlayOfAnotherBaseFolderIsRefused()
    {
        using var ws = Workspace();
        using var other = Workspace();
        var mine = ws.Build(ws.Mod("a", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = alpha\nprice = 111\n" }));
        var theirs = other.Build(other.Mod("b", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = alpha\nprice = 222\n" }));
        var live = theirs.CreateFileSystem();
        var before = live.FileProviders.ToList();

        Assert.Throws<InvalidOperationException>(() => mine.Replace(live));
        Assert.Equal(before, live.FileProviders);
        Assert.Equal(222, Price(live));
    }

    [Fact]
    public void TheSameBaseFolderWithAnEndingSeparatorIsTheSameFolder()
    {
        using var ws = Workspace();
        var a = ws.Build(ws.Mod("a", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = alpha\nprice = 111\n" }));
        var b = ModOverlay.Build(ws.Base + Path.DirectorySeparatorChar,
            [ws.Mod("b", new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = alpha\nprice = 222\n" })],
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);
        var live = a.CreateFileSystem();

        b.Replace(live);

        Assert.Equal(222, Price(live));
    }

    private static TestWorkspace Workspace()
    {
        var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, "[Good]\nnickname = alpha\nprice = 100\n");
        ws.WriteBase(BaseOnlyPath, "[Base]\nnickname = only\n");
        return ws;
    }

    private static byte[] Bytes(ModOverlay overlay, string path) =>
        overlay.Files.Single(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ReadAllBytes();

    /// <summary>The price of the good <c>alpha</c> that the file system gives.</summary>
    private static int Price(FileSystem fs)
    {
        using var stream = fs.Open(GoodsPath);
        return IniFile.ParseFile(GoodsPath, stream).Single()["price"]![0].ToInt32();
    }
}

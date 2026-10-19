using System.IO;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The screenshot rule (decision D586 of the M11c design). The images are synthetic headers.</summary>
public sealed class ScreenshotRulesTests
{
    [Fact]
    public void APngAndAJpegPass()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var png = PublishWorkspace.Screenshot(mod, "a.png");
        PublishWorkspace.WriteBytes(Path.Combine(mod, "publish", "screenshots", "b.jpg"), PublishWorkspace.Jpeg(1920, 1080));

        Assert.Null(ScreenshotRules.Check(mod, png, []));
        Assert.Null(ScreenshotRules.Check(mod, "publish/screenshots/b.jpg", []));
        Assert.Equal((1920, 1080), ScreenshotRules.ImageSize(PublishWorkspace.Jpeg(1920, 1080)));
        Assert.Equal((640, 480), ScreenshotRules.ImageSize(PublishWorkspace.Png(640, 480)));
    }

    [Fact]
    public void AFileThatIsNotAnImageFails()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(Path.Combine(mod, "publish", "screenshots", "a.png"), "not an image");

        Assert.Equal(PublishMessages.ImageRefused("publish/screenshots/a.png"),
            ScreenshotRules.Check(mod, "publish/screenshots/a.png", []));
        Assert.Equal("publish/screenshots/a.png is not a PNG or JPEG image. OpenLancer adds only PNG and JPEG images.",
            PublishMessages.ImageRefused("publish/screenshots/a.png"));
        Assert.Equal(PublishMessages.ImageMissing("publish/screenshots/b.png"),
            ScreenshotRules.Check(mod, "publish/screenshots/b.png", []));
    }

    [Theory]
    [InlineData("publish/screenshots/../../mod.json")]
    [InlineData("publish/screenshots/../x.png")]
    [InlineData("patches/x.png")]
    [InlineData("publish/screenshots/")]
    [InlineData(@"C:\x.png")]
    [InlineData("/publish/screenshots/x.png")]
    public void APathWithDotsFails(string file)
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");

        Assert.Equal(PublishMessages.ImageOutsideFolder(file), ScreenshotRules.Check(mod, file, []));
    }

    [Fact]
    public void AJunctionOutOfTheModFolderFails()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var outside = Path.Combine(workspace.Root, "outside");
        PublishWorkspace.WriteBytes(Path.Combine(outside, "a.png"), PublishWorkspace.Png(10, 10));
        Directory.CreateDirectory(Path.Combine(mod, "publish", "screenshots"));
        PublishWorkspace.Junction(Path.Combine(mod, "publish", "screenshots", "linked"), outside);

        Assert.Equal(PublishMessages.ImageOutsideFolder("publish/screenshots/linked/a.png"),
            ScreenshotRules.Check(mod, "publish/screenshots/linked/a.png", []));
    }

    [Fact]
    public void AFileInAGameFolderFails()
    {
        using var game = new FakeGame();
        var image = game.PathOf(@"EXE\shot.png");
        PublishWorkspace.WriteBytes(image, PublishWorkspace.Png(10, 10));
        var linked = Path.Combine(game.Folder, "linked");
        PublishWorkspace.WriteBytes(Path.Combine(linked, "b.png"), PublishWorkspace.Png(10, 10));

        Assert.Equal(PublishMessages.ImageInGameFolder("shot.png", game.Root), ScreenshotRules.CheckImport(image, []));
        Assert.Equal(PublishMessages.ImageInGameFolder("b.png", linked),
            ScreenshotRules.CheckImport(Path.Combine(linked, "b.png"), [linked]));
    }

    [Fact]
    public void AFileWithTheHashOfAGameFileFails()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var shot = PublishWorkspace.Screenshot(mod, "a.png", mark: 7);
        var bytes = File.ReadAllBytes(Path.Combine(mod, "publish", "screenshots", "a.png"));
        var builds = FakeGame.Builds(("hde-0.7.1",
            DataDigest.Line(@"EXE\textures\dirt.png", bytes.Length, ContentHash.Of(bytes)) + "\n"));

        var reason = ScreenshotRules.Check(mod, shot, [], builds);

        Assert.Equal($"{shot} has the same bytes as exe/textures/dirt.png of {GameDetector.BuildName("hde-0.7.1")}. A mod must " +
                     "not contain game files. Remove the file.", reason);
        Assert.Null(ScreenshotRules.Check(mod, shot, [], FakeGame.Builds()));
    }

    [Fact]
    public void ATooLargeImageFails()
    {
        using var workspace = new PublishWorkspace();
        var source = Path.Combine(workspace.Root, "large.png");
        PublishWorkspace.WriteBytes(source, PublishWorkspace.Png(8193, 100));
        var edge = Path.Combine(workspace.Root, "edge.png");
        PublishWorkspace.WriteBytes(edge, PublishWorkspace.Png(ScreenshotRules.MaxSide, ScreenshotRules.MaxSide));

        Assert.Equal("large.png has 8193 x 100 pixels. Use an image with 8,192 pixels or fewer on each side.",
            ScreenshotRules.CheckImport(source, []));
        Assert.Null(ScreenshotRules.CheckImport(edge, []));
    }
}

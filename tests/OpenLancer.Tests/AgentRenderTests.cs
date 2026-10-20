using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImageLib;
using LibreLancer.Render.Cameras;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Scene;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The parts of the offscreen render service of the agent server that need no GPU (section 12.3 of the MCP1 design): the
/// camera of an image and the pixel places of its items, the PNG data, the part of the back buffer of a panel, the
/// spheres of the zones, the time of a cutscene image and the scripts of its build. The render bench of the editor
/// (section 13.4) renders the images.
/// </summary>
public sealed class AgentRenderTests
{
    private const int Width = 1024;
    private const int Height = 640;

    /// <summary>The default angle is the angle of the Scene camera, so an image looks as the Scene looks.</summary>
    [Fact]
    public void TheDefaultAngleIsTheAngleOfTheSceneCamera()
    {
        Assert.Equal(70, ShotCamera.SceneFov(4f / 3), 3);
        foreach (var aspect in new[] { 4f / 3, 1.6f, 16f / 9 })
        {
            var scene = new LookAtCamera { GameFOV = true, ZRange = new Vector2(1, ShotCamera.Far) };
            scene.Update(aspect * 100, 100, new Vector3(0, 0, 100), Vector3.Zero);
            var shot = new ShotCamera(new Vector3(0, 0, 100), Vector3.Zero, ShotCamera.SceneFov(aspect));

            var projection = shot.Projection(aspect, 1, ShotCamera.Far);
            Assert.Equal(scene.Projection.M11, projection.M11, 4);
            Assert.Equal(scene.Projection.M22, projection.M22, 4);
            Assert.Equal(scene.View, shot.View);
        }
    }

    [Fact]
    public void ThePointThatTheCameraLooksAtIsTheCenterOfTheImage()
    {
        var shot = new ShotCamera(new Vector3(100, 2000, 5000), new Vector3(100, 0, 0), 60);

        var center = shot.Project(new Vector3(100, 0, 0), 10, Width, Height)!.Value;
        var above = shot.Project(new Vector3(100, 300, 0), 10, Width, Height)!.Value;
        var right = shot.Project(new Vector3(400, 0, 0), 10, Width, Height)!.Value;

        Assert.Equal(Width / 2f, center.X, 2);
        Assert.Equal(Height / 2f, center.Y, 2);
        Assert.Equal(shot.Distance, center.Distance, 1);
        Assert.True(above.Y < center.Y, "+Y is toward the top of the image.");
        Assert.True(right.X > center.X, "+X is toward the right of the image, for a camera that looks along -Z.");
        Assert.Null(shot.Project(new Vector3(100, 4000, 10_000), 10, Width, Height));
        Assert.Null(shot.Project(new Vector3(1_000_000, 0, 0), 10, Width, Height));
    }

    /// <summary>The fov is the horizontal angle of the whole image, so a point at half the angle is at the edge.</summary>
    [Fact]
    public void TheAngleIsTheHorizontalAngleOfTheWholeImage()
    {
        var shot = new ShotCamera(Vector3.Zero, new Vector3(0, 0, -1000), 90);

        var edge = shot.Project(new Vector3(1000, 0, -1000), 1, Width, Height)!.Value;

        Assert.Equal(Width, edge.X, 1);
        Assert.Equal(Height / 2f, edge.Y, 1);
    }

    /// <summary>A view straight down has -Z at the top of the image, as the top view of the axis widget of the
    /// Scene.</summary>
    [Fact]
    public void AViewStraightDownHasMinusZAtTheTop()
    {
        var shot = new ShotCamera(new Vector3(0, 10_000, 0), Vector3.Zero, 70);

        var north = shot.Project(new Vector3(0, 0, -1000), 10, Width, Height)!.Value;
        var east = shot.Project(new Vector3(1000, 0, 0), 10, Width, Height)!.Value;

        Assert.Equal(-Vector3.UnitZ, shot.Up);
        Assert.True(north.Y < Height / 2f);
        Assert.Equal(Width / 2f, north.X, 1);
        Assert.True(east.X > Width / 2f);
        Assert.Equal(Vector3.UnitY, new ShotCamera(Vector3.Zero, new Vector3(1, 0, 0), 70).Up);
    }

    [Fact]
    public void TheNearPlaneFollowsTheDistanceAsInTheScene()
    {
        Assert.Equal(1, new ShotCamera(Vector3.Zero, new Vector3(0, 0, 10), 70).Near);
        Assert.Equal(50, new ShotCamera(Vector3.Zero, new Vector3(0, 0, 50_000), 70).Near, 3);
        Assert.Equal(1000, new ShotCamera(Vector3.Zero, new Vector3(0, 0, 9_000_000), 70).Near);
    }

    /// <summary>A frame looks at the spheres from the frame direction of the Scene, and each sphere is in the
    /// image.</summary>
    [Fact]
    public void AFrameShowsEachSphere()
    {
        (Vector3, float)[] spheres = [(new Vector3(-20_000, 0, 0), 500), (new Vector3(20_000, 3000, 8000), 2000)];

        var shot = ShotCamera.Frame(spheres, Width / (float)Height);

        Assert.Equal(ShotCamera.SceneFov(Width / (float)Height), shot.Fov);
        Assert.Equal(ShotCamera.FrameDirection.X, shot.Forward.X, 4);
        Assert.Equal(ShotCamera.FrameDirection.Y, shot.Forward.Y, 4);
        Assert.Equal(ShotCamera.FrameDirection.Z, shot.Forward.Z, 4);
        foreach (var (center, radius) in spheres)
        {
            var point = shot.Project(center, radius, Width, Height)!.Value;
            Assert.InRange(point.X - point.Radius, 0, Width);
            Assert.InRange(point.X + point.Radius, 0, Width);
            Assert.InRange(point.Y - point.Radius, 0, Height);
            Assert.InRange(point.Y + point.Radius, 0, Height);
        }

        var empty = ShotCamera.Frame([], 1);
        Assert.Equal(Vector3.Zero, empty.LookAt);
        Assert.True(empty.Distance > 10_000);
    }

    /// <summary>The list gives the largest items first and keeps the number of all items that the image shows.</summary>
    [Fact]
    public void TheVisibleItemsComeLargestFirstUpToTheLimit()
    {
        var shot = new ShotCamera(new Vector3(0, 0, 10_000), Vector3.Zero, 70);
        (string, Vector3, float)[] items =
        [
            ("small", new Vector3(-500, 0, 0), 10),
            ("large", new Vector3(500, 0, 0), 400),
            ("behind", new Vector3(0, 0, 20_000), 400),
            ("near", new Vector3(0, 0, 5000), 10),
            ("outside", new Vector3(1_000_000, 0, 0), 10),
        ];

        var (all, total) = shot.Visible(items, Width, Height, 10);
        var (top, limited) = shot.Visible(items, Width, Height, 2);

        Assert.Equal(["large", "near", "small"], all.Select(i => i.Item));
        Assert.Equal(3, total);
        Assert.Equal(["large", "near"], top.Select(i => i.Item));
        Assert.Equal(3, limited);
    }

    /// <summary>
    /// The PNG has the top row first, and each pixel is opaque. The GPU gives the bottom row first, and a render writes
    /// the alpha of its blends.
    /// </summary>
    [Fact]
    public void ThePngHasTheTopRowFirstAndOpaquePixels()
    {
        // Two rows of two pixels in BGRA order, the bottom row first: blue and green, then red and white.
        byte[] bottomUp = [255, 0, 0, 10, 0, 255, 0, 20, 0, 0, 255, 30, 255, 255, 255, 0];

        var image = AgentImages.Png(2, 2, bottomUp);
        using var stream = new MemoryStream(image.Png);
        var decoded = Generic.ImageFromStream(stream);
        var pixels = MemoryMarshal.Cast<byte, Bgra8>(decoded.Data.AsSpan()).ToArray();

        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.Equal(ToolImage.MimeType, "image/png");
        Assert.Equal([0x50, 0x4E, 0x47], image.Png[1..4]);
        Assert.All(pixels, p => Assert.Equal(255, p.A));
        Assert.Equal((255, 0, 0), (pixels[0].R, pixels[0].G, pixels[0].B));
        Assert.Equal((255, 255, 255), (pixels[1].R, pixels[1].G, pixels[1].B));
        Assert.Equal((0, 0, 255), (pixels[2].R, pixels[2].G, pixels[2].B));
        Assert.Throws<ArgumentException>(() => AgentImages.Png(2, 2, new byte[12]));
    }

    /// <summary>A panel is a rectangle in the coordinates of the window. The back buffer has the bottom row first, so
    /// the crop counts its rows from the bottom.</summary>
    [Fact]
    public void ACropTakesThePanelFromTheBackBuffer()
    {
        // A window of 4 by 3 pixels. Each pixel holds its window column in B and its window row in G.
        const int w = 4, h = 3;
        var buffer = new Bgra8[w * h];
        for (var row = 0; row < h; row++)
        {
            for (var column = 0; column < w; column++)
                buffer[(h - 1 - row) * w + column] = new Bgra8 { B = (byte)column, G = (byte)row, A = 255 };
        }

        var (bytes, width, height) = AgentImages.Crop(buffer, w, h, new Rectangle(1, 0, 2, 2))!.Value;
        var crop = MemoryMarshal.Cast<byte, Bgra8>(bytes.AsSpan()).ToArray();
        var cut = AgentImages.Crop(buffer, w, h, new Rectangle(3, 2, 5, 5))!.Value;

        Assert.Equal((2, 2), (width, height));
        // The bottom row of the crop is window row 1, then window row 0.
        Assert.Equal([(1, 1), (2, 1), (1, 0), (2, 0)], crop.Select(p => ((int)p.B, (int)p.G)));
        Assert.Equal((1, 1), (cut.Width, cut.Height));
        Assert.Null(AgentImages.Crop(buffer, w, h, new Rectangle(4, 0, 2, 2)));
        Assert.Null(AgentImages.Crop(buffer, w, h, new Rectangle(0, -5, 2, 2)));
    }

    /// <summary>The sphere of a zone holds its volume as the Scene draws it.</summary>
    [Fact]
    public void TheSphereOfAZoneHoldsItsVolume()
    {
        static Zone Zone(ShapeKind shape, Vector3 size) => new() { Shape = shape, Size = size };

        Assert.Equal(500, SceneView.ZoneRadius(Zone(ShapeKind.Sphere, new Vector3(500, 0, 0))));
        Assert.Equal(900, SceneView.ZoneRadius(Zone(ShapeKind.Ellipsoid, new Vector3(100, 900, 300))));
        Assert.Equal(new Vector3(100, 200, 300).Length() / 2, SceneView.ZoneRadius(Zone(ShapeKind.Box, new Vector3(100, 200, 300))), 3);
        Assert.Equal(MathF.Sqrt(750 * 750 + 2000 * 2000),
            SceneView.ZoneRadius(Zone(ShapeKind.Cylinder, new Vector3(750, 4000, 0))), 2);
        Assert.Equal(MathF.Sqrt(5000 * 5000 + 100 * 100),
            SceneView.ZoneRadius(Zone(ShapeKind.Ring, new Vector3(5000, 200, 3000))), 2);
    }

    /// <summary>The render bench turns the frame of the system about the vertical axis, one step for each image, and
    /// gates the median time (section 13.4 of the MCP1 design).</summary>
    [Fact]
    public void TheRenderBenchLooksAtTheSystemFromTenDirections()
    {
        var framed = new ShotCamera(new Vector3(0, 500, 1000), new Vector3(0, 0, 0), 80);

        var half = AgentRenderBench.Camera(framed, AgentRenderBench.Images / 2);
        var full = AgentRenderBench.Camera(framed, AgentRenderBench.Images);

        Assert.Equal(framed, AgentRenderBench.Camera(framed, 0));
        Assert.Equal(framed.LookAt, half.LookAt);
        Assert.Equal(framed.Distance, half.Distance, 2);
        Assert.Equal(-1000, half.Position.Z, 2);
        Assert.Equal(500, half.Position.Y, 2);
        Assert.Equal(framed.Position.Z, full.Position.Z, 2);
        Assert.Equal(2, AgentRenderBench.Median([3, 1, 2]));
        Assert.Equal(2.5, AgentRenderBench.Median([4, 1, 3, 2]));
    }

    /// <summary>The preview clock steps whole steps of 1/60 s, so an image shows the last step at or before the time of
    /// the call.</summary>
    [Fact]
    public void ACutsceneImageShowsTheLastStepAtOrBeforeTheTime()
    {
        Assert.Equal(0, AgentRender.LastStep(0));
        Assert.Equal(2, AgentRender.LastStep(2), 9);
        Assert.Equal(2, AgentRender.LastStep(2.01), 9);
        Assert.Equal(121 * CutsceneClock.StepSeconds, AgentRender.LastStep(121 / 60.0 - 1e-9), 9);
    }

    /// <summary>
    /// The build of a cutscene image reads the scripts as the preview reads them: W for the cutscene of the image and
    /// for each open cutscene of the session, else the bytes of the edit view. The plan keeps these bytes at the call.
    /// </summary>
    [Fact]
    public void TheBuildOfAnImageReadsTheCutsceneFromW()
    {
        const string other = @"DATA\other.thn";
        const string otherScript = "duration = 3\nentities = {}\nevents = {}\n";
        using var ws = ThnSessions.Workspace();
        ws.WriteBase(other, otherScript);
        var session = ws.Session("act");
        session.Execute(ThnEdits.SetDuration(ThnSessions.ScriptPath, 12));
        var files = session.EditView.CreateFileSystem();

        var script = Encoding.ASCII.GetString(CutsceneWorkspace.ScriptBytes(session, files, ThnSessions.ScriptPath, null));
        // The session has not opened the other script yet, so its bytes come from the edit view.
        var file = CutsceneWorkspace.ScriptBytes(session, files, other, null);
        var named = CutsceneWorkspace.ScriptBytes(session, files, other, other);

        Assert.Contains("duration = 12", script);
        Assert.Equal(otherScript, Encoding.ASCII.GetString(file).ReplaceLineEndings("\n"));
        Assert.Contains("duration = 3", Encoding.ASCII.GetString(named));
    }
}

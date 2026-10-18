using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenLancer.Editor.Assets;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The point of a drop in the Scene (decision D275 of the M23 design).</summary>
public sealed class ScenePlacementTests
{
    [Fact]
    public void ARayInFrontOfTheCameraHitsTheGridPlane()
    {
        var eye = new Vector3(0, 1000, 0);
        var direction = Vector3.Normalize(new Vector3(1, -1, 0));

        Assert.Equal(new Vector3(1000, 0, 0), ScenePlacement.Point(eye, direction, new Vector3(0, 0, 0)));
        Assert.True(ScenePlacement.OnPlane(ScenePlacement.Point(eye, direction, Vector3.Zero)));
    }

    /// <summary>A flat ray hits the plane far away, past 4 times the pivot distance, so the point is at the pivot
    /// distance.</summary>
    [Fact]
    public void AFlatRayTakesThePivotDistance()
    {
        var eye = new Vector3(0, 10, 0);
        var direction = Vector3.Normalize(new Vector3(1, -0.001f, 0));
        var pivot = new Vector3(100, 10, 0);

        var point = ScenePlacement.Point(eye, direction, pivot);

        Assert.Equal(100, point.X);
        Assert.Equal(10, point.Y);
        Assert.False(ScenePlacement.OnPlane(point));
    }

    [Fact]
    public void ARayAwayFromThePlaneOrACameraOnThePlaneTakesThePivotDistance()
    {
        var up = ScenePlacement.Point(new Vector3(0, 50, 0), Vector3.UnitY, new Vector3(0, 0, 200));
        Assert.Equal(new Vector3(0, 256, 0), up);

        var below = ScenePlacement.Point(new Vector3(0, -50, 0), -Vector3.UnitY, new Vector3(0, -50, 300));
        Assert.Equal(new Vector3(0, -350, 0), below);

        var onPlane = ScenePlacement.Point(Vector3.Zero, Vector3.Normalize(new Vector3(1, -1, 0)), new Vector3(0, 0, 100));
        Assert.Equal(new Vector3(71, -71, 0), onPlane);

        var flat = ScenePlacement.Point(new Vector3(0, 0, 5), Vector3.UnitX, new Vector3(30, 0, 5));
        Assert.Equal(new Vector3(30, 0, 5), flat);
    }

    [Fact]
    public void EachValueIsAWholeNumber()
    {
        var point = ScenePlacement.Point(new Vector3(0.3f, 100, 0.6f), Vector3.Normalize(new Vector3(0.37f, -1, 0.21f)),
            new Vector3(10, 0, 10));

        Assert.Equal(point, new Vector3(MathF.Round(point.X), MathF.Round(point.Y), MathF.Round(point.Z)));
        Assert.Equal(0, point.Y);
    }
}

/// <summary>The keys, the files and the memory of the thumbnails (sections 7.3 and 7.4 of the M23 design). Each test
/// uses a temporary folder and never touches LocalAppData (review finding R15).</summary>
public sealed class ThumbnailCacheTests
{
    private static readonly DateTime Time = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static ThumbnailPart Part(string model = @"data\ships\a.cmp", string library = @"data\ships\a.mat",
        string? envMap = "envmapbasic", long length = 100, DateTime? time = null, string layer = "base",
        Vector3 position = default) =>
        new([new ThumbnailFile(model, layer, length, time ?? Time), new ThumbnailFile(library, "base", 50, Time)], envMap,
            position);

    [Fact]
    public void TheKeyChangesWithEachInputAndOnlyWithIt()
    {
        var key = ThumbnailKey.Of(128, [Part()]);

        Assert.Equal(key, ThumbnailKey.Of(128, [Part()]));
        Assert.Equal(64, key.Length);
        Assert.All(new[]
        {
            ThumbnailKey.Of(256, [Part()]),
            ThumbnailKey.Of(128, [Part(model: @"data\ships\b.cmp")]),
            ThumbnailKey.Of(128, [Part(library: @"data\ships\b.mat")]),
            ThumbnailKey.Of(128, [Part(envMap: null)]),
            ThumbnailKey.Of(128, [Part(length: 101)]),
            ThumbnailKey.Of(128, [Part(time: Time.AddSeconds(1))]),
            ThumbnailKey.Of(128, [Part(layer: "my_mod")]),
            ThumbnailKey.Of(128, [Part(position: new Vector3(0, 0, 1))]),
            ThumbnailKey.Of(128, [Part(), Part()]),
        }, other => Assert.NotEqual(key, other));
    }

    [Fact]
    public void AFileHasTheHeaderAndReadsBack()
    {
        using var ws = new TestWorkspace();
        var store = new ThumbnailStore(Path.Combine(ws.Root, "thumbnails"));
        byte[] png = [1, 2, 3, 4, 5];

        store.Write("abc", png);

        Assert.Equal("OLTH"u8.ToArray().Concat(new byte[] { 1 }).Concat(png), File.ReadAllBytes(store.PathOf("abc")));
        Assert.Equal(png, store.Read("abc"));
        Assert.EndsWith("abc.olthumb", store.PathOf("abc"));
        Assert.Null(ThumbnailStore.Decode([.. "OLTH"u8, 2, 1]));
    }

    /// <summary>A file without the header is not an image, and the read deletes it.</summary>
    [Fact]
    public void AFileWithoutTheHeaderIsIgnoredAndDeleted()
    {
        using var ws = new TestWorkspace();
        var folder = Path.Combine(ws.Root, "thumbnails");
        Directory.CreateDirectory(folder);
        var store = new ThumbnailStore(folder);
        File.WriteAllBytes(store.PathOf("bad"), [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0]);

        Assert.Null(store.Read("bad"));
        Assert.False(File.Exists(store.PathOf("bad")));
    }

    /// <summary>The prune deletes the least recently used <c>.olthumb</c> files down to the low mark, and never deletes
    /// another file.</summary>
    [Fact]
    public void ThePruneDeletesTheOldestImagesAndNoOtherFile()
    {
        using var ws = new TestWorkspace();
        var folder = Path.Combine(ws.Root, "thumbnails");
        var store = new ThumbnailStore(folder);
        for (var i = 0; i < 10; i++)
        {
            store.Write($"key{i}", new byte[95]);
            File.SetLastWriteTimeUtc(store.PathOf($"key{i}"), Time.AddMinutes(i));
        }
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(folder, "other.png"), new byte[5000]);
        File.WriteAllBytes(store.PathOf("broken"), new byte[3]);

        // Each image file has 100 bytes: 1,000 in all. Above 800, the prune goes down below 500.
        // Under the high mark, only the file without the header goes.
        Assert.Equal(1, store.Prune(above: 2000, below: 500));
        Assert.False(File.Exists(store.PathOf("broken")));
        Assert.Equal(6, store.Prune(above: 800, below: 500));

        Assert.Equal(["key6", "key7", "key8", "key9"],
            Directory.GetFiles(folder, "*.olthumb").Select(Path.GetFileNameWithoutExtension).Order());
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "other.png")));
        Assert.Equal(0, store.Prune(above: 800, below: 500));
    }

    /// <summary>Review finding R12: the budget counts the bytes of each texture, and the least recently drawn texture
    /// goes first.</summary>
    [Fact]
    public void TheTextureBudgetCountsBytesAndEvictsTheLeastRecentlyUsed()
    {
        Assert.Equal(64 * 1024, TextureBudget<string>.BytesOf(128));
        Assert.Equal(256 * 1024, TextureBudget<string>.BytesOf(256));
        Assert.Equal(512, TextureBudget<string>.DefaultLimit / TextureBudget<string>.BytesOf(128));
        var budget = new TextureBudget<string>(4 * TextureBudget<string>.BytesOf(128));

        budget.Use("a", TextureBudget<string>.BytesOf(128));
        budget.Use("b", TextureBudget<string>.BytesOf(128));
        budget.Use("big", TextureBudget<string>.BytesOf(256));
        budget.Use("a", TextureBudget<string>.BytesOf(128));

        Assert.Equal(["b", "big"], budget.Evict());
        Assert.Equal(TextureBudget<string>.BytesOf(128), budget.Bytes);
        Assert.Empty(budget.Evict());
    }

    /// <summary>Review finding R16: a PNG of the cache keeps the row order of the render target. The PNG holds the top
    /// row first, so a viewer shows it the right way up.</summary>
    [Fact]
    public void ThePngKeepsTheRowOrderOfTheRenderTarget()
    {
        const int size = 4;
        var bgra = new byte[size * size * 4];
        for (var row = 0; row < size; row++)
        {
            for (var x = 0; x < size; x++)
            {
                var at = (row * size + x) * 4;
                (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = ((byte)(10 * row), (byte)(20 * x), (byte)(row + x), 255);
            }
        }

        var png = ThumbnailStore.ToPng(size, bgra);
        var image = ThumbnailStore.FromPng(png);

        Assert.Equal((size, size), (image.Width, image.Height));
        Assert.Equal(bgra, image.Data);
        // Without the flip, the first row of the PNG is the last row of the render target: the top of the image.
        using var stream = new MemoryStream(png);
        var upright = LibreLancer.ImageLib.Generic.ImageFromStream(stream, flip: false);
        Assert.Equal(bgra[((size - 1) * size * 4)..(size * size * 4)], upright.Data[..(size * 4)]);
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.IO;
using LibreLancer.Graphics;
using LibreLancer.ImageLib;
using LibreLancer.ImUI;
using LibreLancer.Resources;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Assets;

/// <summary>A file of the edit view that an image shows: its layer, its length and its last write time.</summary>
/// <param name="Path">The install path in lowercase.</param>
/// <param name="Layer">"base", the id of the mod that gives the file, or "missing".</param>
internal sealed record ThumbnailFile(string Path, string Layer, long Length, DateTime Written);

/// <summary>The inputs of the image of one model: its files and its environment map, at a position of a
/// group.</summary>
/// <param name="Files">The model file first, then each library file.</param>
internal sealed record ThumbnailPart(IReadOnlyList<ThumbnailFile> Files, string? EnvMap, Vector3 Position);

/// <summary>
/// The cache key of a thumbnail (decision D274 of the M23 design): a SHA-256 of the renderer version, the image size, and
/// for each model its files with their layers, lengths and times, its environment map and its position. A change of a
/// <c>DA_archetype</c>, a <c>material_library</c> or an <c>envmap_material</c> line of W gives a new key at once.
/// </summary>
internal static class ThumbnailKey
{
    /// <summary>The version of the renderer. A change of the image of the same inputs needs a new version.</summary>
    public const int RendererVersion = 1;

    /// <summary>The key in lowercase hexadecimal: 64 characters.</summary>
    public static string Of(int size, IReadOnlyList<ThumbnailPart> parts)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"OpenLancer thumbnail {RendererVersion}\n{size}\n");
        foreach (var part in parts)
        {
            text.Append(CultureInfo.InvariantCulture, $"model {part.Position.X:R} {part.Position.Y:R} {part.Position.Z:R}\n");
            text.Append("env ").Append(part.EnvMap?.ToLowerInvariant() ?? "").Append('\n');
            foreach (var file in part.Files)
                text.Append(CultureInfo.InvariantCulture, $"file {file.Path}|{file.Layer}|{file.Length}|{file.Written.Ticks}\n");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

/// <summary>
/// The files of the thumbnail cache (decision D273 of the M23 design): one file <c>&lt;key&gt;.olthumb</c> for each
/// image, with the four bytes <c>OLTH</c>, a format byte and the PNG data. The folder is an argument, so a test never
/// touches the folder of the user. Only the editor passes the folder in LocalAppData.
/// </summary>
internal sealed class ThumbnailStore(string folder)
{
    public const string Extension = ".olthumb";
    public const byte Format = 1;

    /// <summary>The cache uses up to this many bytes before a prune.</summary>
    public const long PruneAbove = 200L * 1024 * 1024;

    /// <summary>A prune deletes images until the cache uses less than this many bytes.</summary>
    public const long PruneBelow = 150L * 1024 * 1024;

    private static readonly byte[] Signature = "OLTH"u8.ToArray();

    public string Folder => folder;

    public string PathOf(string key) => Path.Combine(folder, key + Extension);

    /// <summary>The bytes of a cache file: the signature, the format byte, then the PNG data.</summary>
    public static byte[] Encode(byte[] png) => [.. Signature, Format, .. png];

    /// <summary>The PNG data of the bytes of a cache file, or null when the bytes have no valid header.</summary>
    public static byte[]? Decode(byte[] bytes) =>
        bytes.Length > Signature.Length + 1 && bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature) &&
        bytes[Signature.Length] == Format
            ? bytes[(Signature.Length + 1)..]
            : null;

    /// <summary>True when the cache has a file for the key.</summary>
    public bool Has(string key) => File.Exists(PathOf(key));

    /// <summary>
    /// The PNG data of a key, or null when the cache has no valid file for it. A file without the header goes. A read
    /// sets the time of the file, so a prune removes the least recently used images first.
    /// </summary>
    public byte[]? Read(string key)
    {
        var path = PathOf(key);
        try
        {
            var png = Decode(File.ReadAllBytes(path));
            if (png == null)
                File.Delete(path);
            else
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return png;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the PNG data of a key through a temporary file, so a reader never sees a part of it.</summary>
    public void Write(string key, byte[] png)
    {
        Directory.CreateDirectory(folder);
        var path = PathOf(key);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Encode(png));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes the least recently used <c>.olthumb</c> files while the <c>.olthumb</c> files use more than
    /// <paramref name="below"/> bytes, when they use more than <paramref name="above"/> bytes. A file without the header
    /// goes too. The prune never deletes another file.
    /// </summary>
    /// <returns>The number of deleted files.</returns>
    public int Prune(long above = PruneAbove, long below = PruneBelow)
    {
        if (!Directory.Exists(folder))
            return 0;
        var deleted = 0;
        var files = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*" + Extension))
        {
            if (!file.Extension.Equals(Extension, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!HasHeader(file))
            {
                if (TryDelete(file))
                    deleted++;
                continue;
            }
            files.Add(file);
        }
        var total = files.Sum(f => f.Length);
        if (total <= above)
            return deleted;
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total < below)
                break;
            var length = file.Length;
            if (TryDelete(file))
            {
                total -= length;
                deleted++;
            }
        }
        return deleted;
    }

    private static bool HasHeader(FileInfo file)
    {
        try
        {
            using var stream = file.OpenRead();
            var head = new byte[Signature.Length + 1];
            return stream.Read(head) == head.Length && Decode([.. head, 0]) != null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that another process holds stays.
            return true;
        }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The PNG data of an image with the row order of a render target, the bottom row first. The PNG holds the
    /// top row first, so a viewer shows it the right way up.</summary>
    public static byte[] ToPng(int size, byte[] bgra)
    {
        using var stream = new MemoryStream();
        PNG.Save(stream, size, size, MemoryMarshal.Cast<byte, Bgra8>(bgra), true);
        return stream.ToArray();
    }

    /// <summary>The image of PNG data with the row order of a render target, the bottom row first, as
    /// <see cref="ToPng"/> takes it. So a cached image and a new image show the same way up (review finding R16).</summary>
    public static Image FromPng(byte[] png)
    {
        using var stream = new MemoryStream(png, false);
        return Generic.ImageFromStream(stream, flip: true);
    }
}

/// <summary>
/// The thumbnail textures in memory, by bytes (section 7.4 of the M23 design): a 128 px RGBA image counts 64 KiB and a
/// 256 px image 256 KiB. Over the limit, the least recently drawn texture goes first.
/// </summary>
internal sealed class TextureBudget<TKey>(long limit) where TKey : notnull
{
    /// <summary>The default limit: 32 MiB.</summary>
    public const long DefaultLimit = 32L * 1024 * 1024;

    private readonly Dictionary<TKey, (long Bytes, long Used)> entries = [];
    private long clock;

    public long Bytes { get; private set; }

    /// <summary>The bytes of a square RGBA image of a size.</summary>
    public static long BytesOf(int size) => 4L * size * size;

    /// <summary>Notes the use of a texture, or adds it.</summary>
    public void Use(TKey key, long bytes)
    {
        if (entries.TryGetValue(key, out var known))
            Bytes -= known.Bytes;
        entries[key] = (bytes, ++clock);
        Bytes += bytes;
    }

    /// <summary>Removes a texture.</summary>
    public void Remove(TKey key)
    {
        if (entries.Remove(key, out var known))
            Bytes -= known.Bytes;
    }

    /// <summary>The least recently used textures that must go, so the rest is within the limit. It removes them from
    /// the budget.</summary>
    public IReadOnlyList<TKey> Evict()
    {
        var result = new List<TKey>();
        foreach (var (key, _) in entries.OrderBy(e => e.Value.Used).ToList())
        {
            if (Bytes <= limit)
                break;
            Remove(key);
            result.Add(key);
        }
        return result;
    }
}

/// <summary>
/// The thumbnails of the Assets panel (section 7 of the M23 design): rendered images of the visible tiles, cached on disk
/// by a content key, and their textures in memory.
/// </summary>
/// <remarks>
/// <para>The panel asks for the image of each visible tile with <see cref="Get"/>, then calls <see cref="Frame"/> once in
/// its draw. A cached image loads on a worker task and becomes a texture on the UI thread, up to 8 textures in each
/// frame. A missing image renders on the UI thread, at most one in each frame, and only when the caller allows it: the
/// game data is loaded, no reload runs, and no camera move or drag runs (section 7.2). The PNG encode and the file write
/// run on a worker task.</para>
/// <para>The renderer has a resource manager of its own over a file system of the current edit view, with the texture
/// budget of LancerEdit. At the budget, and after a save (a new edit view), the cache makes a new one (review finding
/// R6).</para>
/// </remarks>
internal sealed class ThumbnailCache : IDisposable
{
    public const int SmallSize = 128;
    public const int LargeSize = 256;

    /// <summary>The texture memory of the resource manager of the renderer before the cache makes a new one, as in
    /// LancerEdit.</summary>
    private const long ResourceBudget = 128L * 1024 * 1024;

    private const int UploadsPerFrame = 8;

    private readonly Game game;
    private readonly ThumbnailStore store;
    private readonly TextureBudget<string> budget = new(TextureBudget<string>.DefaultLimit);
    private readonly Dictionary<string, (Texture2D Texture, ImTextureRef Ref, float Radius)> textures = [];
    private readonly HashSet<string> failed = [];
    private readonly HashSet<string> reading = [];

    // The keys without a cache file, so a tile that waits for its render does not ask the disk in each frame.
    private readonly HashSet<string> absent = [];
    private readonly ConcurrentQueue<(string Key, byte[]? Png)> read = new();
    private readonly Dictionary<(AssetRef Asset, int Size), (int Version, string? Key, IReadOnlyList<ThumbnailModel> Models)> keys = [];
    private readonly Dictionary<string, ThumbnailFile> stamps = [];
    private readonly Dictionary<AssetRef, float> radii = [];
    private (string Key, int Size, IReadOnlyList<ThumbnailModel> Models)? wish;
    private ModOverlay? view;
    private Dictionary<string, OverlayFile> overlayFiles = new(StringComparer.OrdinalIgnoreCase);
    private GameResourceManager? resources;
    private ThumbnailRenderer? renderer;
    private bool pruneStarted;

    /// <param name="folder">The folder of the cache files.</param>
    public ThumbnailCache(Game game, string folder)
    {
        this.game = game;
        store = new ThumbnailStore(folder);
    }

    /// <summary>
    /// The texture of the thumbnail of an asset for a size, or null while it is not ready or when the asset has no
    /// image. A missing image becomes the wish of this frame, when no earlier call of the frame made a wish.
    /// </summary>
    /// <param name="version">A number that changes with W, so the models of the asset are read again.</param>
    /// <param name="radius">The radius of the rendered models, or null.</param>
    public ImTextureRef? Get(EditSession session, int version, AssetRef asset, int size, out float? radius)
    {
        radius = null;
        Follow(session.EditView);
        if (!keys.TryGetValue((asset, size), out var known) || known.Version != version)
        {
            var models = ModelsOf(session, asset);
            known = (version, models.Count == 0 ? null : ThumbnailKey.Of(size, models.Select(Part).ToList()), models);
            keys[(asset, size)] = known;
        }
        if (known.Key is not { } key || failed.Contains(key))
            return null;
        if (textures.TryGetValue(key, out var texture))
        {
            budget.Use(key, TextureBudget<string>.BytesOf(size));
            // An image from the cache files does not know its radius.
            radius = texture.Radius > 0 ? texture.Radius : null;
            if (radius is { } knownRadius)
                radii[asset] = knownRadius;
            return texture.Ref;
        }
        if (reading.Contains(key))
            return null;
        if (!absent.Contains(key) && store.Has(key))
        {
            reading.Add(key);
            Task.Run(() => read.Enqueue((key, store.Read(key))));
            return null;
        }
        absent.Add(key);
        wish ??= (key, size, known.Models);
        return null;
    }

    /// <summary>The radius of the models of an asset from its last rendered image, or null.</summary>
    public float? RadiusOf(AssetRef asset) => radii.TryGetValue(asset, out var radius) ? radius : null;

    /// <summary>
    /// The work of a frame: the first prune of the session, up to 8 cached images as textures, and the render of the wish
    /// of the frame when <paramref name="canRender"/> is true. Call it once in each frame of the panel draw.
    /// </summary>
    public void Frame(bool canRender)
    {
        if (!pruneStarted)
        {
            pruneStarted = true;
            Task.Run(() => store.Prune());
        }
        for (var i = 0; i < UploadsPerFrame && read.TryDequeue(out var item); i++)
        {
            reading.Remove(item.Key);
            if (item.Png == null)
            {
                // The file went away or is not valid: render the image again.
                continue;
            }
            try
            {
                var image = ThumbnailStore.FromPng(item.Png);
                var texture = new Texture2D(game.RenderContext, image.Width, image.Height);
                texture.SetData(image.Data);
                Add(item.Key, texture, image.Width, 0);
            }
            catch (Exception)
            {
                // A PNG that does not decode gives the glyph. The next session renders it again.
                failed.Add(item.Key);
            }
        }
        if (canRender && wish is { } next)
            Render(next.Key, next.Size, next.Models);
        wish = null;
        foreach (var key in budget.Evict())
            Remove(key);
    }

    /// <summary>Renders one image, shows it, and writes it into the cache on a worker task.</summary>
    private void Render(string key, int size, IReadOnlyList<ThumbnailModel> models)
    {
        if (view == null)
            return;
        if (resources != null && resources.EstimatedTextureMemory >= ResourceBudget)
            ResetRenderer();
        if (renderer == null)
        {
            var files = view.CreateFileSystem();
            resources = new GameResourceManager(game, files);
            renderer = new ThumbnailRenderer(game, resources, files);
        }
        Texture2D? texture;
        float radius;
        try
        {
            texture = renderer.Render(models, size, out radius);
        }
        catch (Exception e)
        {
            // A model that does not load gives the glyph, and the cache does not try it again in this session.
            FLLog.Warning("Thumbnails", $"The thumbnail did not render: {e.Message}");
            texture = null;
            radius = 0;
        }
        if (texture == null)
        {
            failed.Add(key);
            return;
        }
        Add(key, texture, size, radius);
        texture.GetDataAsync().ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
                return;
            try
            {
                store.Write(key, ThumbnailStore.ToPng(size, t.Result));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The image stays in memory. The next session renders it again.
            }
        }, TaskScheduler.Default);
    }

    private void Add(string key, Texture2D texture, int size, float radius)
    {
        textures[key] = (texture, ImGuiHelper.RegisterTexture(texture), radius);
        budget.Use(key, TextureBudget<string>.BytesOf(size));
    }

    private void Remove(string key)
    {
        budget.Remove(key);
        if (!textures.Remove(key, out var texture))
            return;
        ImGuiHelper.DeregisterTexture(texture.Texture);
        texture.Texture.Dispose();
    }

    /// <summary>The models of an asset: the model of a solar archetype, a ship, an asteroid, equipment or a model file,
    /// or the models of the object members of a prop group at their places. Empty for an asset without a model
    /// (decision D272).</summary>
    private static IReadOnlyList<ThumbnailModel> ModelsOf(EditSession session, AssetRef asset)
    {
        if (asset.Kind != AssetKind.PropGroup)
            return session.Assets.ModelOf(asset) is { } source ? [new ThumbnailModel(source, Vector3.Zero)] : [];
        if (PropGroups.Of(session).FirstOrDefault(g => g.File == asset.File) is not { } group)
            return [];
        var result = new List<ThumbnailModel>();
        foreach (var member in group.Members)
        {
            if (member.Type != "Object" ||
                member.Lines.LastOrDefault(l => l.Name.Equals("archetype", StringComparison.OrdinalIgnoreCase)) is not
                    { Values.Count: > 0 } line ||
                session.Lists.List("solar_archetype")?.Find(line.Values[0].ToString()!)?.Section is not { } section ||
                ModelSource.Of(session, section) is not { } source)
                continue;
            result.Add(new ThumbnailModel(source, member.Position));
        }
        return result;
    }

    /// <summary>The key part of a model: its files with their stamps, its environment map and its position.</summary>
    private ThumbnailPart Part(ThumbnailModel model)
    {
        var files = new[] { model.Source.Model }.Concat(model.Source.Libraries).Select(Stamp).ToList();
        return new ThumbnailPart(files, model.Source.EnvMapMaterial, model.Position);
    }

    /// <summary>The stamp of a file below the data folder in the edit view: the layer that gives it, its length and its
    /// time. The stamps stay for the life of an edit view, which changes only with a save.</summary>
    private ThumbnailFile Stamp(string value)
    {
        var path = (ThnReferences.PathOf(value) ?? value).ToLowerInvariant();
        if (stamps.TryGetValue(path, out var known))
            return known;
        FileInfo? info = null;
        var layer = "base";
        if (overlayFiles.TryGetValue(path, out var overlay))
        {
            layer = overlay.DiskPath == null ? "generated"
                : view!.Mods.FirstOrDefault(m => overlay.DiskPath.StartsWith(m.Folder, StringComparison.OrdinalIgnoreCase))?.Id ??
                  "mod";
            info = overlay.DiskPath == null ? null : new FileInfo(overlay.DiskPath);
        }
        else if (view != null)
            info = new FileInfo(Path.Combine(view.BaseFolder, path));
        known = info is { Exists: true }
            ? new ThumbnailFile(path, layer, info.Length, info.LastWriteTimeUtc)
            : new ThumbnailFile(path, "missing", 0, DateTime.MinValue);
        stamps[path] = known;
        return known;
    }

    /// <summary>Follows the edit view of the session. A new edit view after a save gives new stamps and a new resource
    /// manager of the renderer.</summary>
    private void Follow(ModOverlay editView)
    {
        if (ReferenceEquals(view, editView))
            return;
        view = editView;
        overlayFiles = editView.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        stamps.Clear();
        keys.Clear();
        failed.Clear();
        absent.Clear();
        ResetRenderer();
    }

    private void ResetRenderer()
    {
        renderer?.Dispose();
        resources?.Dispose();
        renderer = null;
        resources = null;
    }

    /// <summary>Frees the textures and the renderer, for example when the profile closes.</summary>
    public void Reset()
    {
        foreach (var key in textures.Keys.ToList())
            Remove(key);
        keys.Clear();
        failed.Clear();
        absent.Clear();
        stamps.Clear();
        wish = null;
        view = null;
        ResetRenderer();
    }

    public void Dispose() => Reset();
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The fingerprints of what the lower layer L reads. Before a save writes a patch, the session compares them again. So
/// a save never writes a patch against a B that a change outside the editor made old (review finding F11).
/// </summary>
/// <remarks>
/// <para>The fingerprints are:</para>
/// <list type="bullet">
/// <item>The layer: the base folder, and the id and the folder of each lower mod and of the active mod, in load
/// order. The profile and the <c>dependsOn</c> and <c>loadAfter</c> lists of the mods decide them.</item>
/// <item>The text: the index of the generated text DLL, and the <c>strings.json</c> of each lower mod. The text ids
/// in B come from them.</item>
/// <item>For each path: the base file, and the whole file and the patch of each lower mod.</item>
/// </list>
/// <para>The merge reads the files of a path that a lower mod changes when the session opens. So the constructor takes
/// their fingerprints. The session reads the base file of any other path when it opens the path, so it calls
/// <see cref="Remember"/> then.</para>
/// </remarks>
internal sealed class LayerInputs
{
    /// <summary>The name of the text inputs in the result of <see cref="Changed"/>: the text ids of the lower mods
    /// changed.</summary>
    public const string TextInputs = "lower text";

    private readonly FileSystem baseFs;
    private readonly Dictionary<string, string> paths = new(VfsPath.Comparer);
    private readonly string layer;
    private readonly string text;

    /// <param name="lower">The lower mods, in load order.</param>
    /// <param name="active">The active mod, or null.</param>
    public LayerInputs(string baseFolder, IReadOnlyList<Mod> lower, Mod? active)
    {
        baseFs = new FileSystem(new SysFolderQuickInit(baseFolder));
        layer = LayerFingerprint(baseFolder, lower, active);
        text = TextFingerprint(baseFolder, lower);
        // The merge reads the INI files and the cutscene scripts that a lower mod changes.
        var changed = lower.SelectMany(m => m.Patches.Keys.Concat(m.Files.Keys)).Distinct(VfsPath.Comparer);
        foreach (var path in changed.Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                                               p.EndsWith(".thn", StringComparison.OrdinalIgnoreCase)))
            paths[path] = Fingerprint(path, lower);
    }

    /// <summary>Takes the fingerprint of a path, when the session has none for it yet.</summary>
    /// <param name="lower">The lower mods, as the session loaded them.</param>
    public void Remember(string path, IReadOnlyList<Mod> lower)
    {
        if (!paths.ContainsKey(path))
            paths[path] = Fingerprint(path, lower);
    }

    /// <summary>
    /// What changed since the open. When the profile now gives another layer, this is only the profile file. Else
    /// it is each path whose files differ from their fingerprint, and <see cref="TextInputs"/> when the text
    /// differs.
    /// </summary>
    /// <param name="profileNow">The profile, loaded again from its file.</param>
    /// <param name="active">The active mod of the session.</param>
    public List<string> Changed(IEnumerable<string> pathsToCheck, ModProfile profileNow, Mod active)
    {
        var index = profileNow.IndexOf(active.Id);
        var lowerNow = profileNow.Mods.Take(Math.Max(index, 0)).ToList();
        if (index < 0 || LayerFingerprint(profileNow.BaseFolder, lowerNow, profileNow.Mods[index]) != layer)
            return [profileNow.FilePath];
        var result = pathsToCheck
            .Where(p => !paths.TryGetValue(p, out var known) || known != Fingerprint(p, lowerNow))
            .ToList();
        if (TextFingerprint(profileNow.BaseFolder, lowerNow) != text)
            result.Add(TextInputs);
        return result;
    }

    private string Fingerprint(string path, IReadOnlyList<Mod> lower)
    {
        var result = new StringBuilder();
        if (baseFs.FileExists(path))
            result.Append("base ").Append(Hash(() => baseFs.ReadAllBytes(path))).Append('\n');
        foreach (var mod in lower)
        {
            if (mod.Files.TryGetValue(path, out var file))
                result.Append(mod.Id).Append(" files ").Append(Hash(() => File.ReadAllBytes(file))).Append('\n');
            if (mod.Patches.TryGetValue(path, out var patch))
                result.Append(mod.Id).Append(" patches ").Append(Hash(() => File.ReadAllBytes(patch))).Append('\n');
        }
        return result.ToString();
    }

    /// <summary>
    /// The hash of the content of a file, or a mark when the file cannot be read. The merge reports such a file as
    /// an error. When the file can be read at the save, the fingerprint differs, so the save stops.
    /// </summary>
    private static string Hash(Func<byte[]> read)
    {
        try
        {
            return ContentHash.Of(read());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static string LayerFingerprint(string baseFolder, IReadOnlyList<Mod> lower, Mod? active)
    {
        var result = new StringBuilder("base ").Append(Path.GetFullPath(baseFolder)).Append('\n');
        foreach (var mod in active == null ? lower : lower.Append(active))
            result.Append(mod.Id).Append(' ').Append(mod.Folder).Append('\n');
        return result.ToString();
    }

    private static string TextFingerprint(string baseFolder, IReadOnlyList<Mod> lower)
    {
        var result = new StringBuilder("dll ").Append(ModOverlay.StringsDllIndex(baseFolder, lower)).Append('\n');
        foreach (var mod in lower)
        {
            var file = Path.Combine(mod.Folder, Mod.StringsFile);
            if (File.Exists(file))
                result.Append(mod.Id).Append(' ').Append(Hash(() => File.ReadAllBytes(file))).Append('\n');
        }
        return result.ToString();
    }
}

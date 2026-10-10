using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Overlay;

/// <summary>
/// The game data that a list of mods gives on top of the base data. The base folder is never written.
/// </summary>
/// <remarks>
/// For each changed path, the mods apply in load order: a mod's whole file replaces the content so far, and
/// a mod's patch changes it. Text from all mods goes into one generated resource DLL, which the overlay adds to
/// the <c>[Resources]</c> section of <c>EXE\freelancer.ini</c>.
/// </remarks>
public sealed class ModOverlay
{
    public const string FreelancerIniPath = @"EXE\freelancer.ini";
    public const string StringsDllName = "openlancer_strings.dll";
    public const string StringsDllPath = @"EXE\" + StringsDllName;

    private readonly OverlayLayer layer;

    /// <summary>The merged document of each path that a mod patches, or null when they are not kept.</summary>
    private readonly IReadOnlyDictionary<string, IniDocument>? documents;

    private ModOverlay(string baseFolder, IReadOnlyList<Mod> mods, OverlayLayer layer,
        IReadOnlyList<Diagnostic> diagnostics, StringIds stringIds, PatchLedger ledger,
        IReadOnlyDictionary<string, IniDocument>? documents)
    {
        BaseFolder = baseFolder;
        Mods = mods;
        this.layer = layer;
        Diagnostics = diagnostics;
        StringIds = stringIds;
        Ledger = ledger;
        this.documents = documents;
    }

    public string BaseFolder { get; }

    /// <summary>The mods in load order.</summary>
    public IReadOnlyList<Mod> Mods { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>The files that the mods add or change. All other files come from the base folder.</summary>
    public IReadOnlyCollection<OverlayFile> Files => layer.Files;

    public StringIds StringIds { get; }

    /// <summary>Which mod changes which entries of each merged file.</summary>
    internal PatchLedger Ledger { get; }

    /// <summary>
    /// Makes a Librelancer file system that shows the merged data. Give it to <c>GameItemDb</c> and the resource managers.
    /// </summary>
    /// <remarks>
    /// Merged files report no backing file. A whole-file override reports its file in the mod, and an unchanged
    /// file reports its file in the base folder, which only the read-only attribute protects.
    /// </remarks>
    public FileSystem CreateFileSystem() => new(new SysFolderQuickInit(BaseFolder), layer);

    public static ModOverlay Build(string baseFolder, IReadOnlyList<Mod> modsInOrder, IStringResourceWriter stringWriter) =>
        Build(baseFolder, modsInOrder, stringWriter, keepDocuments: false);

    /// <param name="keepDocuments">True to keep the merged document of each patched path for
    /// <see cref="Document"/>.</param>
    internal static ModOverlay Build(string baseFolder, IReadOnlyList<Mod> modsInOrder,
        IStringResourceWriter stringWriter, bool keepDocuments)
    {
        baseFolder = Path.GetFullPath(baseFolder);
        var builder = new Builder(baseFolder, modsInOrder, keepDocuments);
        return builder.Run(stringWriter);
    }

    /// <summary>
    /// The index of the generated text DLL in the <c>[Resources]</c> list, as <see cref="Build"/> gives it for these
    /// mods (see <see cref="StringIds.DllIndex"/>). The text ids of the mods depend on it.
    /// </summary>
    internal static int StringsDllIndex(string baseFolder, IReadOnlyList<Mod> modsInOrder)
    {
        var builder = new Builder(Path.GetFullPath(baseFolder), modsInOrder, keepDocuments: false);
        builder.MergeFreelancerIni();
        return builder.DllIndex();
    }

    /// <summary>
    /// The document that a patch of one more mod changes at <paramref name="path"/>, exactly as the merge gives it
    /// to that patch: a deep copy of the merged document, or else the parsed file of a mod or of the base folder.
    /// Returns null when the path has no file.
    /// </summary>
    /// <remarks>
    /// The merge keeps each patched document in memory and writes it once. Its values can differ from the values
    /// that a parse of the written file gives. For example, the text writer writes the float 100 as <c>100</c>, which
    /// the parser reads as an Int32. So a patched path must come from the kept document, never from the file.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The overlay was built without <c>keepDocuments</c>.</exception>
    internal IniDocument? Document(string path)
    {
        if (documents == null)
            throw new InvalidOperationException("The overlay was built without its merged documents.");
        if (documents.TryGetValue(path, out var document))
            return document.Clone();
        var fs = CreateFileSystem();
        return fs.FileExists(path) ? IniDocument.Parse(path, fs.ReadAllBytes(path)) : null;
    }

    private sealed class Builder(string baseFolder, IReadOnlyList<Mod> mods, bool keepDocuments)
    {
        private readonly FileSystem baseFs = FileSystem.FromPath(baseFolder);
        private readonly List<Diagnostic> diagnostics = [];
        private readonly PatchLedger ledger = new();
        private readonly Dictionary<string, OverlayFile> results = new(VfsPath.Comparer);
        private readonly Dictionary<string, IniDocument>? documents = keepDocuments ? new(VfsPath.Comparer) : null;

        public ModOverlay Run(IStringResourceWriter stringWriter)
        {
            var paths = mods.SelectMany(m => m.Files.Keys.Concat(m.Patches.Keys))
                .Distinct(VfsPath.Comparer)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Contains(StringsDllPath, VfsPath.Comparer))
                diagnostics.Add(Diagnostic.Error(StringsDllPath, "This path is reserved for the generated text DLL."));
            foreach (var mod in mods.Where(m => SaveTransaction.HasJournal(m.Folder)))
            {
                diagnostics.Add(Diagnostic.Warning(Path.Combine(mod.Folder, SaveTransaction.FolderName),
                    $"A save of the editor in mod '{mod.Id}' is not complete, so some files of the mod can be from " +
                    "before the save. Open the profile in the editor to complete the save."));
            }

            MergeFreelancerIni();
            var stringIds = AllocateStrings();

            foreach (var path in paths.Where(p => !VfsPath.Comparer.Equals(p, FreelancerIniPath)))
                Merge(path, stringIds);

            if (!stringIds.IsEmpty)
            {
                AddStringsDll();
                results[StringsDllPath] = OverlayFile.Generated(StringsDllPath,
                    stringWriter.Write(stringIds.Strings, stringIds.Infocards));
            }

            return new ModOverlay(baseFolder, mods, new OverlayLayer(results.Values), diagnostics, stringIds, ledger,
                documents);
        }

        /// <summary>
        /// Applies every mod's file and patch for one path, in load order, and stores the result.
        /// </summary>
        private void Merge(string path, StringIds? stringIds)
        {
            OverlayFile? current = null;
            IniDocument? document = null;
            // The mods whose changes are in the content so far.
            var producers = new List<string>();
            foreach (var mod in mods)
            {
                if (mod.Files.TryGetValue(path, out var file))
                {
                    var lost = producers.Where(p => p != mod.Id).ToList();
                    if (lost.Count > 0)
                        diagnostics.Add(Diagnostic.Warning(file,
                            $"Mod '{mod.Id}' replaces {path}. The changes of {string.Join(", ", lost.Select(p => $"mod '{p}'"))} are lost."));
                    ledger.RecordFile(path, mod.Id);
                    current = OverlayFile.FromDisk(path, file);
                    document = ResolveFilePlaceholders(path, file, mod, stringIds);
                    producers = [mod.Id];
                }
                if (!mod.Patches.TryGetValue(path, out var patchFile))
                    continue;
                if (VfsPath.Comparer.Equals(path, FreelancerIniPath))
                {
                    diagnostics.Add(Diagnostic.Error(patchFile,
                        $"{FreelancerIniPath} cannot be patched in format v1. Use files/{FreelancerIniPath.Replace('\\', '/')}."));
                    continue;
                }
                if (!producers.Contains(mod.Id))
                    producers.Add(mod.Id);

                document ??= Load(path, current, patchFile);
                if (document == null)
                    continue;

                IReadOnlyList<Section> patch;
                try
                {
                    patch = IniDocument.ParseFile(patchFile);
                }
                catch (Exception e)
                {
                    diagnostics.Add(Diagnostic.Error(patchFile, e.Message));
                    continue;
                }
                IniPatcher.Apply(document, patch,
                    new PatchContext(mod.Id, patchFile, path, stringIds, ledger, diagnostics));
            }

            if (document != null)
            {
                results[path] = OverlayFile.Generated(path, document.ToBytes());
                if (documents != null)
                    documents[path] = document;
            }
            else if (current != null)
                results[path] = current;
        }

        /// <summary>
        /// Parses a whole INI file of a mod and resolves its text placeholders. Returns null when the file is not
        /// an INI file or has no placeholders, so that it is served unchanged.
        /// </summary>
        private IniDocument? ResolveFilePlaceholders(string path, string file, Mod mod, StringIds? stringIds)
        {
            if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                return null;
            IniDocument document;
            try
            {
                document = IniDocument.Parse(file, File.ReadAllBytes(file));
            }
            catch (Exception e)
            {
                diagnostics.Add(Diagnostic.Warning(file, "The engine cannot parse this file: " + e.Message));
                return null;
            }
            if (!IniPatcher.HasPlaceholders(document))
                return null;
            IniPatcher.ResolvePlaceholders(document, new PatchContext(mod.Id, file, path, stringIds, ledger, diagnostics));
            return document;
        }

        private IniDocument? Load(string path, OverlayFile? current, string patchFile)
        {
            try
            {
                if (current != null)
                    return IniDocument.Parse(current.DiskPath ?? path, current.ReadAllBytes());
                if (baseFs.FileExists(path))
                    return IniDocument.Parse(path, baseFs.ReadAllBytes(path));
            }
            catch (Exception e)
            {
                diagnostics.Add(Diagnostic.Error(path, "Cannot parse the file to patch: " + e.Message));
                return null;
            }
            diagnostics.Add(Diagnostic.Error(patchFile, $"The file to patch, {path}, does not exist."));
            return null;
        }

        /// <summary>
        /// Merges freelancer.ini. It decides the DLL index, so it is merged before the text ids exist. It cannot use
        /// text placeholders.
        /// </summary>
        public void MergeFreelancerIni() => Merge(FreelancerIniPath, null);

        /// <summary>The index of the generated text DLL. Call it after <see cref="MergeFreelancerIni"/>.</summary>
        public int DllIndex()
        {
            var resources = LastResources(LoadFreelancerIni());
            // Librelancer loads EXE\resources.dll first when it exists, then each listed DLL that exists.
            return (FileExists(@"EXE\resources.dll") ? 1 : 0) +
                   (resources?.Count(e => IsDll(e) && e.Count > 0 && FileExists(@"EXE\" + e[0])) ?? 0);
        }

        private StringIds AllocateStrings()
        {
            var index = DllIndex();
            try
            {
                return StringIds.Allocate(mods, index);
            }
            catch (ModLoadException e)
            {
                diagnostics.Add(Diagnostic.Error(e.Path, e.Message));
                return StringIds.Allocate([], index);
            }
        }

        private void AddStringsDll()
        {
            var document = LoadFreelancerIni();
            var resources = LastResources(document);
            if (document == null || resources == null)
            {
                diagnostics.Add(Diagnostic.Error(FreelancerIniPath, "No [Resources] section for the generated text DLL."));
                return;
            }
            var entry = new Entry(resources, "DLL", 1);
            entry.Add(new StringValue(StringsDllName) { Entry = entry });
            resources.Add(entry);
            results[FreelancerIniPath] = OverlayFile.Generated(FreelancerIniPath, document.ToBytes());
        }

        /// <summary>Parses freelancer.ini as the mods leave it, or the base copy when no mod changes it.</summary>
        private IniDocument? LoadFreelancerIni()
        {
            if (results.TryGetValue(FreelancerIniPath, out var merged))
                return IniDocument.Parse(FreelancerIniPath, merged.ReadAllBytes());
            return baseFs.FileExists(FreelancerIniPath)
                ? IniDocument.Parse(FreelancerIniPath, baseFs.ReadAllBytes(FreelancerIniPath))
                : null;
        }

        private bool FileExists(string path)
        {
            var normal = VfsPath.Normalize(path);
            // Mod files are merged after the text ids are allocated, so check the mods' own files too.
            return normal != null && (results.ContainsKey(normal) || baseFs.FileExists(normal) ||
                                      mods.Any(m => m.Files.ContainsKey(normal)));
        }

        private static Section? LastResources(IniDocument? document) =>
            document?.Sections.LastOrDefault(s => s.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase));

        private static bool IsDll(Entry e) => e.Name.Equals("DLL", StringComparison.OrdinalIgnoreCase);
    }
}

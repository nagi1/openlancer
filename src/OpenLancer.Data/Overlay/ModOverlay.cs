using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Overlay;

/// <summary>
/// The game data that a list of mods gives on top of the base data. The base folder is never written.
/// </summary>
/// <remarks>
/// For each changed path, the mods apply in load order: a mod's whole file replaces the content so far, and
/// a mod's patch changes it. An INI patch changes the sections of an INI file, and a THN patch changes the tree of a
/// cutscene script (see <see cref="ThnPatch"/>). Text from all mods goes into one generated resource DLL, which the
/// overlay adds to the <c>[Resources]</c> section of <c>EXE\freelancer.ini</c>. A patch of <c>EXE\freelancer.ini</c>
/// can only add <c>[Data]</c> lines (<c>ol.append</c>, milestone M11).
/// <para>The <see cref="OverlayOptions"/> give the rules of the text DLL. The export and Play in the game use the retail
/// rules.</para>
/// </remarks>
public sealed class ModOverlay
{
    public const string FreelancerIniPath = @"EXE\freelancer.ini";
    public const string StringsDllName = "openlancer_strings.dll";
    public const string StringsDllPath = @"EXE\" + StringsDllName;

    private readonly OverlayLayer layer;

    /// <summary>The merged document of each path that a mod patches, or null when they are not kept.</summary>
    private readonly IReadOnlyDictionary<string, IniDocument>? documents;

    private ModOverlay(GameBase gameBase, OverlayOptions options, IReadOnlyList<Mod> mods, OverlayLayer layer,
        IReadOnlyList<Diagnostic> diagnostics, StringIds stringIds, PatchLedger ledger,
        IReadOnlyDictionary<string, IniDocument>? documents)
    {
        Base = gameBase;
        Options = options;
        Mods = mods;
        this.layer = layer;
        Diagnostics = diagnostics;
        StringIds = stringIds;
        Ledger = ledger;
        this.documents = documents;
    }

    /// <summary>The full path of the base folder.</summary>
    public string BaseFolder => layer.BaseFolder;

    /// <summary>The base files of the merge: the base folder and its layout.</summary>
    public GameBase Base { get; }

    public OverlayOptions Options { get; }

    /// <summary>The mods in load order.</summary>
    public IReadOnlyList<Mod> Mods { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>The files that the mods add or change. All other files come from the base folder.</summary>
    public IReadOnlyCollection<OverlayFile> Files => layer.Files;

    /// <summary>The install paths of the cutscene scripts (<c>.thn</c>) of <see cref="Files"/>: the whole files and the
    /// merged patches of the mods. The validation checks these cutscenes (section 9 of the M8 design).</summary>
    public IReadOnlyList<string> CutscenePaths =>
        [.. layer.Files.Select(f => f.Path).Where(p => p.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))];

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
    public FileSystem CreateFileSystem() => new(Base.OpenProvider(), layer);

    /// <summary>
    /// Makes a file system of <see cref="CreateFileSystem"/> show the files of this overlay. The base folder provider
    /// stays. A running game keeps the file system object of its start, so a hot reload gives it the files of a new
    /// overlay in this way.
    /// </summary>
    /// <remarks>
    /// The replace is one reference write in the list of providers. <see cref="FileSystem.Open"/> reads that list with an
    /// index loop, so a reader on another thread sees the old files or the new files, never a mix of the two lists.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="live"/> is not a file system of
    /// <see cref="CreateFileSystem"/> of an overlay of the same base folder.</exception>
    public void Replace(FileSystem live)
    {
        if (live.FileProviders is not [_, OverlayLayer old] || !SameFolder(old.BaseFolder, BaseFolder))
            throw new InvalidOperationException(
                $"The file system does not show an overlay of the base folder {BaseFolder}, so it cannot take the files " +
                "of this overlay.");
        live.FileProviders[1] = layer;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <param name="stringWriter">Writes the text DLL of the mods.</param>
    /// <param name="thn">Reads the cutscene scripts and their patches, or null when the caller cannot run the engine
    /// VM. Without it, each THN patch is an error.</param>
    public static ModOverlay Build(string baseFolder, IReadOnlyList<Mod> modsInOrder, IStringResourceWriter stringWriter,
        IThnEvaluator? thn) =>
        Build(GameBase.Standard(baseFolder), modsInOrder, stringWriter, thn, OverlayOptions.Default);

    /// <param name="keepDocuments">True to keep the merged document of each patched path for
    /// <see cref="Document"/>.</param>
    internal static ModOverlay Build(string baseFolder, IReadOnlyList<Mod> modsInOrder,
        IStringResourceWriter stringWriter, IThnEvaluator? thn, bool keepDocuments) =>
        Build(GameBase.Standard(baseFolder), modsInOrder, stringWriter, thn,
            OverlayOptions.Default with { KeepDocuments = keepDocuments });

    /// <summary>Merges the mods on the files of a game, with the rules of the options.</summary>
    /// <param name="stringWriter">Writes the text DLL of the mods.</param>
    /// <param name="thn">Reads the cutscene scripts and their patches, or null when the caller cannot run the engine
    /// VM. Without it, each THN patch is an error.</param>
    public static ModOverlay Build(GameBase gameBase, IReadOnlyList<Mod> modsInOrder, IStringResourceWriter stringWriter,
        IThnEvaluator? thn, OverlayOptions options)
    {
        var builder = new Builder(gameBase with { Root = Path.GetFullPath(gameBase.Root) }, modsInOrder, thn, options);
        return builder.Run(stringWriter);
    }

    /// <summary>
    /// The index of the generated text DLL in the <c>[Resources]</c> list, as <see cref="Build"/> gives it for these
    /// mods (see <see cref="StringIds.DllIndex"/>). The text ids of the mods depend on it.
    /// </summary>
    internal static int StringsDllIndex(string baseFolder, IReadOnlyList<Mod> modsInOrder)
    {
        var builder = new Builder(GameBase.Standard(baseFolder), modsInOrder, thn: null, OverlayOptions.Default);
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

    /// <summary>The merged document of a path that the merge kept (see <see cref="OverlayOptions.KeepDocuments"/>), or
    /// null. The caller must not change it.</summary>
    internal IniDocument? MergedDocument(string path) =>
        documents != null && documents.TryGetValue(path, out var document) ? document : null;

    private sealed class Builder(GameBase gameBase, IReadOnlyList<Mod> mods, IThnEvaluator? thn, OverlayOptions options)
    {
        private readonly FileSystem baseFs = gameBase.OpenFileSystem();
        private readonly List<Diagnostic> diagnostics = [];
        private readonly PatchLedger ledger = new();
        private readonly ThnLedger thnLedger = new();
        private readonly Dictionary<string, OverlayFile> results = new(VfsPath.Comparer);
        private readonly Dictionary<string, IniDocument>? documents =
            options.KeepDocuments ? new(VfsPath.Comparer) : null;

        private string StringsDllPath => options.StringsDllPath;

        public ModOverlay Run(IStringResourceWriter stringWriter)
        {
            var paths = mods.SelectMany(m => m.Files.Keys.Concat(m.Patches.Keys))
                .Distinct(VfsPath.Comparer)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Contains(StringsDllPath, VfsPath.Comparer))
                diagnostics.Add(Diagnostic.Error(StringsDllPath, "This path is reserved for the generated text DLL."));
            foreach (var mod in mods.Where(m => m.HasPendingSave))
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

            return new ModOverlay(gameBase, options, mods, new OverlayLayer(gameBase.Root, results.Values), diagnostics,
                stringIds, ledger, documents);
        }

        /// <summary>
        /// Applies every mod's file and patch for one path, in load order, and stores the result.
        /// </summary>
        private void Merge(string path, StringIds? stringIds)
        {
            if (path.EndsWith(".thn", StringComparison.OrdinalIgnoreCase) && mods.Any(m => m.Patches.ContainsKey(path)))
            {
                MergeCutscene(path);
                return;
            }
            OverlayFile? current = null;
            IniDocument? document = null;
            // The mods whose changes are in the content so far.
            var producers = new List<string>();
            foreach (var mod in mods)
            {
                if (mod.Files.TryGetValue(path, out var file))
                {
                    WarnLostChanges(path, file, mod, producers);
                    ledger.RecordFile(path, mod.Id);
                    current = OverlayFile.FromDisk(path, file);
                    document = ResolveFilePlaceholders(path, file, mod, stringIds);
                    producers = [mod.Id];
                }
                if (!mod.Patches.TryGetValue(path, out var patchFile))
                    continue;
                var isFreelancerIni = VfsPath.Comparer.Equals(path, FreelancerIniPath);
                if (isFreelancerIni && mod.Manifest.FormatVersion < 2)
                {
                    diagnostics.Add(Diagnostic.Error(patchFile,
                        $"{FreelancerIniPath} cannot be patched in format v1. Use files/{FreelancerIniPath.Replace('\\', '/')}."));
                    continue;
                }

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
                if (isFreelancerIni && !IniPatcher.IsDataAppend(patch))
                {
                    diagnostics.Add(Diagnostic.Error(patchFile,
                        $"A patch of {FreelancerIniPath} can only add [Data] lines, in [Data] sections with " +
                        $"{IniPatcher.DirectiveAppend} = true. For other changes, use " +
                        $"files/{FreelancerIniPath.Replace('\\', '/')}."));
                    continue;
                }
                if (!producers.Contains(mod.Id))
                    producers.Add(mod.Id);

                document ??= Load(path, current, patchFile);
                if (document == null)
                    continue;

                var context = new PatchContext(mod.Id, patchFile, path, stringIds, ledger, diagnostics,
                    options.KeepPlaceholders);
                if (isFreelancerIni)
                    IniPatcher.ApplyDataAppend(document, patch, context, DataFileExists);
                else
                    IniPatcher.Apply(document, patch, context);
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
        /// Applies every mod's whole file and THN patch of a cutscene script, in load order (see <see cref="ThnPatch"/>).
        /// The evaluator reads the content so far into a tree, each patch changes the tree, and the canonical writer
        /// writes the result as Lua text. The engine reads text as it reads bytecode (ThornRuntime.cs:49-59).
        /// </summary>
        /// <remarks>A patch with an error does not apply, and the tree stays as the mods before it leave it. The native
        /// compiler of the evaluator is not re-entrant, so the paths merge one after another. Each problem has the
        /// cutscene as its place, so that a click in the Console opens it.</remarks>
        private void MergeCutscene(string path)
        {
            var place = new ThnLocation(path);
            void Add(Diagnostic problem) => diagnostics.Add(problem with { Cutscene = place });
            OverlayFile? current = null;
            // The tree of the content so far, after it is read. It is null after a whole file.
            ThnScriptModel? tree = null;
            var patched = false;
            var ids = ThnElementId.Counter();
            var producers = new List<string>();
            foreach (var mod in mods)
            {
                if (mod.Files.TryGetValue(path, out var file))
                {
                    WarnLostChanges(path, file, mod, producers);
                    thnLedger.RecordFile(path);
                    current = OverlayFile.FromDisk(path, file);
                    tree = null;
                    patched = false;
                    producers = [mod.Id];
                }
                if (!mod.Patches.TryGetValue(path, out var patchFile))
                    continue;
                if (!producers.Contains(mod.Id))
                    producers.Add(mod.Id);
                if (thn == null)
                {
                    Add(Diagnostic.Error(patchFile, "This tool cannot merge cutscene patches."));
                    continue;
                }

                tree ??= LoadCutscene(path, current, patchFile, ids, Add);
                if (tree == null)
                    continue;
                ThnPatch patch;
                try
                {
                    patch = ThnPatch.Read(SharedFile.ReadAllBytes(patchFile), patchFile, thn);
                }
                catch (ThnPatchException e)
                {
                    foreach (var problem in e.Problems)
                        Add(Diagnostic.Error(patchFile, problem));
                    continue;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Add(Diagnostic.Error(patchFile, e.Message));
                    continue;
                }
                var result = patch.Apply(tree, thn.Symbols, ids);
                foreach (var error in result.Errors)
                    Add(Diagnostic.Error(patchFile, error));
                foreach (var warning in result.Warnings)
                    Add(Diagnostic.Warning(patchFile, warning));
                if (result.Model == null)
                    continue;
                foreach (var warning in thnLedger.Record(path, mod.Id, result))
                    Add(Diagnostic.Warning(patchFile, warning));
                tree = result.Model;
                patched = true;
            }

            if (patched)
                results[path] = OverlayFile.Generated(path, Encoding.ASCII.GetBytes(ThnWriter.Write(tree!.ToGlobals())));
            else if (current != null)
                results[path] = current;
        }

        /// <summary>Reads the cutscene that a patch changes: the whole file of a mod, or else the base file. Returns null
        /// after an error.</summary>
        /// <param name="add">Adds a problem of the cutscene.</param>
        private ThnScriptModel? LoadCutscene(string path, OverlayFile? current, string patchFile, Func<ThnElementId> ids,
            Action<Diagnostic> add)
        {
            if (current == null && !baseFs.FileExists(path))
            {
                add(Diagnostic.Error(patchFile, $"The file to patch, {path}, does not exist."));
                return null;
            }
            var source = current?.DiskPath ?? path;
            try
            {
                var bytes = current != null ? current.ReadAllBytes() : baseFs.ReadAllBytes(path);
                return ThnScriptModel.From(thn!.Evaluate(bytes, source), ids);
            }
            catch (Exception e) when (e is ThnEvaluationException or FormatException or IOException or UnauthorizedAccessException)
            {
                add(Diagnostic.Error(source, "Cannot read the cutscene to patch: " + e.Message));
                return null;
            }
        }

        /// <summary>Warns that the whole file of a mod discards the changes of the mods before it.</summary>
        /// <param name="producers">The mods whose changes are in the content so far.</param>
        private void WarnLostChanges(string path, string file, Mod mod, List<string> producers)
        {
            var lost = producers.Where(p => p != mod.Id).ToList();
            if (lost.Count > 0)
                diagnostics.Add(Diagnostic.Warning(file,
                    $"Mod '{mod.Id}' replaces {path}. The changes of {string.Join(", ", lost.Select(p => $"mod '{p}'"))} are lost."));
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
                document = IniDocument.Parse(file, SharedFile.ReadAllBytes(file));
            }
            catch (Exception e)
            {
                diagnostics.Add(Diagnostic.Warning(file, "The engine cannot parse this file: " + e.Message));
                return null;
            }
            if (!IniPatcher.HasPlaceholders(document))
                return null;
            IniPatcher.ResolvePlaceholders(document,
                new PatchContext(mod.Id, file, path, stringIds, ledger, diagnostics, options.KeepPlaceholders));
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
            var document = LoadFreelancerIni();
            // The retail game loads resources.dll as index 0. Then each DLL line of each [Resources] section takes the
            // next index, also when its file does not load.
            if (options.DllIndexRule == DllIndexRule.Retail)
                return 1 + (document?.Sections.Where(IsResources).Sum(s => s.Count(IsDll)) ?? 0);
            var resources = LastResources(document);
            // Librelancer loads EXE\resources.dll first when it exists, then each listed DLL that exists.
            return (FileExists(@"EXE\resources.dll") ? 1 : 0) +
                   (resources?.Count(e => IsDll(e) && e.Count > 0 && FileExists(@"EXE\" + e[0])) ?? 0);
        }

        /// <summary>True when the merged view has the file of a <c>[Data]</c> line: a path relative to the data folder of
        /// the merged <c>freelancer.ini</c>.</summary>
        private bool DataFileExists(string relative)
        {
            var dataPath = LoadFreelancerIni()?.Sections
                .Where(s => s.Name.Equals("Freelancer", StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s)
                .FirstOrDefault(e => e.Name.Equals("data path", StringComparison.OrdinalIgnoreCase) && e.Count > 0)?[0]
                .ToString() ?? @"..\DATA";
            return FileExists(@"EXE\" + dataPath + @"\" + relative);
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
            if (options.DllIndexRule == DllIndexRule.Retail)
                WarnMissingDlls(document);
            // Librelancer adds the line at the end of the section. The retail rule puts it after the last DLL line. The
            // merge puts a copy of the section into the list (see IniPatcher).
            var after = options.DllIndexRule == DllIndexRule.Retail ? resources.ToList().FindLastIndex(IsDll) : -1;
            if (after < 0)
                after = resources.Count - 1;
            var copy = new Section(resources.Name, resources.Count + 1) { File = resources.File, Line = resources.Line };
            var entry = new Entry(copy, "DLL", 1);
            entry.Add(new StringValue(options.StringsDllName) { Entry = entry });
            if (after < 0)
                copy.Add(entry);
            for (var i = 0; i < resources.Count; i++)
            {
                copy.Add(IniPatcher.Copy(copy, resources[i], null, false, null));
                if (i == after)
                    copy.Add(entry);
            }
            document.Sections[document.Sections.LastIndexOf(resources)] = copy;
            results[FreelancerIniPath] = OverlayFile.Generated(FreelancerIniPath, document.ToBytes());
            if (documents != null)
                documents[FreelancerIniPath] = document;
        }

        /// <summary>Warns about each <c>DLL</c> line of <c>[Resources]</c> whose file the game does not have. The retail
        /// game keeps the index of such a line.</summary>
        private void WarnMissingDlls(IniDocument document)
        {
            foreach (var line in document.Sections.Where(IsResources).SelectMany(s => s).Where(IsDll))
            {
                var name = line.Count > 0 ? line[0].ToString().Trim() : "";
                if (name.Length > 0 && !FileExists(@"EXE\" + name))
                    diagnostics.Add(Diagnostic.Warning(FreelancerIniPath,
                        $"freelancer.ini names {name}, and the EXE folder does not contain it. The game keeps its index, " +
                        "so the text ids of the mod stay correct."));
            }
        }

        /// <summary>The merged freelancer.ini: a copy of the kept document, else a parse of the merged file or of the base
        /// file. Null when the game has no such file.</summary>
        private IniDocument? LoadFreelancerIni()
        {
            if (documents != null && documents.TryGetValue(FreelancerIniPath, out var kept))
                return kept.Clone();
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

        private static Section? LastResources(IniDocument? document) => document?.Sections.LastOrDefault(IsResources);

        private static bool IsResources(Section s) => s.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase);

        private static bool IsDll(Entry e) => e.Name.Equals("DLL", StringComparison.OrdinalIgnoreCase);
    }
}

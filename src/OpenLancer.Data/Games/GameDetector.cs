using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using LibreLancer.Data.Dll;
using LibreLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Games;

/// <summary>
/// Tells which game a folder holds (milestone M11a): Freelancer 1.0 or 1.1, with or without JFLP, Freelancer: HD
/// Edition and its version, a game that OpenLancer does not support, an unknown game, or no game.
/// </summary>
/// <remarks>
/// The check reads files only. It opens each file for read, and lets other programs keep it open. It never runs a
/// program of the game, never loads a game DLL as code, never writes, renames or deletes a file, never reads the
/// registry, never searches the disk and never uses the network. The rules and their evidence are in sections 4.4 and 6
/// of <c>research\m11a-game-link-final.md</c>.
/// </remarks>
public static partial class GameDetector
{
    internal const string DiscoveryBuild = "discovery";
    internal const string UnknownBuild = "unknown";

    /// <summary>The build id of a retail data copy without code files: 1.0 or 1.1, not known which.</summary>
    internal const string VanillaBuild = "vanilla";

    internal const string HdeBuild = "hde";
    private const string VanillaPrefix = "vanilla-";
    private const string HdePrefix = "hde-";

    /// <summary>The five code files that patch 1.1 replaces. Their hashes and versions tell the build.</summary>
    internal static readonly string[] CodeFiles =
        [@"EXE\Common.dll", @"EXE\Server.dll", @"EXE\DALib.dll", @"EXE\FLServer.exe", @"DLLS\BIN\Content.dll"];

    /// <summary>The binary file version of the code files of retail 1.0.</summary>
    internal static readonly Version RetailCodeVersion = new(1, 0, 1223, 11);

    /// <summary>The HD Edition marker DLL after 0.7.1.</summary>
    internal const string HdeResourcesDll = "HDEResources.dll";

    /// <summary>The HD Edition marker DLL of 0.7.1. Other mods can use the name too, so it needs a second fact.</summary>
    internal const string ControlsDll = "Controls.dll";

    /// <summary>The <c>[Libraries]</c> plugins that only HD Edition ships, without ".dll".</summary>
    private static readonly string[] HdePlugins = ["flplusplus", "flsharp"];

    private const int MaxDataFiles = 50;

    /// <summary>The version in the version text of HD Edition, for example "HDE v0.7.1". The text must name HD Edition,
    /// so a plain "v1.1" of the game does not match (decision D215).</summary>
    [GeneratedRegex(@"\b(?:hd ?edition|hde)\b\D{0,20}?v?(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex HdeVersionPattern();

    /// <summary>Finds the game root from the folder (a selected folder, a folder inside the game, or a file) and checks
    /// it.</summary>
    /// <param name="builds">The known builds, or null for the embedded manifests.</param>
    /// <exception cref="OperationCanceledException">The token stopped the check.</exception>
    public static GameReport Detect(string folder, KnownBuilds? builds = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var found = GameRoot.Find(folder);
        return found.Root == null
            ? NotAGame(folder, found.Error!, found.Missing)
            : new Run(folder, found.Root, builds ?? KnownBuilds.Embedded, cancel).Check();
    }

    /// <summary>Checks a known game root, for example a linked game, without a search for the root.</summary>
    /// <exception cref="OperationCanceledException">The token stopped the check.</exception>
    public static GameReport CheckRoot(string root, KnownBuilds? builds = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        try
        {
            if (!Directory.Exists(root))
                return NotAGame(root, GameTexts.FolderMissing, missing: true);
            if (!GameRoot.IsRoot(root))
                return NotAGame(root, GameTexts.NotAGameFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return NotAGame(root, GameTexts.FolderNotReadable(GameRoot.Sentence(e.Message)));
        }

        return new Run(root, GameFolders.Normal(root), builds ?? KnownBuilds.Embedded, cancel).Check();
    }

    /// <summary>The fast check before a profile opens: the root and <c>EXE\freelancer.ini</c> exist.</summary>
    public static bool LooksLikeGame(string root)
    {
        try
        {
            return GameRoot.IsRoot(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The manifest lines (see <see cref="DataDigest"/>) of the five code files and of the marker DLLs of a game root that
    /// exist: <c>Controls.dll</c>, <c>HDEResources.dll</c> and <c>jflp.dll</c>. A tester with a build that OpenLancer
    /// does not know sends them. They hold paths, sizes and hashes only.
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A file cannot be read.</exception>
    public static IReadOnlyList<string> HashLines(string root) =>
        CodeFiles.Concat([@"EXE\" + ControlsDll, @"EXE\" + HdeResourcesDll, @"EXE\jflp.dll"])
            .Select(file => (File: file, Path: GameRoot.Resolve(root, file)))
            .Where(f => f.Path != null && File.Exists(f.Path))
            .Select(f => DataDigest.Line(f.File, new FileInfo(f.Path!).Length, DataDigest.HashFile(f.Path!)))
            .ToList();

    /// <summary>The name of a build for the user, for example "Freelancer 1.0".</summary>
    public static string BuildName(string build) =>
        build.StartsWith(VanillaPrefix, StringComparison.Ordinal) ? $"Freelancer {build[VanillaPrefix.Length..]}"
        : build.StartsWith(HdePrefix, StringComparison.Ordinal) ? $"Freelancer: HD Edition {build[HdePrefix.Length..]}"
        : build;

    private static GameReport NotAGame(string folder, string error, bool missing = false) =>
        new(folder, null, GameKind.NotAGame, null, VersionProof.None, false, null, null, null, null, null,
            DataState.NotCompared, null, 0, 0, [], [], [new GameFinding(GameSeverity.Error, error)])
        {
            FolderMissing = missing,
        };

    /// <summary>Opens a file for read. Other programs can keep the file open, also for write.</summary>
    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>One check of a game root.</summary>
    private sealed class Run(string folder, string root, KnownBuilds builds, CancellationToken cancel)
    {
        private readonly List<string> evidence = [];
        private readonly List<GameFinding> findings = [];
        private readonly List<string> dataFiles = [];
        private GameKind kind = GameKind.Unknown;
        private bool kindKnown;
        private string? version;
        private VersionProof proof;
        private bool jflp;
        private string? jflpVersion;
        private string? product;
        private string? exePath;
        private string? build;
        private string? target;
        private DataState data = DataState.NotCompared;
        private string? reference;
        private int comparedFiles;
        private int changedFiles;
        private IReadOnlyList<string> libraries = [];
        private List<string> resourceDlls = [];

        public GameReport Check()
        {
            if (CheckRoot() is { } error)
                return NotAGame(folder, error);
            libraries = ReadLibraries();
            TotalConversions();
            if (!kindKnown)
                HdEdition();
            if (!kindKnown)
                Vanilla();
            Jflp();
            GameProgram();
            DataFiles();
            Plugins();
            VirtualStore();
            Target();
            return new GameReport(folder, root, kind, version, proof, jflp, jflpVersion, product, exePath, build, target,
                data, reference, comparedFiles, changedFiles, dataFiles, evidence, findings);
        }

        private void Info(string text) => findings.Add(new GameFinding(GameSeverity.Info, text));

        private void Warning(string text) => findings.Add(new GameFinding(GameSeverity.Warning, text));

        private void SetKind(GameKind value, string? id, string? versionText, VersionProof how)
        {
            (kind, build, version, proof, kindKnown) = (value, id, versionText, how, true);
        }

        private string? Find(string relative) =>
            GameRoot.Resolve(root, relative) is { } path && File.Exists(path) ? path : null;

        /// <summary>The checks of section 6.2. Returns the error of a folder that holds no complete game, or
        /// null.</summary>
        private string? CheckRoot()
        {
            var iniPath = Find(GameRoot.MarkerFile)!;
            List<Section> sections;
            try
            {
                using var stream = OpenRead(iniPath);
                sections = IniFile.ParseFile(iniPath, stream).ToList();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return GameTexts.IniNotReadable(GameRoot.Sentence(e.Message));
            }

            var dataPath = Value(sections, "Freelancer", "data path");
            if (string.IsNullOrWhiteSpace(dataPath))
                return GameTexts.IniNotReadable(GameTexts.NoDataPath);
            if (DataFolder(dataPath) is not { } dataFolder)
                return GameTexts.DataPathMissing(dataPath);

            var universe = Value(sections, "Data", "universe") is { Length: > 0 } named ? named : @"universe\universe.ini";
            var universePath = GameRoot.Resolve(dataFolder, universe);
            if (universePath == null || !File.Exists(universePath))
                return GameTexts.NotComplete(Path.GetRelativePath(root,
                    Path.Combine(dataFolder, universe.Replace('\\', Path.DirectorySeparatorChar))));
            if (Find(@"EXE\resources.dll") == null)
                return GameTexts.NotComplete(@"EXE\resources.dll");
            evidence.Add($"EXE\\freelancer.ini: data path {dataPath}, universe {universe}. EXE\\resources.dll exists.");
            resourceDlls = sections
                .Where(s => s.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s)
                .Where(e => e.Name.Equals("DLL", StringComparison.OrdinalIgnoreCase) && e.Count > 0)
                .Select(e => e[0].ToString()?.Trim() ?? "")
                .Where(v => v.Length > 0)
                .ToList();
            return null;
        }

        /// <summary>The data folder that the data path names, relative to the EXE folder, in any letter case. Null when
        /// it does not exist.</summary>
        private string? DataFolder(string dataPath)
        {
            var full = Path.GetFullPath(Path.Combine(root, "EXE", dataPath.Replace('\\', Path.DirectorySeparatorChar)));
            if (Directory.Exists(full))
                return full;
            var relative = Path.GetRelativePath(root, full);
            if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                return null;
            return GameRoot.Resolve(root, relative) is { } resolved && Directory.Exists(resolved) ? resolved : null;
        }

        private static string? Value(List<Section> sections, string section, string name) =>
            sections.Where(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s)
                .FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && e.Count > 0)?[0]
                .ToString()?.Trim();

        private IReadOnlyList<string> ReadLibraries()
        {
            if (Find(@"EXE\dacom.ini") is not { } path)
            {
                evidence.Add(@"EXE\dacom.ini: not found.");
                return [];
            }

            try
            {
                using var reader = new StreamReader(OpenRead(path));
                var names = DacomLibraries.Read(reader);
                evidence.Add($"EXE\\dacom.ini [Libraries]: {names.Count} active lines: {string.Join(", ", names)}.");
                return names;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Warning(GameTexts.FileNotReadable(@"EXE\dacom.ini", GameRoot.Sentence(e.Message)));
                return [];
            }
        }

        private bool HasLibrary(string key) => libraries.Any(l => DacomLibraries.Key(l) == key);

        /// <summary>Rule G4: Discovery has its own launcher.</summary>
        private void TotalConversions()
        {
            var launcher = Find("DSLauncher.exe") ?? Find(@"EXE\DSLauncher.exe");
            if (launcher == null)
            {
                evidence.Add("DSLauncher.exe: not found, so the folder does not contain Discovery.");
                return;
            }

            evidence.Add($"{Path.GetRelativePath(root, launcher)}: found. This is the launcher of Discovery.");
            SetKind(GameKind.NotSupported, DiscoveryBuild, null, VersionProof.None);
            product = "Discovery";
            Warning(GameTexts.NotSupported(product));
        }

        /// <summary>Rule G5: the marker DLL of HD Edition in <c>[Resources]</c>, and its version text (decisions D210 and
        /// D215).</summary>
        private void HdEdition()
        {
            var marker = resourceDlls.FirstOrDefault(v => IsName(v, HdeResourcesDll) || IsName(v, ControlsDll));
            if (marker == null)
            {
                evidence.Add($"[Resources] of EXE\\freelancer.ini: {resourceDlls.Count} DLLs, no {HdeResourcesDll} or " +
                             $"{ControlsDll}. So the folder does not contain HD Edition.");
                return;
            }

            var name = FileName(marker);
            var plugins = HdePlugins.Any(HasLibrary);
            var pluginText = plugins ? "has flplusplus.dll or FLSharp.dll" : "does not have flplusplus.dll or FLSharp.dll";
            string? found = null;
            var dllPath = Find(@"EXE\" + name);
            if (dllPath == null)
            {
                Warning(GameTexts.HdeDllMissing(name));
                evidence.Add($"[Resources] names {name}. The EXE folder does not contain it. [Libraries] {pluginText}.");
            }
            else
            {
                found = VersionText(dllPath, name);
                evidence.Add($"[Resources] names {name}. [Libraries] {pluginText}.");
            }

            var isHde = IsName(marker, HdeResourcesDll) || found != null || plugins;
            if (!isHde)
            {
                evidence.Add($"{name} has no HD Edition version text, and [Libraries] has no HD Edition plugin. So " +
                             $"{name} is not the HD Edition marker.");
                return;
            }

            SetKind(GameKind.HdEdition, found == null ? HdeBuild : HdePrefix + found, found,
                found == null ? VersionProof.None : VersionProof.VersionText);
            if (found == null && dllPath != null)
                Warning(GameTexts.HdeVersionUnknown(name));
            else if (found != null && HdePrefix + found != ModTargets.HdEdition)
                Warning(GameTexts.OtherHdeVersion(found));
        }

        /// <summary>The HD Edition version in the strings, then the infocards, of the marker DLL, by ascending id. The
        /// evidence names the text that matched, or the texts that the check read.</summary>
        private string? VersionText(string path, string name)
        {
            ResourceDll dll;
            try
            {
                dll = ResourceDll.FromStream(OpenRead(path), path);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Warning(GameTexts.FileNotReadable(@"EXE\" + name, GameRoot.Sentence(e.Message)));
                return null;
            }

            var texts = dll.Strings.OrderBy(s => s.Key).Select(s => ($"string {s.Key}", s.Value))
                .Concat(dll.Infocards.OrderBy(s => s.Key).Select(s => ($"infocard {s.Key}", s.Value)))
                .ToList();
            foreach (var (id, text) in texts)
            {
                var match = HdeVersionPattern().Match(text);
                if (match.Success)
                {
                    evidence.Add($"{name}: {id} matches the HD Edition version: \"{Short(match.Value)}\".");
                    return match.Groups[1].Value;
                }
            }

            evidence.Add($"{name}: no text names the HD Edition version. The check read {texts.Count} texts" +
                         (texts.Count == 0 ? "." : ": " + string.Join(" | ", texts.Take(5).Select(t => Short(t.Item2)))));
            return null;
        }

        /// <summary>Rule G6: the code files, by hash, by file version, or missing.</summary>
        private void Vanilla()
        {
            var present = CodeFiles.Select(f => (File: f, Path: Find(f))).ToList();
            var commonVersion = present[0].Path is { } common ? PeVersion.Of(common) : null;
            if (present[0].Path != null)
                evidence.Add(commonVersion == null
                    ? @"EXE\Common.dll: no file version."
                    : $"EXE\\Common.dll: file version {commonVersion}.");
            var missing = present.Where(p => p.Path == null).Select(p => p.File).ToList();
            if (missing.Count == CodeFiles.Length)
            {
                evidence.Add("The five code files are missing: " + string.Join(", ", CodeFiles) + ".");
                SetKind(GameKind.Vanilla, VanillaBuild, null, VersionProof.None);
                Info(GameTexts.NoCodeFiles);
                return;
            }

            if (missing.Count > 0)
            {
                evidence.Add("Missing code files: " + string.Join(", ", missing) + ".");
                SetKind(GameKind.Unknown, UnknownBuild, null, VersionProof.None);
                Warning(GameTexts.CodeFilesMissing(string.Join(", ", missing.Select(Path.GetFileName))));
                return;
            }

            if (MatchingBuild(present.Select(p => (p.File, p.Path!)).ToList()) is { } match)
            {
                var hde = match.StartsWith(HdePrefix, StringComparison.Ordinal);
                SetKind(hde ? GameKind.HdEdition : GameKind.Vanilla, match,
                    match[(hde ? HdePrefix : VanillaPrefix).Length..], VersionProof.Hash);
                return;
            }

            if (commonVersion == RetailCodeVersion)
            {
                SetKind(GameKind.Vanilla, KnownBuilds.VanillaOnePointZero, "1.0", VersionProof.FileVersion);
                Warning(GameTexts.OnePointZeroCodeChanged);
            }
            else if (commonVersion != null && commonVersion.Major == 1 && commonVersion > RetailCodeVersion)
            {
                SetKind(GameKind.Vanilla, KnownBuilds.VanillaOnePointOne, "1.1", VersionProof.FileVersion);
                Info(GameTexts.OnePointOneByFileVersion(commonVersion.ToString()));
            }
            else
            {
                SetKind(GameKind.Unknown, UnknownBuild, null, VersionProof.None);
                Warning(GameTexts.UnknownVersion);
            }
        }

        /// <summary>The build whose manifest has the size and the hash of each of the five code files, or null.</summary>
        private string? MatchingBuild(List<(string File, string Path)> files)
        {
            var rows = new List<ManifestRow>();
            foreach (var (file, path) in files)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    rows.Add(new ManifestRow(DataDigest.ManifestPath(file), new FileInfo(path).Length,
                        DataDigest.HashFile(path)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Warning(GameTexts.FileNotReadable(file, GameRoot.Sentence(e.Message)));
                    return null;
                }
            }

            string? match = null;
            foreach (var id in builds.Ids)
            {
                var manifest = builds.Rows(id);
                if (rows.All(r => manifest.TryGetValue(r.Path, out var known) && known == r))
                {
                    match = id;
                    break;
                }
            }

            foreach (var (row, file) in rows.Zip(files.Select(f => f.File)))
                evidence.Add($"{file}: {row.Size} bytes, SHA-256 {row.Sha256}" +
                             (match == null ? ", not a hash of a known build." : $", the hash of {match}."));
            return match;
        }

        /// <summary>Rule G7: JFLP is a line of <c>[Libraries]</c> and a DLL.</summary>
        private void Jflp()
        {
            var line = HasLibrary(DacomLibraries.Jflp);
            var path = Find(@"EXE\jflp.dll");
            if (kind == GameKind.HdEdition)
            {
                evidence.Add($"JFLP: {(line ? "[Libraries] names jflp.dll" : "[Libraries] does not name jflp.dll")}" +
                             $"{(path != null ? ", and the EXE folder contains it" : "")}. HD Edition includes it.");
                return;
            }

            if (line && path != null)
            {
                jflp = true;
                jflpVersion = PeVersion.Of(path)?.ToString();
                evidence.Add($"JFLP: [Libraries] names jflp.dll, and the EXE folder contains it. File version: " +
                             $"{jflpVersion ?? "unknown"}.");
            }
            else if (line)
            {
                Warning(GameTexts.JflpDllMissing);
                evidence.Add("JFLP: [Libraries] names jflp.dll, but the EXE folder does not contain it.");
            }
            else if (path != null)
            {
                evidence.Add("JFLP: the EXE folder contains jflp.dll, but [Libraries] does not name it. So the game " +
                             "does not load it.");
            }
        }

        /// <summary>Rule G8: the game program.</summary>
        private void GameProgram()
        {
            exePath = Find(@"EXE\Freelancer.exe");
            evidence.Add(exePath == null ? @"EXE\Freelancer.exe: not found." : @"EXE\Freelancer.exe: found.");
            if (exePath == null)
                Info(GameTexts.GameDataOnly);
        }

        /// <summary>Rule G9: the INI files under DATA and EXE, compared with the manifest of the build (section
        /// 6.4).</summary>
        private void DataFiles()
        {
            if (kind == GameKind.HdEdition && (build == null || !builds.Has(build)))
            {
                evidence.Add("INI files: not compared. OpenLancer has no file list of this HD Edition build.");
                Info(GameTexts.HdeNotCompared);
                return;
            }

            var id = build == VanillaBuild ? KnownBuilds.VanillaOnePointZero : build;
            if (kind is not (GameKind.Vanilla or GameKind.HdEdition) || id == null || builds.Reference(id) is not { } known)
            {
                evidence.Add("INI files: not compared. OpenLancer has no file list of this build.");
                return;
            }

            reference = known.Reference;
            var expected = known.Rows.Values.Where(r => IsDataIni(r.Path)).ToList();
            comparedFiles = expected.Count;
            var actual = RootIniFiles();
            var changes = new List<string>();
            foreach (var row in expected)
            {
                cancel.ThrowIfCancellationRequested();
                if (!actual.Remove(row.Path, out var path))
                {
                    changes.Add($"{row.Path} (missing)");
                    continue;
                }

                try
                {
                    if (new FileInfo(path).Length != row.Size || DataDigest.HashFile(path) != row.Sha256)
                        changes.Add($"{row.Path} (changed)");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Warning(GameTexts.FileNotReadable(Path.GetRelativePath(root, path), GameRoot.Sentence(e.Message)));
                }
            }

            changes.AddRange(actual.Keys.Select(p => $"{p} (extra)"));
            changes.Sort(StringComparer.Ordinal);
            changedFiles = changes.Count;
            dataFiles.AddRange(changes.Take(MaxDataFiles));
            data = changes.Count == 0 ? DataState.NotChanged : DataState.Changed;
            int Count(string change) => changes.Count(c => c.EndsWith(change, StringComparison.Ordinal));
            evidence.Add($"INI files: {comparedFiles} compared with {reference}: {Count("(changed)")} changed, " +
                         $"{Count("(missing)")} missing, {Count("(extra)")} extra.");
            if (known.FromParent && build == KnownBuilds.VanillaOnePointOne)
                Info(GameTexts.OnePointOneData);
            if (data == DataState.Changed)
                Warning(GameTexts.ChangedData(changedFiles, BuildName(known.Reference)) +
                        (jflp ? " " + GameTexts.JflpChangesFiles : ""));
        }

        private static bool IsDataIni(string path) =>
            (path.StartsWith("data/", StringComparison.Ordinal) || path.StartsWith("exe/", StringComparison.Ordinal)) &&
            path.EndsWith(".ini", StringComparison.Ordinal);

        /// <summary>The INI files under DATA and EXE of the root, by manifest path.</summary>
        private Dictionary<string, string> RootIniFiles()
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = true,
            };
            foreach (var top in new[] { "DATA", "EXE" })
            {
                if (GameRoot.Resolve(root, top) is not { } dir || !Directory.Exists(dir))
                    continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.ini", options))
                {
                    cancel.ThrowIfCancellationRequested();
                    files.TryAdd(DataDigest.ManifestPath(Path.GetRelativePath(root, file)), file);
                }
            }
            return files;
        }

        /// <summary>Rule G10: the <c>[Libraries]</c> lines that are not retail lines and not JFLP.</summary>
        private void Plugins()
        {
            var plugins = libraries
                .Where(l => DacomLibraries.Key(l) is var key && !DacomLibraries.Retail.Contains(key) &&
                            key != DacomLibraries.Jflp)
                .ToList();
            if (plugins.Count == 0)
                return;
            var names = string.Join(", ", plugins);
            if (kind is GameKind.Vanilla or GameKind.Unknown)
                Info(GameTexts.Plugins(names));
            else
                evidence.Add($"Plugins in dacom.ini: {names}. They are part of " +
                             $"{(kind == GameKind.HdEdition ? "HD Edition" : product)}.");
        }

        /// <summary>Rule G11: Windows keeps the changes of an old program to files under Program Files in the
        /// VirtualStore folder of the user (decision D205).</summary>
        private void VirtualStore()
        {
            if (!OperatingSystem.IsWindows())
                return;
            var programFolders = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                }
                .Where(p => p.Length > 0);
            if (!programFolders.Any(p => PathRules.IsInside(root, p)))
                return;
            var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VirtualStore", root[Path.GetPathRoot(root)!.Length..]);
            try
            {
                var count = Directory.Exists(store)
                    ? Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories).Count()
                    : 0;
                evidence.Add($"VirtualStore: {count} files in {store}.");
                if (count > 0)
                    Warning(GameTexts.VirtualStore(count, store));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                evidence.Add($"VirtualStore: OpenLancer cannot read {store}: {e.Message}");
            }
        }

        /// <summary>Rule G12: the export target of mods for this game (decisions P1 and D198).</summary>
        private void Target()
        {
            if (kind == GameKind.Vanilla && version is "1.0" or "1.1")
            {
                target = ModTargets.Vanilla;
                if (version == "1.0")
                    Info(GameTexts.OnePointZeroTarget);
            }
            else if (kind == GameKind.HdEdition && build == ModTargets.HdEdition)
            {
                target = ModTargets.HdEdition;
            }
        }

        private static bool IsName(string value, string dll) =>
            FileName(value).Equals(dll, StringComparison.OrdinalIgnoreCase);

        private static string FileName(string value) => Path.GetFileName(value.Replace('\\', '/').Trim());

        private static string Short(string text)
        {
            text = text.ReplaceLineEndings(" ").Trim();
            return text.Length <= 80 ? text : text[..77] + "...";
        }
    }
}

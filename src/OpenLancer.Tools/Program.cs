using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LibreLancer;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Dump;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;

namespace OpenLancer.Tools;

/// <summary>
/// oltool: command-line tasks for OpenLancer profiles and game data.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitProblems = 1;
    private const int ExitFailure = 2;

    private const string Usage = """
        Usage: oltool <command> [arguments]

        Commands:
          verify-base <folder>              Print the data digest. Exit 1 if it is not retail v1.0 or a file is writable.
          game <folder> [--json] [--hashes] [--write-manifest <file>]
                                            Tell which game and version a folder holds. Exit 1 if it holds no game.
                                            --json: write the report as JSON, and nothing else.
                                            --hashes: add the manifest lines of the code files and the marker DLLs.
                                            The lines hold paths, sizes and hashes only, no game content.
                                            --write-manifest: write the manifest line of each file of the game folder
                                            to the file. The file must be outside each game folder. The list leaves out
                                            unins* in the root, the SAVE folder and *.log files.
          build <profile.json>              Merge the profile's mods and print the problems and the changed files.
          validate <profile.json> [--active <mod id>] [--json]
                                            Build, then report the data problems that the mods add to vanilla.
                                            --active: report only the problems that this mod adds to the mods that
                                            load before it. The later mods are not used.
                                            --json: write the problems to stdout as a JSON list of
                                            {"severity", "source", "message"}, and nothing else. A problem
                                            of a cutscene also has "cutscene": the path, the element and
                                            the value.
          deploy <profile.json>             Build and validate, then write the merged data next to the profile.
          export <profile.json> --mod <id> --target <hde-0.7.1|vanilla-1.1|openlancer>
                 [--game <folder>] [--format dropin|flmm|all] [--out <folder>] [--checklist] [--flmm-probe]
                                            Merge one mod on the files of the target game, check the result and write
                                            the packages of the target. --game: the game folder of a retail target.
                                            Without it, the base of the profile must give the target. --format: the
                                            package forms of a retail target, all by default. --out: the output
                                            folder, <profile folder>\..\exports\<mod id> by default. --checklist:
                                            print the checklist for testers, and write nothing. --flmm-probe: write
                                            the probe of the FLMM check, which is not for players.
          ini <profile.json|folder> <path>  Print a merged INI file (binary or text) as text.
          dump <profile.json|folder> <out>  Write a canonical JSON dump of the resolved game data.
          thn <profile.json|folder> <path>  Print a cutscene script (.thn) as canonical Lua text. The script comes from
                                            the merged view, so the text shows the changes of the mods.
          thn roundtrip <profile.json|folder> [<path>]
                                            Check that the canonical text of each .thn file, or of one file, gives the
                                            same tables, with exact numbers and key order, and that the text is a fixed
                                            point. Exit 1 on a difference. Each problem names the first different value.

        Exit codes: 0 = OK, 1 = the data has problems, 2 = the command could not run (wrong arguments, files, folders).
        """;

    private static TextWriter Out = Console.Out;
    private static TextWriter Err = Console.Error;

    private static int Main(string[] args)
    {
        // The engine writes its log to the console. oltool shows only its own output and reports problems itself.
        Out = Console.Out;
        Err = Console.Error;
        Console.SetOut(TextWriter.Null);
        AppHandler.ConsoleInit(silent: true);
        try
        {
            return args switch
            {
                ["verify-base", var folder] => VerifyBase(folder),
                ["game", var folder, .. var options] => Game(folder, options),
                ["build", var profile] => Build(profile),
                ["validate", var profile, .. var options] => Validate(profile, options),
                ["deploy", var profile] => Deploy(profile),
                ["export", var profile, .. var options] => Export(profile, options),
                ["ini", var source, var path] => PrintIni(source, path),
                ["dump", var source, var output] => Dump(source, output),
                ["thn", "roundtrip", var source] => ThnRoundTrip(source, null),
                ["thn", "roundtrip", var source, var path] => ThnRoundTrip(source, path),
                ["thn", var source, var path] => PrintThn(source, path),
                _ => Fail(Usage),
            };
        }
        catch (Exception e) when (e is ModLoadException or DeployException or EngineLoadException
                                      or IOException or UnauthorizedAccessException)
        {
            return Fail(e.Message);
        }
    }

    private static int VerifyBase(string folder)
    {
        if (!Directory.Exists(folder))
            return Fail($"The folder {folder} does not exist.");
        var (count, digest) = DataDigest.Compute(folder);
        var writable = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Count(f => !new FileInfo(f).IsReadOnly);
        Out.WriteLine($"{count} {digest}");
        var isRetail = count == DataDigest.RetailV1FileCount && digest == DataDigest.RetailV1;
        Out.WriteLine(isRetail ? "Matches the retail v1.0 data." : "Does NOT match the retail v1.0 data.");
        Out.WriteLine(writable == 0 ? "All files are read-only." : $"{writable} files are writable.");
        return isRetail && writable == 0 ? ExitOk : ExitProblems;
    }

    /// <summary>Prints the report of the game folder check (section 11 of the M11a design).</summary>
    private static int Game(string folder, string[] options)
    {
        var json = false;
        var hashes = false;
        string? manifest = null;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--json" when !json:
                    json = true;
                    break;
                case "--hashes" when !hashes:
                    hashes = true;
                    break;
                case "--write-manifest" when manifest == null && i + 1 < options.Length:
                    manifest = options[++i];
                    break;
                default:
                    return Fail(Usage);
            }
        }

        if (!Directory.Exists(folder) && !File.Exists(folder))
            return Fail($"The folder {folder} does not exist.");
        var report = GameDetector.Detect(folder);
        if (manifest != null)
        {
            if (report.Root == null)
                return Fail(report.Error ?? $"The folder {folder} holds no game.");
            int count;
            try
            {
                count = ManifestWriter.Write(report.Root, manifest);
            }
            catch (InvalidOperationException e)
            {
                return Fail(e.Message);
            }
            Out.WriteLine($"Wrote {count} manifest lines of {report.Root} to {Path.GetFullPath(manifest)}.");
            return ExitOk;
        }
        var lines = hashes && report.Root != null ? GameDetector.HashLines(report.Root) : [];
        if (json)
        {
            var node = JsonSerializer.SerializeToNode(report, ReportJson)!.AsObject();
            if (hashes)
                node["Hashes"] = new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            Out.WriteLine(node.ToJsonString(ReportJson));
        }
        else
        {
            foreach (var line in GameReportText(report))
                Out.WriteLine(line);
            foreach (var line in lines)
                Out.WriteLine(line);
        }
        return report.IsGame ? ExitOk : ExitProblems;
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The lines of a report as text, in the order of the M11a design.</summary>
    private static IEnumerable<string> GameReportText(GameReport report)
    {
        yield return $"Game: {report.Name}";
        if (report.Root != null)
        {
            yield return $"Root: {report.Root}";
            yield return $"Build: {report.Build} (proof: {report.Proof.ToString().ToLowerInvariant()})";
            yield return $"Export target: {report.Target ?? "none"}";
            yield return report.ExePath == null ? "Game program: not found (game data only)" : $"Game program: {report.ExePath}";
            yield return report.Data == DataState.NotCompared
                ? report.DataText
                : $"{report.DataText} ({report.ComparedFiles} INI files compared with {report.Reference})";
            foreach (var file in report.DataFiles)
                yield return $"  {file}";
        }
        foreach (var finding in report.Findings)
            yield return $"{finding.Severity}: {finding.Text}";
    }

    private static int Build(string profilePath)
    {
        var (profile, overlay) = BuildOverlay(profilePath);
        Out.WriteLine($"Profile {profile.Name}: {profile.Mods.Count} mods: " +
                      string.Join(", ", profile.Mods.Select(m => m.Id)));
        foreach (var file in overlay.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            Out.WriteLine($"  {(file.DiskPath == null ? "merged " : "file   ")} {file.Path}");
        return Report(overlay);
    }

    private static int Validate(string profilePath, string[] options)
    {
        string? active = null;
        var json = false;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--json" when !json:
                    json = true;
                    break;
                case "--active" when active == null && i + 1 < options.Length:
                    active = options[++i];
                    break;
                default:
                    return Fail(Usage);
            }
        }

        if (active == null && !json)
        {
            var (_, overlay) = BuildOverlay(profilePath);
            if (Report(overlay) != ExitOk)
                return ExitProblems;
            return ReportValidation(overlay);
        }

        var problems = Problems(ModProfile.Load(profilePath), active);
        if (json)
        {
            Out.WriteLine(ValidationJson.Write(problems));
            return problems.Any(p => p.Severity == DiagnosticSeverity.Error) ? ExitProblems : ExitOk;
        }
        foreach (var problem in problems)
            Err.WriteLine(problem);
        return Summary(problems);
    }

    /// <summary>
    /// The merge problems and the data problems that a mod adds to the mods that load before it, or that all mods add
    /// to vanilla when <paramref name="activeId"/> is null. The data checks run only when the merge has no error.
    /// </summary>
    /// <exception cref="ModLoadException">The profile has no mod with the id, or the mods before it do not
    /// merge.</exception>
    private static List<Diagnostic> Problems(ModProfile profile, string? activeId)
    {
        ModOverlay? lower = null;
        if (activeId != null)
        {
            var index = profile.IndexOf(activeId);
            if (index < 0)
                throw new ModLoadException(profile.FilePath, $"The profile has no mod '{activeId}'.");
            lower = ModOverlay.Build(profile.BaseFolder, profile.Mods.Take(index).ToList(), DllStringResourceWriter.Instance,
                ThornEvaluator.Instance);
            if (lower.HasErrors)
                throw new ModLoadException(profile.FilePath,
                    $"The mods that load before '{activeId}' have merge errors. Run 'oltool build' on a profile with them.");
        }
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.ModsThrough(activeId), DllStringResourceWriter.Instance,
            ThornEvaluator.Instance);
        var problems = (lower == null ? overlay.Diagnostics : Diagnostic.NewIn(lower.Diagnostics, overlay.Diagnostics)).ToList();
        if (!overlay.HasErrors)
        {
            var before = lower?.CreateFileSystem() ?? FileSystem.FromPath(overlay.BaseFolder);
            problems.AddRange(ProfileValidator.NewProblems(before, overlay.CreateFileSystem(), overlay.CutscenePaths));
        }
        return problems;
    }

    private static int ReportValidation(ModOverlay overlay)
    {
        var problems = ProfileValidator.NewProblems(FileSystem.FromPath(overlay.BaseFolder), overlay.CreateFileSystem(),
            overlay.CutscenePaths);
        foreach (var problem in problems)
            Err.WriteLine(problem);
        return Summary(problems);
    }

    private static int Summary(IReadOnlyList<Diagnostic> problems)
    {
        var errors = problems.Count(p => p.Severity == DiagnosticSeverity.Error);
        Out.WriteLine(errors > 0
            ? $"Validation failed: {errors} errors, {problems.Count - errors} warnings."
            : $"Validation OK: {problems.Count} warnings.");
        return errors > 0 ? ExitProblems : ExitOk;
    }

    private static int Dump(string source, string output)
    {
        File.WriteAllText(output, DataDump.Create(OpenSource(source)));
        Out.WriteLine($"Wrote {output}");
        return ExitOk;
    }

    private static int Deploy(string profilePath)
    {
        var (profile, overlay) = BuildOverlay(profilePath);
        if (Report(overlay) != ExitOk || ReportValidation(overlay) != ExitOk)
            return ExitProblems;
        ProfileDeployer.Result result;
        try
        {
            result = ProfileDeployer.Deploy(overlay, profile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"{e.Message}{Environment.NewLine}The deploy did not complete. Run deploy again to complete the folder.");
        }
        if (result.PreviousDeployIncomplete)
            Out.WriteLine("The last deploy did not complete. This deploy completed the folder.");
        if (result.Folder.Any(c => c > 0x7F))
            Out.WriteLine("Note: the deploy folder path has characters outside ASCII. lancer.exe reads it correctly " +
                          "until the game saves its settings. After that, run 'oltool deploy' again.");
        Out.WriteLine($"Deployed to {result.Folder}: {result.Copied} copied, {result.Written} written, " +
                      $"{result.Deleted} deleted, {result.Unchanged} unchanged.");
        Out.WriteLine($"Start the game: lancer.exe \"{ProfileDeployer.GameConfigPath(profile)}\"");
        return ExitOk;
    }

    /// <summary>Exports one mod of a profile for one target (section 7.14 of the M11 design).</summary>
    private static int Export(string profilePath, string[] options)
    {
        string? modId = null, targetId = null, game = null, format = null, output = null;
        bool checklist = false, probe = false;
        for (var i = 0; i < options.Length; i++)
        {
            var hasValue = i + 1 < options.Length;
            switch (options[i])
            {
                case "--mod" when modId == null && hasValue:
                    modId = options[++i];
                    break;
                case "--target" when targetId == null && hasValue:
                    targetId = options[++i];
                    break;
                case "--game" when game == null && hasValue:
                    game = options[++i];
                    break;
                case "--format" when format == null && hasValue:
                    format = options[++i];
                    break;
                case "--out" when output == null && hasValue:
                    output = options[++i];
                    break;
                case "--checklist" when !checklist:
                    checklist = true;
                    break;
                case "--flmm-probe" when !probe:
                    probe = true;
                    break;
                default:
                    return Fail(Usage);
            }
        }
        if (modId == null || targetId == null || ExportTarget.Find(targetId) is not { } target)
            return Fail(Usage);
        ExportForms forms;
        switch (format)
        {
            case null or "all":
                forms = ExportForms.All;
                break;
            case "dropin":
                forms = ExportForms.DropIn;
                break;
            case "flmm":
                forms = ExportForms.Flmm;
                break;
            default:
                return Fail(Usage);
        }

        var profile = ModProfile.Load(profilePath);
        var index = profile.IndexOf(modId);
        if (index < 0)
            return Fail($"The profile has no mod '{modId}'.");
        GameReport? report = null;
        if (target.IsRetail)
        {
            if (game != null && !Directory.Exists(game))
                return Fail($"The folder {game} does not exist.");
            report = game != null ? GameDetector.Detect(game) : GameDetector.CheckRoot(profile.BaseFolder);
        }
        var result = ModExport.Run(new ExportRequest
        {
            Mod = profile.Mods[index],
            Targets = [target],
            BaseOf = _ => report,
            OutputFolder = output ?? ModExport.DefaultFolder(profile.FilePath, modId),
            Forms = forms,
            FlmmProbe = probe,
            ChecklistOnly = checklist,
            OpenLancerBase = GameBase.Standard(profile.BaseFolder),
            StringWriter = DllStringResourceWriter.Instance,
            Thn = ThornEvaluator.Instance,
        });
        foreach (var problem in result.Problems)
            Err.WriteLine(problem);
        if (checklist)
        {
            foreach (var line in result.Checklist)
                Out.WriteLine(line);
            return result.Problems.Any(p => p.Severity == DiagnosticSeverity.Error) ? ExitProblems : ExitOk;
        }
        foreach (var package in result.Packages)
            Out.WriteLine(package.Path != null ? $"Wrote {package.Path}" : $"Refused {package.FileName}");
        return result.Succeeded ? ExitOk : ExitProblems;
    }

    private static int PrintIni(string source, string path)
    {
        var fs = OpenSource(source);
        if (!fs.FileExists(path))
            return Fail($"{path} is not in {source}.");
        using var input = fs.Open(path);
        var sections = IniFile.ParseFile(path, input, preparse: false).ToList();
        using var output = Console.OpenStandardOutput();
        IniWriter.WriteIni(output, sections);
        return ExitOk;
    }

    /// <summary>Prints the canonical text of a script. The bytes come through the file system, because a merged file has
    /// no file on disk.</summary>
    private static int PrintThn(string source, string path)
    {
        var fs = OpenSource(source);
        if (!fs.FileExists(path))
            return Fail($"{path} is not in {source}.");
        try
        {
            Out.Write(ThnWriter.Write(ThornEvaluator.Instance.Evaluate(fs.ReadAllBytes(path), path)));
            return ExitOk;
        }
        catch (ThnEvaluationException e)
        {
            Err.WriteLine(e.Message);
            return ExitProblems;
        }
    }

    /// <summary>Evaluates each script, writes it, evaluates the text and compares. Prints one line for each script
    /// with a problem, then a summary.</summary>
    private static int ThnRoundTrip(string source, string? path)
    {
        var fs = OpenSource(source);
        List<string> paths;
        if (path == null)
            paths = ThnFiles(fs);
        else if (fs.FileExists(path))
            paths = [path];
        else
            return Fail($"{path} is not in {source}.");

        var failed = 0;
        foreach (var file in paths)
        {
            string line;
            try
            {
                var problem = ThnWriter.RoundTripProblem(ThornEvaluator.Instance.Evaluate(fs.ReadAllBytes(file), file),
                    ThornEvaluator.Instance, file);
                if (problem == null)
                    continue;
                line = $"{file}: {problem}";
            }
            catch (ThnEvaluationException e)
            {
                // The message starts with the file.
                line = e.Message;
            }
            failed++;
            Err.WriteLine(line);
        }
        Out.WriteLine($"Round trip: {paths.Count - failed} of {paths.Count} scripts give the same tables, with key " +
                      "order, and a fixed point text.");
        return failed == 0 ? ExitOk : ExitProblems;
    }

    /// <summary>The paths of all <c>.thn</c> files of a file system, in a stable order.</summary>
    private static List<string> ThnFiles(FileSystem fs)
    {
        var files = new List<string>();
        var folders = new Stack<string>([""]);
        while (folders.TryPop(out var folder))
        {
            foreach (var name in fs.GetFiles(folder))
            {
                if (name.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
                    files.Add(folder.Length == 0 ? name : $@"{folder}\{name}");
            }
            foreach (var name in fs.GetDirectories(folder))
                folders.Push(folder.Length == 0 ? name : $@"{folder}\{name}");
        }
        files.Sort(VfsPath.Comparer);
        return files;
    }

    /// <summary>A profile file gives its merged view. A folder gives its plain contents.</summary>
    private static FileSystem OpenSource(string source)
    {
        if (Directory.Exists(source))
            return FileSystem.FromPath(source);
        if (!File.Exists(source))
            throw new ModLoadException(source, "This is not a profile file or a folder.");
        var (_, overlay) = BuildOverlay(source);
        if (overlay.HasErrors)
            throw new ModLoadException(source, "The profile has merge errors. Run 'oltool build' to see them.");
        return overlay.CreateFileSystem();
    }

    private static (ModProfile, ModOverlay) BuildOverlay(string profilePath)
    {
        var profile = ModProfile.Load(profilePath);
        return (profile, ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance));
    }

    private static int Report(ModOverlay overlay)
    {
        foreach (var d in overlay.Diagnostics)
            Err.WriteLine(d);
        Out.WriteLine(overlay.HasErrors
            ? $"Failed: {overlay.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} errors."
            : $"OK: {overlay.Diagnostics.Count} warnings.");
        return overlay.HasErrors ? ExitProblems : ExitOk;
    }

    private static int Fail(string message)
    {
        Err.WriteLine(message);
        return ExitFailure;
    }
}

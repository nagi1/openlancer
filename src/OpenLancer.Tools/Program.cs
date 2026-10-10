using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Dump;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
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
          build <profile.json>              Merge the profile's mods and print the problems and the changed files.
          validate <profile.json> [--active <mod id>] [--json]
                                            Build, then report the data problems that the mods add to vanilla.
                                            --active: report only the problems that this mod adds to the mods that
                                            load before it. The later mods are not used.
                                            --json: write the problems to stdout as a JSON list of
                                            {"severity", "source", "message"}, and nothing else.
          deploy <profile.json>             Build and validate, then write the merged data next to the profile.
          ini <profile.json|folder> <path>  Print a merged INI file (binary or text) as text.
          dump <profile.json|folder> <out>  Write a canonical JSON dump of the resolved game data.
          thn <profile.json|folder> <path>  Print a cutscene script (.thn) as readable Lua text.

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
                ["build", var profile] => Build(profile),
                ["validate", var profile, .. var options] => Validate(profile, options),
                ["deploy", var profile] => Deploy(profile),
                ["ini", var source, var path] => PrintIni(source, path),
                ["dump", var source, var output] => Dump(source, output),
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
        var count = profile.Mods.Count;
        ModOverlay? lower = null;
        if (activeId != null)
        {
            count = profile.Mods.Select(m => m.Id).ToList().IndexOf(activeId) + 1;
            if (count == 0)
                throw new ModLoadException(profile.FilePath, $"The profile has no mod '{activeId}'.");
            lower = ModOverlay.Build(profile.BaseFolder, profile.Mods.Take(count - 1).ToList(), DllStringResourceWriter.Instance);
            if (lower.HasErrors)
                throw new ModLoadException(profile.FilePath,
                    $"The mods that load before '{activeId}' have merge errors. Run 'oltool build' on a profile with them.");
        }
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.Mods.Take(count).ToList(), DllStringResourceWriter.Instance);
        var problems = (lower == null ? overlay.Diagnostics : Diagnostic.NewIn(lower.Diagnostics, overlay.Diagnostics)).ToList();
        if (!overlay.HasErrors)
        {
            var before = lower?.CreateFileSystem() ?? FileSystem.FromPath(overlay.BaseFolder);
            problems.AddRange(ProfileValidator.NewProblems(before, overlay.CreateFileSystem()));
        }
        return problems;
    }

    private static int ReportValidation(ModOverlay overlay)
    {
        var problems = ProfileValidator.NewProblems(FileSystem.FromPath(overlay.BaseFolder), overlay.CreateFileSystem());
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

    private static int PrintThn(string source, string path)
    {
        var fs = OpenSource(source);
        // The decompiler reads a file on disk. Scripts are never merged, so the base or mod file is the real one.
        if (fs.GetBackingFileName(path) is not { } file || !File.Exists(file))
            return Fail($"{path} is not a file in {source}.");
        // Scripts include other scripts relative to EXE, as the engine reads them (GameItemDb.ThornReadCallback).
        Out.Write(LibreLancer.Thn.ThnDecompile.Decompile(file, include => fs.ReadAllBytes("EXE/" + include)));
        return ExitOk;
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
        return (profile, ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance));
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Export;

/// <summary>The package forms of a retail target.</summary>
[Flags]
public enum ExportForms
{
    DropIn = 1,
    Flmm = 2,
    All = DropIn | Flmm,
}

/// <summary>What an export makes (milestone M11).</summary>
public sealed record ExportRequest
{
    public required Mod Mod { get; init; }

    /// <summary>The targets, in the order of <see cref="ExportTarget.All"/>.</summary>
    public required IReadOnlyList<ExportTarget> Targets { get; init; }

    /// <summary>The check of the linked game that is the base of a retail target, or null when no game gives the
    /// target.</summary>
    public required Func<ExportTarget, GameReport?> BaseOf { get; init; }

    /// <summary>The folder of the packages. The export makes it.</summary>
    public required string OutputFolder { get; init; }

    public ExportForms Forms { get; init; } = ExportForms.All;

    /// <summary>True to make the probe of the FLMM check (see <see cref="FlmmPackage.Write"/>).</summary>
    public bool FlmmProbe { get; init; }

    /// <summary>True to make the checklist only, and write no file.</summary>
    public bool ChecklistOnly { get; init; }

    /// <summary>The base of the checklist of the target OpenLancer, or null. A retail target uses its own base.</summary>
    public GameBase? OpenLancerBase { get; init; }

    public required IStringResourceWriter StringWriter { get; init; }

    public required IThnEvaluator Thn { get; init; }
}

/// <summary>One package of an export.</summary>
/// <param name="Path">The full path of the written file, or null when the export refused the package.</param>
public sealed record ExportedPackage(ExportTarget Target, string FileName, string? Path);

/// <summary>The result of an export.</summary>
public sealed record ExportResult(
    string OutputFolder,
    IReadOnlyList<ExportedPackage> Packages,
    IReadOnlyList<Diagnostic> Problems,
    IReadOnlyList<string> Checklist,
    IReadOnlyDictionary<ExportTarget, TargetFiles> Builds)
{
    /// <summary>True when the export wrote each package that the request asks for.</summary>
    public bool Succeeded => Packages.Count > 0 && Packages.All(p => p.Path != null) &&
                             Problems.All(p => p.Severity != DiagnosticSeverity.Error);
}

/// <summary>
/// The export of one mod (milestone M11): for each retail target, the merge on the files of a linked game, the format
/// checks, the drop-in zip and the FLMM package. For the target OpenLancer, a zip of the mod folder. Then the text of
/// the Nexus page and the checklist for testers.
/// </summary>
/// <remarks>
/// The export only reads the game folders and the mod folder. It writes only into the output folder, which must not
/// overlap a game folder or the mod folder.
/// </remarks>
public static class ModExport
{
    public const string NexusPageFile = "nexus-page.txt";
    public const string ChecklistFile = "CHECKLIST.txt";

    /// <summary>The text after the errors of a target.</summary>
    public const string Stopped = "The export stopped. Fix the errors, then export again.";

    /// <summary>The default output folder of a mod: <c>exports\&lt;mod id&gt;</c> next to the folder of the profile
    /// folder.</summary>
    public static string DefaultFolder(string profilePath, string modId) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profilePath))!, "..", "exports", modId));

    /// <summary>The prefix of the file name of the text DLL of an exported mod.</summary>
    public static string DllName(string modId) => $"ol_{modId}.dll";

    public static ExportResult Run(ExportRequest request)
    {
        var mod = request.Mod;
        var output = Path.GetFullPath(request.OutputFolder);
        var problems = new List<Diagnostic>();
        var packages = new List<ExportedPackage>();
        var checklist = new List<string>();
        var builds = new Dictionary<ExportTarget, TargetFiles>();
        var nexus = new List<(ExportTarget, IReadOnlyList<string>)>();

        if (OutputRefusal(output, request) is { } refusal)
        {
            problems.Add(Diagnostic.Error(output, refusal));
            return new ExportResult(output, packages, problems, checklist, builds);
        }
        if (!request.ChecklistOnly && InGitWorkTree(output))
            problems.Add(Diagnostic.Warning(output, "The export folder is in a git repository. The packages hold merged " +
                                                    "game files. Do not commit them."));

        foreach (var target in request.Targets)
        {
            var written = new List<string>();
            var targetProblems = new List<Diagnostic>();
            if (target.Refusal(mod.Manifest) is { } reason)
            {
                problems.Add(Diagnostic.Error(mod.Folder, reason));
                packages.Add(new ExportedPackage(target, target.IsRetail ? DropInPackage.FileName(mod.Manifest, target)
                    : ModZip.FileName(mod.Manifest), null));
                continue;
            }
            if (!target.IsRetail)
            {
                if (request.OpenLancerBase is { } openLancerBase)
                {
                    var build = TargetBuild.Run(openLancerBase, [mod], ModOverlay.StringsDllName, request.StringWriter,
                        request.Thn);
                    builds[target] = build;
                    Checklist(checklist, target, build);
                }
                if (request.ChecklistOnly)
                    continue;
                var zip = ModZip.Write(mod);
                problems.AddRange(zip.Problems);
                packages.Add(Save(output, zip, target, written));
                nexus.Add((target, written));
                continue;
            }

            var report = request.BaseOf(target);
            if (target.BaseRefusal(report) is { } noBase)
            {
                problems.Add(Diagnostic.Error(mod.Folder, noBase));
                AddRefused(packages, request, target);
                continue;
            }
            targetProblems.AddRange(target.BaseWarnings(report!).Select(w => Diagnostic.Warning(report!.Root!, w)));
            targetProblems.AddRange(PackageRules.RefusedFiles(mod));
            var gameBase = GameBase.Standard(report!.Root!);
            var files = TargetBuild.Run(gameBase, [mod], DllName(mod.Id), request.StringWriter, request.Thn);
            builds[target] = files;
            targetProblems.AddRange(files.Diagnostics);
            targetProblems.AddRange(FormatChecks.Run(files));
            targetProblems.AddRange(TargetChecks.Run(files));
            if (RetailReference(request, target) is { } retail)
                targetProblems.AddRange(TargetChecks.HdEditionLines(files, retail));
            if (files.File(ModOverlay.FreelancerIniPath) != null && FreelancerIniDiffers(gameBase, report))
                targetProblems.Add(Diagnostic.Warning(ModOverlay.FreelancerIniPath,
                    $"Your freelancer.ini differs from the file list of {GameDetector.BuildName(report.Build!)}. The " +
                    "drop-in zip carries these differences to each player."));
            problems.AddRange(targetProblems);
            if (targetProblems.Any(p => p.Severity == DiagnosticSeverity.Error))
            {
                problems.Add(Diagnostic.Error(mod.Folder, Stopped));
                AddRefused(packages, request, target);
                continue;
            }
            Checklist(checklist, target, files);
            if (request.ChecklistOnly)
                continue;

            if (request.Forms.HasFlag(ExportForms.DropIn))
            {
                var dropIn = DropInPackage.Write(files, mod.Manifest, target);
                problems.AddRange(dropIn.Problems);
                packages.Add(Save(output, dropIn, target, written));
            }
            if (request.Forms.HasFlag(ExportForms.Flmm))
            {
                var placeholders = TargetBuild.Run(gameBase, [mod], DllName(mod.Id), request.StringWriter, request.Thn,
                    keepPlaceholders: true);
                var flmm = FlmmPackage.Write(placeholders, mod, target, request.FlmmProbe);
                problems.AddRange(flmm.Problems);
                packages.Add(Save(output, flmm, target, written));
            }
            nexus.Add((target, written));
        }

        if (!request.ChecklistOnly && packages.Any(p => p.Path != null))
        {
            AtomicFile.WriteAllBytes(Path.Combine(output, NexusPageFile),
                Encoding.UTF8.GetBytes(PackageTexts.NexusPage(mod.Manifest, nexus)));
            AtomicFile.WriteAllBytes(Path.Combine(output, ChecklistFile),
                Encoding.UTF8.GetBytes(string.Join("\r\n", checklist) + "\r\n"));
        }
        return new ExportResult(output, packages, problems, checklist, builds);
    }

    /// <summary>The reason why the export cannot write into the folder, or null: the folder overlaps the mod folder
    /// or the folder of a base, or it is in a game folder.</summary>
    private static string? OutputRefusal(string output, ExportRequest request)
    {
        if (PathRules.Overlaps(output, request.Mod.Folder))
            return $"The export folder {output} overlaps the mod folder {request.Mod.Folder}. Pick another folder.";
        foreach (var target in request.Targets.Where(t => t.IsRetail))
        {
            if (request.BaseOf(target)?.Root is { } root && PathRules.Overlaps(output, root))
                return $"The export folder {output} overlaps the game folder {root}. OpenLancer does not write into the " +
                       "game folder. Pick another folder.";
        }
        // Also a game that is not a base of the export, for example the profile base or another install.
        if (GameRoot.Containing(output) is { } game)
            return $"The export folder {output} is in the game folder {game}. OpenLancer does not write into a game " +
                   "folder. Pick another folder.";
        return null;
    }

    private static void AddRefused(List<ExportedPackage> packages, ExportRequest request, ExportTarget target)
    {
        if (request.ChecklistOnly)
            return;
        if (request.Forms.HasFlag(ExportForms.DropIn))
            packages.Add(new ExportedPackage(target, DropInPackage.FileName(request.Mod.Manifest, target), null));
        if (request.Forms.HasFlag(ExportForms.Flmm))
            packages.Add(new ExportedPackage(target, FlmmPackage.FileName(request.Mod.Manifest, target, request.FlmmProbe),
                null));
    }

    private static ExportedPackage Save(string output, Package package, ExportTarget target, List<string> written)
    {
        if (package.Bytes == null)
            return new ExportedPackage(target, package.FileName, null);
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, package.FileName);
        AtomicFile.WriteAllBytes(path, package.Bytes);
        written.Add(package.FileName);
        return new ExportedPackage(target, package.FileName, path);
    }

    private static void Checklist(List<string> checklist, ExportTarget target, TargetFiles files)
    {
        if (checklist.Count > 0)
            checklist.Add("");
        checklist.Add($"Target: {target.Name}");
        checklist.AddRange(PackageTexts.Checklist(files));
    }

    /// <summary>The files of a linked Freelancer 1.0 or 1.1 game for the check of the lines that HD Edition changed, or
    /// null. Only the target HD Edition 0.7.1 uses it.</summary>
    private static GameBase? RetailReference(ExportRequest request, ExportTarget target) =>
        target == ExportTarget.HdEdition && request.BaseOf(ExportTarget.Vanilla) is
            { CanBeBase: true, Target: ModTargets.Vanilla } report
            ? report.Base
            : null;

    /// <summary>True when the <c>freelancer.ini</c> of a base differs from the row of the file list of its build.</summary>
    private static bool FreelancerIniDiffers(GameBase gameBase, GameReport report)
    {
        if (report.Build is not { } build || KnownBuilds.Embedded.Reference(build) is not { } known ||
            !known.Rows.TryGetValue(DataDigest.ManifestPath(ModOverlay.FreelancerIniPath), out var row))
            return false;
        var path = Path.Combine(gameBase.Root, PathRules.WithDiskCase(gameBase.Root, ModOverlay.FreelancerIniPath));
        return !File.Exists(path) || new FileInfo(path).Length != row.Size || DataDigest.HashFile(path) != row.Sha256;
    }

    /// <summary>True when the folder is inside a git work tree: a folder above it has a <c>.git</c> folder or
    /// file.</summary>
    private static bool InGitWorkTree(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return true;
        }
        return false;
    }
}

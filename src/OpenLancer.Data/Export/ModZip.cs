using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;

namespace OpenLancer.Data.Export;

/// <summary>
/// The package of the target OpenLancer (milestones M11 and M11c): a zip of the mod folder with <c>mod.json</c>,
/// <c>strings.json</c>, <c>publish.json</c>, <c>patches/</c>, <c>files/</c> and the listed screenshots that pass the
/// screenshot rule. It leaves out the save folder of the editor, the prop groups and the hidden, system and temporary
/// files.
/// </summary>
public static class ModZip
{
    public static string FileName(ModManifest mod) => $"{mod.Id}-{mod.Version}-{ModTargets.OpenLancer}.zip";

    /// <summary>Writes the zip of a mod folder. A file in <c>files/</c> with the bytes of a known game file refuses the
    /// package.</summary>
    public static Package Write(Mod mod) => Write(mod, null);

    /// <param name="builds">The builds of the game file rule, or null for the embedded manifests.</param>
    internal static Package Write(Mod mod, KnownBuilds? builds) =>
        Plan(mod, PublishTexts.Of(mod.Manifest), [], "", builds).ToPackage();

    /// <summary>The plan of the zip.</summary>
    /// <param name="screenshots">The listed screenshots that passed the screenshot rule, by their path relative to the
    /// mod folder.</param>
    /// <param name="content">The content fingerprint of the mod. It is the guard digest of the zip, because
    /// <c>mod.json</c> in the zip holds the display name and the version (decision D589).</param>
    internal static PackagePlan Plan(Mod mod, PublishTexts texts, IReadOnlyList<string> screenshots, string content,
        KnownBuilds? builds)
    {
        var problems = new List<Diagnostic>();
        var entries = new List<(string Name, byte[] Bytes)>();
        foreach (var (relative, path) in Files(mod.Folder))
        {
            if (PackageRules.SkipReason(path, relative) is { } skip)
            {
                problems.Add(Diagnostic.Warning(path, PackageRules.SkippedText(relative, skip)));
                continue;
            }
            var bytes = File.ReadAllBytes(path);
            if (relative.StartsWith(Mod.FilesFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                PackageRules.GameFile(relative, bytes, builds) is { } gameFile)
                problems.Add(gameFile);
            entries.Add(($"{mod.Id}/{relative}", bytes));
        }
        foreach (var relative in screenshots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(($"{mod.Id}/{relative}",
                File.ReadAllBytes(Path.Combine(mod.Folder, relative.Replace('/', Path.DirectorySeparatorChar)))));
        }
        var name = FileName(mod.Manifest);
        if (problems.Any(p => p.Severity == DiagnosticSeverity.Error))
            return new PackagePlan(name, null, problems, null);
        var readme = PackageTexts.Readme(texts, ExportTarget.OpenLancer, PackageForm.ModFolder, null, []);
        entries.Add(($"{mod.Id}/{PackageTexts.ReadmeName(mod.Manifest)}", Encoding.UTF8.GetBytes(readme)));
        return new PackagePlan(name, () => DeterministicZip.Write(entries), problems, content) { Readme = readme };
    }

    /// <summary>The files of the mod folder that the package can hold: <c>mod.json</c>, <c>strings.json</c>,
    /// <c>publish.json</c> and the files of <c>patches/</c> and <c>files/</c>, by their path relative to the mod
    /// folder.</summary>
    private static IEnumerable<(string Relative, string Path)> Files(string folder)
    {
        foreach (var name in new[] { ModManifest.FileName, Mod.StringsFile, PublishSettings.FileName })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
                yield return (name, path);
        }
        foreach (var sub in new[] { Mod.PatchesFolder, Mod.FilesFolder })
        {
            var root = Path.Combine(folder, sub);
            if (!Directory.Exists(root))
                continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                yield return (Path.GetRelativePath(folder, path), path);
        }
    }
}

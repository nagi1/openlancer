using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Export;

/// <summary>
/// The package of the target OpenLancer (milestone M11): a zip of the mod folder with <c>mod.json</c>,
/// <c>strings.json</c>, <c>patches/</c> and <c>files/</c>. It leaves out the save folder of the editor, the prop groups
/// and the hidden, system and temporary files.
/// </summary>
public static class ModZip
{
    public static string FileName(ModManifest mod) => $"{mod.Id}-{mod.Version}-{ModTargets.OpenLancer}.zip";

    /// <summary>Writes the zip of a mod folder. A file in <c>files/</c> with the bytes of a known game file refuses the
    /// package.</summary>
    public static Package Write(Mod mod) => Write(mod, null);

    /// <param name="builds">The builds of the game file rule, or null for the embedded manifests.</param>
    internal static Package Write(Mod mod, KnownBuilds? builds)
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
        var name = FileName(mod.Manifest);
        if (problems.Any(p => p.Severity == DiagnosticSeverity.Error))
            return new Package(name, null, problems);
        var readme = PackageTexts.Readme(mod.Manifest, ExportTarget.OpenLancer, PackageForm.ModFolder, null, []);
        entries.Add(($"{mod.Id}/{PackageTexts.ReadmeName(mod.Manifest)}", Encoding.UTF8.GetBytes(readme)));
        return new Package(name, DeterministicZip.Write(entries), problems);
    }

    /// <summary>The files of the mod folder that the package can hold: <c>mod.json</c>, <c>strings.json</c> and the
    /// files of <c>patches/</c> and <c>files/</c>, by their path relative to the mod folder.</summary>
    private static IEnumerable<(string Relative, string Path)> Files(string folder)
    {
        foreach (var name in new[] { ModManifest.FileName, Mod.StringsFile })
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

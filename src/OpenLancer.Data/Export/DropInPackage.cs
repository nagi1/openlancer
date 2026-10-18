using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Export;

/// <summary>A package file and its problems.</summary>
/// <param name="Bytes">The bytes of the package, or null when a problem refuses the package.</param>
public sealed record Package(string FileName, byte[]? Bytes, IReadOnlyList<Diagnostic> Problems);

/// <summary>
/// The drop-in zip of a retail target (milestone M11): the folders <c>DATA</c> and <c>EXE</c> at the top of the zip,
/// with the merged and new files of the mod, and the README. The player extracts it over the game folder.
/// </summary>
public static class DropInPackage
{
    public static string FileName(ModManifest mod, ExportTarget target) => $"{mod.Id}-{mod.Version}-{target.Id}.zip";

    /// <summary>Writes the zip of the files of a target build. A file with the bytes of a known game file refuses the
    /// package.</summary>
    public static Package Write(TargetFiles files, ModManifest mod, ExportTarget target) => Write(files, mod, target, null);

    /// <param name="builds">The builds of the game file rule, or null for the embedded manifests.</param>
    internal static Package Write(TargetFiles files, ModManifest mod, ExportTarget target, KnownBuilds? builds)
    {
        var entries = files.Files
            .Select(f => (Name: PathRules.WithDiskCase(files.Base.Root, files.Base.ToDisk(f.Path)), f.Bytes))
            .ToList();
        var problems = entries.Select(e => PackageRules.GameFile(e.Name, e.Bytes, builds)).OfType<Diagnostic>().ToList();
        var name = FileName(mod, target);
        if (problems.Count > 0)
            return new Package(name, null, problems);
        var readme = PackageTexts.Readme(mod, target, PackageForm.DropIn, files, entries.Select(e => e.Name).ToList());
        entries.Add((PackageTexts.ReadmeName(mod), Encoding.UTF8.GetBytes(readme)));
        return new Package(name, DeterministicZip.Write(entries), problems);
    }
}

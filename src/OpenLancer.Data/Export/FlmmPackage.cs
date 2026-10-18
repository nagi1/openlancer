using System.Linq;
using System.Text;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Export;

/// <summary>
/// The FLMM package of a retail target (milestone M11): <c>&lt;mod id&gt;.flmod</c>, a zip with <c>script.xml</c> at
/// its top and the whole files of the mod in the folder tree of the game. The export wraps it with the README in a zip.
/// </summary>
public static class FlmmPackage
{
    /// <summary>True when the FLMM check of the M11 manual test confirmed the package form. Until then, the editor
    /// shows the form as a preview.</summary>
    public static bool Checked => false;

    public static string FileName(ModManifest mod, ExportTarget target, bool probe) =>
        $"{mod.Id}-{mod.Version}-{target.Id}-flmm{(probe ? "-probe" : "")}.zip";

    /// <summary>Writes the package from a target build that keeps the text placeholders. A refusal of an operation, or
    /// a file with the bytes of a known game file, refuses the package.</summary>
    /// <param name="probe">True for the probe of the FLMM check: the macros hold each text as it is.</param>
    public static Package Write(TargetFiles files, Mod mod, ExportTarget target, bool probe) =>
        Write(files, mod, target, probe, null);

    /// <param name="builds">The builds of the game file rule, or null for the embedded manifests.</param>
    internal static Package Write(TargetFiles files, Mod mod, ExportTarget target, bool probe, KnownBuilds? builds)
    {
        var manifest = mod.Manifest;
        var script = FlmmScript.Build(files, mod, $"{manifest.Name} {manifest.Version} for {target.Name}", probe);
        var problems = script.Problems.ToList();
        problems.AddRange(script.Files.Select(f => PackageRules.GameFile(f.Path, f.Bytes, builds)).OfType<Diagnostic>());
        var name = FileName(manifest, target, probe);
        if (problems.Any(p => p.Severity == DiagnosticSeverity.Error))
            return new Package(name, null, problems);

        var tree = script.Files.Select(f => (f.Path, f.Bytes)).ToList();
        tree.Add((FlmmScript.FileName, Encoding.ASCII.GetBytes(script.Xml)));
        var flmod = DeterministicZip.Write(tree);
        var readme = PackageTexts.Readme(manifest, target, PackageForm.Flmm, files, script.Files.Select(f => f.Path).ToList(),
            probe);
        var zip = DeterministicZip.Write([($"{manifest.Id}.flmod", flmod),
            (PackageTexts.ReadmeName(manifest), Encoding.UTF8.GetBytes(readme))]);
        return new Package(name, zip, problems);
    }
}

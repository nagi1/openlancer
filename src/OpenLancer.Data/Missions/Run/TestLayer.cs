using System;
using System.IO;
using System.Text;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Missions.Run;

/// <summary>
/// The test layer of a play from a trigger (section 5.4 of the M6c design, MSD decision D411): a mod folder that the
/// editor makes for one play. It holds <c>mod.json</c> and a patch of the mission script with the setup triggers. The
/// play loads it after the mods of the play. It is not a mod of the profile, so the export, the publish flow and the
/// other plays never see it.
/// </summary>
/// <remarks>The editor writes the layer into a new folder beside it, then moves the new folder into place with one
/// rename. So the play process never reads half a layer. A layer of an exact start has no patch.</remarks>
public static class TestLayer
{
    /// <summary>The id of the mod of the layer.</summary>
    public const string ModId = "openlancer-play-start";

    private const string Manifest = """
        {
          "id": "openlancer-play-start",
          "name": "OpenLancer play start",
          "version": "1.0.0",
          "formatVersion": 2,
          "tier": 1
        }
        """;

    /// <summary>Writes the layer of a plan into a folder, in place of the layer that the folder holds.</summary>
    /// <param name="folder">The folder of the layer, for example <c>&lt;play folder&gt;\start\layer</c>.</param>
    /// <exception cref="IOException">The folder cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder cannot be written.</exception>
    public static void Write(string folder, StartPlan plan) => Write(folder, plan.Script, plan.SetupPatch);

    /// <summary>Writes a layer with a patch of a script, or without a patch when the text is empty.</summary>
    /// <param name="script">The install path of the script.</param>
    /// <param name="patch">The INI text of the patch.</param>
    public static void Write(string folder, string script, string patch)
    {
        folder = Path.GetFullPath(folder);
        var fresh = folder + ".new";
        var old = folder + ".old";
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        Delete(fresh);
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, ModManifest.FileName), Manifest.ReplaceLineEndings("\n"), Encoding.UTF8);
        if (patch.Length > 0)
        {
            var normal = VfsPath.Normalize(script) ?? script;
            var target = Path.Combine([fresh, Mod.PatchesFolder, .. normal.Split('\\', StringSplitOptions.RemoveEmptyEntries)]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, patch, new UTF8Encoding(false));
        }
        Delete(old);
        if (Directory.Exists(folder))
            Directory.Move(folder, old);
        Directory.Move(fresh, folder);
        Delete(old);
    }

    private static void Delete(string folder)
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.DataTests;

/// <summary>
/// A temporary folder with a profile of the retail data and a new empty mod in it, made with the calls of the editor's
/// Mod > New mod. Copies of other mods can load before the new mod. The workspace disposes each session that it
/// opened, because a session holds the lock of its mod. Then it deletes the folder.
/// </summary>
internal sealed class RetailWorkspace : IDisposable
{
    private readonly List<EditSession> sessions = [];

    /// <param name="modName">The name of the new mod, or null for its id.</param>
    /// <param name="lowerMods">The folders of the mods that load before the new mod, in load order, or null for none.
    /// The profile loads copies of them, so a session writes no file into the folders.</param>
    public RetailWorkspace(string baseFolder, string modId, string? modName = null, IReadOnlyList<string>? lowerMods = null)
    {
        ModId = modId;
        Root = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        ProfilePath = Path.Combine(Root, "profiles", "retail.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        var mods = Path.Combine(Root, "mods");
        var copies = (lowerMods ?? []).Select(m => Copy(m, Path.Combine(mods, Path.GetFileName(m)))).ToList();
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(new { @base = baseFolder, mods = copies }));
        ModFolder = ModWorkspace.CreateMod(mods, modId, modName ?? modId);
        ModWorkspace.AddToProfile(ProfilePath, ModFolder);
    }

    public string Root { get; }

    public string ProfilePath { get; }

    public string ModId { get; }

    public string ModFolder { get; }

    /// <summary>Opens an edit session with the new mod as the active mod.</summary>
    public EditSession Open()
    {
        var session = EditSession.Open(ModProfile.Load(ProfilePath), ModId, DllStringResourceWriter.Instance,
            ThornEvaluator.Instance);
        sessions.Add(session);
        return session;
    }

    public void Dispose()
    {
        foreach (var session in sessions)
            session.Dispose();
        Directory.Delete(Root, true);
    }

    /// <summary>
    /// Copies the files of a mod folder into a new folder, and returns the new folder. The copy leaves out the
    /// <see cref="SaveTransaction.FolderName"/> folder: it holds the state of an editor that has the mod open (its lock,
    /// its recovery file and its save journal), not mod content. An editor that runs on the machine holds that lock
    /// open, and its files would change what the test sees.
    /// </summary>
    internal static string Copy(string source, string target)
    {
        var editorState = Path.Combine(source, SaveTransaction.FolderName) + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(editorState, StringComparison.OrdinalIgnoreCase))
                continue;
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        return target;
    }
}

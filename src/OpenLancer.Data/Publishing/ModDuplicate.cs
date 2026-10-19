using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>
/// Duplicate of a mod (decision D573 and section 12.2 of the M11c design). The copy has a new id, a new display name
/// and no page link. It does not get the save folder of the editor and the files that a package skips. The copy is not
/// in a profile, and the source mod only gets read.
/// </summary>
public static class ModDuplicate
{
    /// <summary>The first free id for a copy: <c>&lt;id&gt;-copy</c>, then <c>&lt;id&gt;-copy-2</c> and so on. An id
    /// is free when no folder has its name and no mod of the mods folder has it.</summary>
    public static string FreeId(string modsFolder, string id)
    {
        var ids = ModWorkspace.ModsIn(modsFolder).Select(m => m.Manifest.Id).ToHashSet(StringComparer.Ordinal);
        return ModWorkspace.FreeName(id + "-copy", name => ids.Contains(name) || Path.Exists(Path.Combine(modsFolder, name)));
    }

    /// <summary>The default display name of a copy.</summary>
    public static string DefaultName(string name) => $"{name} (copy)";

    /// <summary>Copies a mod folder to <c>&lt;modsFolder&gt;\&lt;newId&gt;</c>, and returns the new folder. A failure
    /// deletes the new folder, which holds only the copy.</summary>
    /// <exception cref="ArgumentException">The id or the name is not valid.</exception>
    /// <exception cref="IOException">The id is in use, the mods folder is in a game folder, the last save of the source
    /// is not complete, or a copy failed.</exception>
    /// <exception cref="InvalidDataException">The mod.json of the source has a comment, or its publish.json has a
    /// comment and a page link.</exception>
    public static string Run(string sourceFolder, string modsFolder, string newId, string name,
        IReadOnlyCollection<string> gameRoots)
    {
        sourceFolder = Path.GetFullPath(sourceFolder);
        modsFolder = Path.GetFullPath(modsFolder);
        if (!ModManifest.IsValidId(newId))
            throw new ArgumentException($"'{newId}': {ModManifest.IdRule}", nameof(newId));
        name = name.Trim();
        if (PublishDraft.DisplayNameProblem(name) is { } nameProblem)
            throw new ArgumentException(nameProblem, nameof(name));
        if (ModWrite.GameFolderOf(modsFolder, gameRoots) != null)
            throw new IOException(GameTexts.ModsFolderInGame);
        // A save that is not complete leaves a mix of old and new files, and the copy would keep the mix.
        if (SaveTransaction.HasJournal(sourceFolder))
            throw new IOException(PublishMessages.DuplicatePendingSave);
        var target = Path.Combine(modsFolder, newId);
        // Two mods with one id would share one publish history.
        if (Path.Exists(target) || ModWorkspace.ModsIn(modsFolder).Any(m => m.Manifest.Id == newId))
            throw new IOException(PublishMessages.IdInUse(newId));
        var manifest = JsonFile.Read(Path.Combine(sourceFolder, ModManifest.FileName));
        manifest.Root["id"] = newId;
        manifest.Root["name"] = name;
        var publish = PublishCopy(sourceFolder);

        Directory.CreateDirectory(target);
        try
        {
            // The new mod.json goes first, so no partial copy has the id of the source.
            AtomicFile.WriteAllBytes(Path.Combine(target, ModManifest.FileName), manifest.ToBytes());
            if (publish != null)
                AtomicFile.WriteAllBytes(Path.Combine(target, PublishSettings.FileName), publish);
            foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceFolder, file);
                if (relative.Equals(ModManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals(PublishSettings.FileName, StringComparison.OrdinalIgnoreCase) ||
                    PackageRules.SkipReason(file, relative) != null)
                    continue;
                var copy = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            return target;
        }
        catch
        {
            // The folder is new, so nothing else is in it. A failed delete keeps the error of the copy.
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The new folder stays. Its mod.json has the new id.
            }
            throw;
        }
    }

    /// <summary>The bytes of <c>publish.json</c> for the copy, without the page link of the source, or null when the
    /// source has no <c>publish.json</c>. The page belongs to the source mod.</summary>
    /// <exception cref="InvalidDataException">The file has a comment and a page link. OpenLancer cannot clear the link
    /// and keep the comment.</exception>
    private static byte[]? PublishCopy(string sourceFolder)
    {
        var path = Path.Combine(sourceFolder, PublishSettings.FileName);
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        if (PublishSettings.Load(sourceFolder).Settings.PageUrl.Length == 0)
            return bytes;
        JsonFile file;
        try
        {
            file = JsonFile.Parse(bytes);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
        file.Root["pageUrl"] = "";
        return file.ToBytes();
    }
}

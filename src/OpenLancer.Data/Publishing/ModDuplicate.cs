using System;
using System.Collections.Generic;
using System.IO;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>
/// Duplicate of a mod (decision D573 and section 12.2 of the M11c design): a copy of the folder without the save folder
/// of the editor and the files that a package skips, with a new id, a new display name and no page link. The copy is
/// not in a profile. The source mod only gets read.
/// </summary>
public static class ModDuplicate
{
    /// <summary>The first free id for a copy: <c>&lt;id&gt;-copy</c>, then <c>&lt;id&gt;-copy-2</c> and so
    /// on.</summary>
    public static string FreeId(string modsFolder, string id)
    {
        var copy = id + "-copy";
        if (!Path.Exists(Path.Combine(modsFolder, copy)))
            return copy;
        for (var i = 2; ; i++)
        {
            if (!Path.Exists(Path.Combine(modsFolder, $"{copy}-{i}")))
                return $"{copy}-{i}";
        }
    }

    /// <summary>The default display name of a copy.</summary>
    public static string DefaultName(string name) => $"{name} (copy)";

    /// <summary>Copies a mod folder to <c>&lt;modsFolder&gt;\&lt;newId&gt;</c>, and returns the new folder. A failure
    /// deletes the new folder, which holds only the copy.</summary>
    /// <exception cref="ArgumentException">The id or the name is not valid.</exception>
    /// <exception cref="IOException">The id is in use, the mods folder is in a game folder, or a copy failed.</exception>
    /// <exception cref="InvalidDataException">The mod.json of the source has a comment.</exception>
    public static string Run(string sourceFolder, string modsFolder, string newId, string name,
        IReadOnlyCollection<string> gameRoots)
    {
        sourceFolder = Path.GetFullPath(sourceFolder);
        modsFolder = Path.GetFullPath(modsFolder);
        if (!ModManifest.IsValidId(newId))
            throw new ArgumentException($"'{newId}': {ModManifest.IdRule}", nameof(newId));
        name = name.Trim();
        if (name.Length == 0 || name.Length > PublishLimits.NameLength || name.Contains('\n'))
            throw new ArgumentException(PublishMessages.DisplayNameEmpty, nameof(name));
        if (ModWrite.GameFolderOf(modsFolder, gameRoots) != null)
            throw new IOException(GameTexts.ModsFolderInGame);
        var target = Path.Combine(modsFolder, newId);
        if (Path.Exists(target))
            throw new IOException(PublishMessages.IdInUse(newId));
        var manifest = JsonFile.Read(Path.Combine(sourceFolder, ModManifest.FileName));

        Directory.CreateDirectory(target);
        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceFolder, file);
                if (PackageRules.SkipReason(file, relative) != null)
                    continue;
                var copy = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            manifest.Root["id"] = newId;
            manifest.Root["name"] = name;
            AtomicFile.WriteAllBytes(Path.Combine(target, ModManifest.FileName), manifest.ToBytes());
            var publish = Path.Combine(target, PublishSettings.FileName);
            if (File.Exists(publish))
                ClearPageLink(publish);
            return target;
        }
        catch
        {
            // The folder is new, so nothing else is in it.
            Directory.Delete(target, recursive: true);
            throw;
        }
    }

    /// <summary>Clears the page link of the copy, because the page belongs to the source mod.</summary>
    private static void ClearPageLink(string path)
    {
        JsonFile file;
        try
        {
            file = JsonFile.Read(path);
        }
        catch (InvalidDataException)
        {
            // OpenLancer cannot write a file with a comment. The publish page of the copy shows it read-only.
            return;
        }
        if (file.Root["pageUrl"] == null)
            return;
        file.Root["pageUrl"] = "";
        AtomicFile.WriteAllBytes(path, file.ToBytes());
    }
}

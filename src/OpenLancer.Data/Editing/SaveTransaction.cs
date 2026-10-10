using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>One file that a save writes or deletes.</summary>
/// <param name="Path">The full path of the file, in the mod folder.</param>
/// <param name="Content">The new bytes, or null to delete the file.</param>
internal sealed record FileChange(string Path, byte[]? Content);

/// <summary>
/// Writes the files of one save to a mod folder: all of them or none of them, also when the editor stops during the
/// save (review findings F1 and F2).
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The save checks that it can put each file in place. Then it stages each new file in the folder
/// <c>.olsave</c> of the mod. <c>Mod.Load</c> reads only <c>patches</c>, <c>files</c> and <c>strings.json</c>, so a
/// staged file never changes the mod.</item>
/// <item>The save writes the journal: for each file, the move or the delete, the hash of the file before the save,
/// and the hash of its new content. The journal is the commit point. Before it exists, the save has changed no file
/// of the mod.</item>
/// <item>The save moves each staged file into place and deletes the files to delete. A move in the mod folder
/// replaces the target in one step. Then the save deletes the journal.</item>
/// </list>
/// <para>When the editor stops after the commit point, <see cref="Recover"/> completes the moves and deletes at the
/// next open. When it stops before, <see cref="Recover"/> deletes the staged files.</para>
/// <para>A step changes its file only when the file still has its content from before the save. A file that changed
/// after that, for example a file that a backup put back after the editor stopped, stays, and the step reports a
/// conflict. A step is complete when the file has its new content, or is gone for a delete. So each step can run
/// again.</para>
/// </remarks>
internal static class SaveTransaction
{
    public const string FolderName = ".olsave";

    private const string JournalName = "journal";
    private const string JournalHeader = "olsave journal 1";
    private const string StagedSuffix = ".new";
    private const string WriteVerb = "write";
    private const string DeleteVerb = "delete";

    /// <summary>The hash of a file that does not exist, in the journal.</summary>
    private const string NoFile = "-";

    /// <summary>True when the mod has the journal of a save that is not complete.</summary>
    public static bool HasJournal(string modFolder) => File.Exists(Path.Combine(modFolder, FolderName, JournalName));

    /// <summary>Writes all changes.</summary>
    /// <param name="modFolder">The full path of the mod folder.</param>
    /// <param name="conflicts">Gets a warning for each file that changed during the save, and that the save kept.</param>
    /// <param name="afterSteps">Called with k after k moves and deletes, from k = 0 at the commit point to the number
    /// of changes before the journal goes. The fault tests use it.</param>
    /// <returns>
    /// Null when the save is complete. Else the error that stopped a move or a delete after the commit point. The save
    /// is then decided, and <see cref="Recover"/> completes it.
    /// </returns>
    /// <exception cref="IOException">The staging failed, or a file to replace cannot be read, is read-only, is a
    /// folder, or is in a folder that cannot be made. No file of the mod changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The staging failed. No file of the mod changed.</exception>
    /// <exception cref="ArgumentException">A file is not in the mod folder, or is in its <c>.olsave</c>
    /// folder.</exception>
    /// <exception cref="InvalidOperationException">The journal of an earlier save exists. Call <see cref="Recover"/>
    /// first.</exception>
    public static Exception? Commit(string modFolder, IReadOnlyList<FileChange> changes, List<Diagnostic> conflicts,
        Action<int>? afterSteps = null)
    {
        var folder = Path.Combine(modFolder, FolderName);
        if (HasJournal(modFolder))
            throw new InvalidOperationException("An earlier save of this mod is not complete. Recover it first.");
        var targets = changes
            .Select(c => Relative(modFolder, c.Path) ?? throw new ArgumentException(OutsideMessage(modFolder, c.Path)))
            .ToList();
        // A move or a delete that fails after the commit point leaves the save decided but not complete, so check
        // before it what can make it fail.
        foreach (var change in changes)
            CheckTarget(modFolder, change.Path);
        var steps = changes.Select((c, i) => new Step(
                c.Content == null ? null : $"{i}{StagedSuffix}",
                ContentHash.OfFile(c.Path),
                c.Content == null ? null : ContentHash.Of(c.Content),
                targets[i]))
            .ToList();

        DeleteStaged(folder);
        Directory.CreateDirectory(folder);
        for (var i = 0; i < changes.Count; i++)
        {
            if (changes[i].Content is { } content)
                AtomicFile.WriteDurable(Path.Combine(folder, steps[i].Staged!), content);
        }
        var journal = new StringBuilder(JournalHeader + "\n");
        foreach (var step in steps)
            journal.Append(step.ToLine()).Append('\n');
        var temp = Path.Combine(folder, JournalName + AtomicFile.TempSuffix);
        AtomicFile.WriteDurable(temp, Encoding.UTF8.GetBytes(journal.ToString()));
        File.Move(temp, Path.Combine(folder, JournalName));

        try
        {
            Complete(modFolder, steps, conflicts, afterSteps);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e;
        }
    }

    /// <summary>
    /// Completes a save that stopped after its commit point, and deletes the staged files of a save that stopped
    /// before it. Does nothing when the mod has no <c>.olsave</c> folder.
    /// </summary>
    /// <param name="conflicts">Gets a warning for each file that changed after the save stopped, and that the
    /// recovery kept.</param>
    /// <returns>True when it completed a save.</returns>
    /// <exception cref="InvalidDataException">The journal is not valid.</exception>
    /// <exception cref="IOException">A move or a delete failed. The journal stays.</exception>
    /// <exception cref="UnauthorizedAccessException">A move or a delete failed. The journal stays.</exception>
    public static bool Recover(string modFolder, List<Diagnostic> conflicts)
    {
        var folder = Path.Combine(modFolder, FolderName);
        var journal = Path.Combine(folder, JournalName);
        var completed = false;
        if (File.Exists(journal))
        {
            Complete(modFolder, Parse(modFolder, journal), conflicts, null);
            completed = true;
        }
        if (Directory.Exists(folder))
        {
            DeleteStaged(folder);
            DeleteIfEmpty(folder);
        }
        return completed;
    }

    /// <summary>Runs each step, then deletes the journal.</summary>
    private static void Complete(string modFolder, List<Step> steps, List<Diagnostic> conflicts, Action<int>? afterSteps)
    {
        var folder = Path.Combine(modFolder, FolderName);
        for (var i = 0; i < steps.Count; i++)
        {
            afterSteps?.Invoke(i);
            Run(modFolder, steps[i], conflicts);
        }
        afterSteps?.Invoke(steps.Count);
        File.Delete(Path.Combine(folder, JournalName));
        DeleteIfEmpty(folder);
    }

    /// <summary>Moves the staged file of a step into place, or deletes its target, when the target still has its
    /// content from before the save.</summary>
    private static void Run(string modFolder, Step step, List<Diagnostic> conflicts)
    {
        var target = Path.Combine(modFolder, step.Target);
        var current = ContentHash.OfFile(target);
        if (current == step.After)
        {
            // The step is complete. A run that stopped after a delete can leave the empty folders.
            if (step.Staged == null)
                DeleteEmptyFolders(modFolder, Path.GetDirectoryName(target)!);
            return;
        }
        var source = step.Staged == null ? null : Path.Combine(modFolder, FolderName, step.Staged);
        if (current != step.Before)
        {
            conflicts.Add(Diagnostic.Warning(target, "This file changed during a save of the editor, or after the save " +
                                                     $"stopped. The editor kept it and did not {(source == null ? "delete it" : "write the saved version")}."));
            return;
        }
        if (source == null)
        {
            File.Delete(target);
            DeleteEmptyFolders(modFolder, Path.GetDirectoryName(target)!);
            return;
        }
        if (!File.Exists(source))
        {
            conflicts.Add(Diagnostic.Warning(target, $"The saved version of this file is not in the {FolderName} folder " +
                                                     "any more, so the editor could not write it. The file stays as it was before the save."));
            return;
        }
        if (PathBlocker(modFolder, target) is { } blocker)
        {
            conflicts.Add(Diagnostic.Warning(target, $"{blocker} The editor did not write the saved version."));
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite: true);
    }

    /// <exception cref="IOException">A move or a delete of the target would fail.</exception>
    private static void CheckTarget(string modFolder, string target)
    {
        if (PathBlocker(modFolder, target) is { } blocker)
            throw new IOException(blocker);
        if (File.Exists(target) && new FileInfo(target).IsReadOnly)
            throw new IOException($"{target} is read-only. The editor does not change a read-only file.");
    }

    /// <summary>Why a file cannot go to the target, or null: the target is a folder, or a folder of its path is a
    /// file.</summary>
    private static string? PathBlocker(string modFolder, string target)
    {
        if (Directory.Exists(target))
            return $"{target} is a folder, so the editor cannot put a file in its place.";
        var root = Full(modFolder);
        for (var dir = Path.GetDirectoryName(Full(target)); dir != null && Path.GetRelativePath(root, dir) != "."; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(dir))
                return $"{dir} is a file, so the editor cannot make the folder of {target}.";
        }
        return null;
    }

    private static List<Step> Parse(string modFolder, string journal)
    {
        var lines = File.ReadAllLines(journal, Encoding.UTF8);
        if (lines.Length == 0 || lines[0] != JournalHeader)
            throw new InvalidDataException($"{journal}: this is not a save journal of this editor version.");
        var steps = new List<Step>();
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var step = Step.Parse(line) ?? throw new InvalidDataException($"{journal}: the line '{line}' is not valid.");
            if (Relative(modFolder, Path.Combine(modFolder, step.Target)) == null)
                throw new InvalidDataException($"{journal}: {OutsideMessage(modFolder, step.Target)}");
            steps.Add(step);
        }
        return steps;
    }

    /// <summary>
    /// The path of a target relative to the mod folder, or null when the target is not a file path in the mod folder
    /// or is in the <c>.olsave</c> folder.
    /// </summary>
    private static string? Relative(string modFolder, string target)
    {
        if (!PathRules.IsInside(target, modFolder) || PathRules.IsInside(target, Path.Combine(modFolder, FolderName)) ||
            Full(target) == Full(modFolder))
            return null;
        return Path.GetRelativePath(modFolder, target);
    }

    private static string OutsideMessage(string modFolder, string target) =>
        $"A save writes only files in the mod folder {modFolder}, not {target}.";

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Deletes the staged files and a temporary journal. They are from a save that stopped before its
    /// commit point, or that is complete.</summary>
    private static void DeleteStaged(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, "*" + StagedSuffix).ToList())
            File.Delete(file);
        var temp = Path.Combine(folder, JournalName + AtomicFile.TempSuffix);
        if (File.Exists(temp))
            File.Delete(temp);
    }

    /// <summary>Deletes the empty folders from <paramref name="folder"/> up to the first folder in the mod folder,
    /// for example <c>patches</c>. That folder stays.</summary>
    private static void DeleteEmptyFolders(string modFolder, string folder)
    {
        var root = Full(modFolder);
        var dir = Full(folder);
        while (dir != root && PathRules.IsInside(dir, root) && Path.GetDirectoryName(dir) is { } parent &&
               Full(parent) != root)
        {
            if (Directory.Exists(dir))
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    return;
                Directory.Delete(dir);
            }
            dir = Full(parent);
        }
    }

    private static void DeleteIfEmpty(string folder)
    {
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }

    /// <summary>One move or delete of the journal.</summary>
    /// <param name="Staged">The name of the staged file in the <c>.olsave</c> folder, or null to delete the target.</param>
    /// <param name="Before">The hash of the target before the save, or null when it did not exist.</param>
    /// <param name="After">The hash of the staged file, or null for a delete.</param>
    /// <param name="Target">The path of the target, relative to the mod folder.</param>
    private sealed record Step(string? Staged, string? Before, string? After, string Target)
    {
        /// <summary>
        /// The journal line: <c>write &lt;staged&gt; &lt;before&gt; &lt;after&gt; &lt;target&gt;</c> or
        /// <c>delete &lt;before&gt; &lt;target&gt;</c>. A hash is <c>-</c> for no file. The target is last, because it
        /// can have spaces.
        /// </summary>
        public string ToLine() => Staged == null
            ? $"{DeleteVerb} {Before ?? NoFile} {Target}"
            : $"{WriteVerb} {Staged} {Before ?? NoFile} {After} {Target}";

        /// <summary>The step of a journal line, or null when the line is not valid.</summary>
        public static Step? Parse(string line)
        {
            var parts = line.Split(' ', 2);
            if (parts is [DeleteVerb, var delete] && delete.Split(' ', 2) is [var before, var target] &&
                TryHash(before, out var beforeHash) && target.Length > 0)
                return new Step(null, beforeHash, null, target);
            if (parts is [WriteVerb, var write] && write.Split(' ', 4) is [var staged, var b, var after, var t] &&
                IsStagedName(staged) && TryHash(b, out var writeBefore) && TryHash(after, out var afterHash) &&
                afterHash != null && t.Length > 0)
                return new Step(staged, writeBefore, afterHash, t);
            return null;
        }

        /// <summary>True for the name of a staged file: a number and <see cref="StagedSuffix"/>.</summary>
        private static bool IsStagedName(string name) =>
            name.EndsWith(StagedSuffix, StringComparison.Ordinal) && name.Length > StagedSuffix.Length &&
            name[..^StagedSuffix.Length].All(char.IsAsciiDigit);

        /// <summary>Reads a hash of <see cref="ContentHash"/>, or <see cref="NoFile"/> as null.</summary>
        private static bool TryHash(string text, out string? hash)
        {
            hash = text == NoFile ? null : text;
            return hash == null || hash.Length == 64 && hash.All(char.IsAsciiHexDigitLower);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The lock of a mod folder: one editor session at a time edits a mod. Without it, a second editor on the same mod
/// would delete the staged files of a save of the first editor, or its recovery file.
/// </summary>
/// <remarks>
/// <para>The lock is an open handle on <c>.olsave\lock</c> that no other handle can share. The holder writes
/// <c>.olsave\lock.json</c> with its process id, its machine name and the time it took the lock, so that a second
/// editor can tell who has the mod open. The handle is the lock, not the files: a crash closes the handle, so the files
/// that a crash leaves block nothing.</para>
/// <para><see cref="SaveTransaction.Recover"/> deletes only staged files and the temporary journal, so it never
/// deletes the lock files. The lock files keep the <c>.olsave</c> folder while the lock is held.</para>
/// </remarks>
internal sealed class ModLock : IDisposable
{
    public const string FileName = "lock";
    public const string InfoName = "lock.json";

    // The Windows errors of a file that another handle holds.
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly string folder;
    private FileStream? handle;

    private ModLock(string folder, FileStream handle)
    {
        this.folder = folder;
        this.handle = handle;
    }

    /// <summary>Takes the lock of a mod, or returns null with the reason when the editor cannot take it.</summary>
    public static ModLock? TryTake(Mod mod, out string? reason)
    {
        var folder = Path.Combine(mod.Folder, SaveTransaction.FolderName);
        FileStream handle;
        try
        {
            Directory.CreateDirectory(folder);
            handle = new FileStream(Path.Combine(folder, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException e) when (IsHeld(e))
        {
            reason = HeldReason(mod.Id, folder);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reason = $"The editor cannot lock mod '{mod.Id}': {e.Message} Correct the problem, then open the " +
                     "profile again.";
            return null;
        }

        try
        {
            AtomicFile.WriteAllBytes(Path.Combine(folder, InfoName), JsonSerializer.SerializeToUtf8Bytes(
                new Holder(Environment.ProcessId, Environment.MachineName, DateTime.UtcNow), JsonSerializerOptions.Web));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The handle holds the lock. Without the file, a second editor cannot name the holder.
        }

        reason = null;
        return new ModLock(folder, handle);
    }

    /// <summary>
    /// Releases the lock: closes the handle, deletes the lock files, and deletes the <c>.olsave</c> folder when it is
    /// empty.
    /// </summary>
    public void Dispose()
    {
        if (handle == null)
            return;
        // The file goes while the handle holds the lock, so a release never deletes the file of the next holder.
        TryDelete(Path.Combine(folder, InfoName));
        Abandon();
        // Another editor can take the lock between the close and the delete. Then the delete fails, and its lock stays.
        TryDelete(Path.Combine(folder, FileName));
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another editor made a file in the folder after the check.
        }
    }

    /// <summary>Closes the handle and deletes no file, as a crash does. The tests use it for a crash.</summary>
    internal void Abandon()
    {
        handle?.Dispose();
        handle = null;
    }

    /// <summary>True when the open failed because another handle holds the file.</summary>
    private static bool IsHeld(IOException e) =>
        e.GetType() == typeof(IOException) &&
        (!OperatingSystem.IsWindows() || (e.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation);

    /// <summary>The reason of a session that cannot take the lock, with the holder that its file names.</summary>
    private static string HeldReason(string modId, string folder)
    {
        Holder? holder = null;
        try
        {
            holder = JsonSerializer.Deserialize<Holder>(File.ReadAllBytes(Path.Combine(folder, InfoName)),
                JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // The holder did not write its file yet, or a crash left a file that is not complete.
        }

        var who = holder == null
            ? ""
            : $" (process {holder.ProcessId}{On(holder.Machine)}, since {holder.Started.ToLocalTime():yyyy-MM-dd HH:mm})";
        return $"Another editor has mod '{modId}' open{who}. Close it, then open the profile again.";

        static string On(string? machine) =>
            machine == null || machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                ? ""
                : $" on {machine}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another editor holds the file.
        }
    }

    /// <summary>The content of <c>lock.json</c>.</summary>
    /// <param name="Started">The time in UTC at which the holder took the lock.</param>
    private sealed record Holder(int ProcessId, string Machine, DateTime Started);
}

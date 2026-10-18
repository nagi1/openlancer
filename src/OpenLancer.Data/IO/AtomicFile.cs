using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenLancer.Data.IO;

/// <summary>
/// Writes a file through a temporary file next to it, then moves the temporary file into place. A reader sees the
/// old file or the new file, never a part of the new file. A write never goes through a hard link.
/// </summary>
/// <remarks>
/// <para>The temporary file is <c>&lt;file&gt;.oltmp</c>. A process that stops during a write can leave it. The next
/// write to the same file deletes it.</para>
/// <para><see cref="WriteAllBytes"/> flushes the temporary file to the disk before the move. So after a power loss,
/// the file is also the old file or the complete new file. A <see cref="Replace"/> write gets this only when it
/// flushes its file.</para>
/// </remarks>
internal static partial class AtomicFile
{
    public const string TempSuffix = ".oltmp";

    private const uint DeleteAccess = 0x00010000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileRenameInfoEx = 22;
    private const uint RenameReplaceIfExists = 0x1;
    private const uint RenamePosixSemantics = 0x2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    /// <summary>A Windows call takes a path of this length or more only with <see cref="LongPrefix"/>.</summary>
    private const int MaxShortPath = 260;
    private const string LongPrefix = @"\\?\";
    private const string LongUncPrefix = @"\\?\UNC\";
    private const string DevicePrefix = @"\\.\";
    private const string UncPrefix = @"\\";

    /// <param name="write">Writes the new content to the temporary file at the path that it gets.</param>
    public static void Replace(string destination, Action<string> write)
    {
        var temp = destination + TempSuffix;
        if (File.Exists(temp))
            File.Delete(temp);
        write(temp);
        MoveOver(temp, destination);
    }

    public static void WriteAllBytes(string destination, byte[] bytes) =>
        Replace(destination, temp => WriteDurable(temp, bytes));

    /// <summary>Writes a file and flushes it to the disk, so that it is complete before the next step uses it.</summary>
    public static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="source"/> in one rename. A reader of the target sees
    /// the old file or the new file, never no file. A reader that holds the target open with delete sharing (see
    /// <see cref="SharedFile"/>) does not stop the rename, and it keeps the old bytes (decision D51).
    /// </summary>
    /// <remarks>
    /// <para>The first try is <see cref="File.Move(string, string, bool)"/> with overwrite, as before D51. It also moves
    /// a file to another volume, for example into a folder of the mod that is a link to another drive. That move is a
    /// copy, so a reader can see a part of the new file. Other systems rename with POSIX semantics, so this move is all
    /// that they need.</para>
    /// <para>On Windows, the move fails with access denied while another process holds the target open, also with
    /// delete sharing. Then a rename with POSIX semantics replaces the file. It needs NTFS on Windows 10 1709 or
    /// later, and one volume. When the file system does not support it, the error of the move stays.</para>
    /// </remarks>
    /// <exception cref="IOException">The move failed, for example because a reader holds the target open without
    /// delete sharing.</exception>
    /// <exception cref="UnauthorizedAccessException">The target is read-only or a folder, or access is
    /// denied.</exception>
    public static void MoveOver(string source, string target)
    {
        try
        {
            File.Move(source, target, overwrite: true);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            if (!PosixRename(Path.GetFullPath(source), Path.GetFullPath(target)))
                throw;
        }
    }

    /// <summary>Renames the source over the target with POSIX semantics.</summary>
    /// <returns>False when the file system does not support the rename. Nothing changed then.</returns>
    private static bool PosixRename(string source, string target)
    {
        // The rename needs a handle with delete access. The handle opens a link itself, not the file of the link, as a
        // move does.
        using var handle = CreateFile(Long(source), DeleteAccess, ShareAll, IntPtr.Zero, OpenExisting,
            OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            throw Failure(Marshal.GetLastPInvokeError(), source, target);
        if (SetFileInformationByHandle(handle, FileRenameInfoEx,
                RenameInfo(Long(target), RenameReplaceIfExists | RenamePosixSemantics, out var size), size))
            return true;
        var error = Marshal.GetLastPInvokeError();
        return error is ErrorNotSupported or ErrorInvalidParameter ? false : throw Failure(error, source, target);
    }

    /// <summary>
    /// The <c>FILE_RENAME_INFO</c> structure for the target: the flags, a null root folder handle, the length of the
    /// name in bytes, and the name with a null at its end. The play copies use it too (see
    /// <see cref="PlayCopies.HardLinks"/>).
    /// </summary>
    internal static byte[] RenameInfo(string target, uint flags, out int size)
    {
        var name = Encoding.Unicode.GetBytes(target);
        // The flags are a 32-bit field. The handle that follows has the alignment of a pointer.
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        size = nameOffset + name.Length + sizeof(char);
        var info = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(info, flags);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(lengthOffset), (uint)name.Length);
        name.CopyTo(info, nameOffset);
        return info;
    }

    /// <summary>A full path with the prefix that a Windows call needs for a long path, as .NET adds it to its own
    /// calls. A shorter path stays as it is.</summary>
    internal static string Long(string fullPath)
    {
        if (fullPath.Length < MaxShortPath || fullPath.StartsWith(LongPrefix, StringComparison.Ordinal) ||
            fullPath.StartsWith(DevicePrefix, StringComparison.Ordinal))
            return fullPath;
        return fullPath.StartsWith(UncPrefix, StringComparison.Ordinal)
            ? LongUncPrefix + fullPath[UncPrefix.Length..]
            : LongPrefix + fullPath;
    }

    private static Exception Failure(int error, string source, string target)
    {
        var message = $"Cannot move {source} to {target}: {new Win32Exception(error).Message}";
        return error == ErrorAccessDenied
            ? new UnauthorizedAccessException(message)
            : new IOException(message, unchecked((int)0x80070000) | error);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte[] info, int size);
}

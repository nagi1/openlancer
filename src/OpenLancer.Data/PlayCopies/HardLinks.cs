using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.PlayCopies;

/// <summary>
/// The identity and the stamps of a file record, read through a handle (milestone M11). All names of a hard-linked file
/// share one file record, so they give the same stamp.
/// </summary>
/// <param name="Volume">The serial number of the volume.</param>
/// <param name="Index">The file index on the volume. With the volume, it identifies the file record.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="WriteTime">The last write time, in UTC.</param>
/// <param name="CreationTime">The creation time, in UTC.</param>
/// <param name="Links">The number of names of the file record.</param>
public readonly record struct FileStamp(uint Volume, ulong Index, long Size, DateTime WriteTime, DateTime CreationTime,
    FileAttributes Attributes, uint Links)
{
    /// <summary>True when the two stamps come from the same file record.</summary>
    public bool SameFile(FileStamp other) => Volume == other.Volume && Index == other.Index;
}

/// <summary>
/// The only code that changes a name in a play copy (section 8.2 of the M11 design). Many files of a play copy are hard
/// links to the files of the user's game. A write, an attribute change or a time change through such a name changes the
/// game file too. So this class never opens a name of a play copy for write. It only renames a new file over a name,
/// deletes a name, and makes a link.
/// </summary>
/// <remarks>
/// <para>Each replace renames a new temporary file over the name with a handle and the flags
/// <c>REPLACE_IF_EXISTS</c>, <c>POSIX_SEMANTICS</c> and <c>IGNORE_READONLY_ATTRIBUTE</c>. A rename replaces the name
/// only, so the other names of the old file keep their bytes and attributes. It never falls back to a move or a copy.
/// A move to another volume and a copy onto a name write into the file of that name. On a file system without that
/// rename, for example FAT, it uses the older rename by handle, which also replaces the name only.</para>
/// <para>Each delete removes a name with a handle and the flags <c>DELETE</c>, <c>POSIX_SEMANTICS</c> and
/// <c>IGNORE_READONLY_ATTRIBUTE</c>. So a read-only name goes away, and the attribute of the file does not change. The
/// handle opens the name itself, also when it is a link to a folder, so a delete never follows a link.</para>
/// <para>The tests replace the two calls of the system (<see cref="CreateLink"/> and <see cref="Rename"/>) to make them
/// fail.</para>
/// </remarks>
internal partial class HardLinks
{
    private const uint ReadAttributesAccess = 0x80;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteAccess = 0x40000000;
    private const uint ShareAll = 0x7;
    private const uint CreateNewDisposition = 1;
    private const uint OpenExisting = 3;
    private const uint ReadOnlyAttribute = 0x1;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;
    private const int FileRenameInfoExClass = 22;
    private const uint RenameReplaceIfExists = 0x1;
    private const uint RenamePosixSemantics = 0x2;
    private const uint RenameIgnoreReadOnly = 0x40;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadOnly = 0x10;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    /// <summary>The calls of the system.</summary>
    public static HardLinks Instance { get; } = new();

    /// <summary>The stamp of a name, through a handle with attribute access only. The handle opens the name itself, not
    /// the target of a link. The values come from the file record, not from a folder listing, which can be old for a
    /// hard-linked file.</summary>
    /// <exception cref="FileNotFoundException">The name does not exist.</exception>
    /// <exception cref="IOException">The handle did not open.</exception>
    public FileStamp Stamp(string path)
    {
        var full = Path.GetFullPath(path);
        using var handle = Open(full, ReadAttributesAccess);
        if (!GetFileInformationByHandle(handle, out var info))
            throw Failure(Marshal.GetLastPInvokeError(), $"Cannot read the stamp of {full}");
        return new FileStamp(info.VolumeSerialNumber, (ulong)info.FileIndexHigh << 32 | info.FileIndexLow,
            (long)info.FileSizeHigh << 32 | info.FileSizeLow, Time(info.LastWriteLow, info.LastWriteHigh),
            Time(info.CreationLow, info.CreationHigh), (FileAttributes)info.FileAttributes, info.NumberOfLinks);
    }

    /// <summary>The stamp of a name, or null when it does not exist.</summary>
    public FileStamp? TryStamp(string path)
    {
        try
        {
            return Stamp(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Makes <paramref name="target"/> a hard link to <paramref name="source"/>. The link goes to a temporary
    /// name first, then <see cref="Replace"/> puts it in place. So an existing target name changes in one rename.</summary>
    /// <exception cref="IOException">The link or the rename failed. The target did not change.</exception>
    public void Link(string source, string target)
    {
        var temp = TempName(target);
        CreateLink(Path.GetFullPath(source), temp);
        Replace(temp, target);
    }

    /// <summary>
    /// Renames a new file over a name. The temporary file must be a new file that the caller made in the folder of the
    /// target. The old file of the name keeps its bytes, its attributes and its other names.
    /// </summary>
    /// <exception cref="IOException">The rename failed. The target did not change, and the temporary file is
    /// gone.</exception>
    public void Replace(string temp, string target)
    {
        try
        {
            Rename(Path.GetFullPath(temp), Path.GetFullPath(target));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Removes a name: a file, an empty folder or a link. A read-only name goes away, and the file keeps its
    /// attribute. The other names of the file keep it. A link to a folder goes away, and the folder does not
    /// change.</summary>
    /// <exception cref="IOException">The name did not go away, for example because a folder is not empty.</exception>
    public void Delete(string path)
    {
        var full = Path.GetFullPath(path);
        using var handle = Open(full, DeleteAccess);
        var flags = new byte[sizeof(uint)];
        BitConverter.TryWriteBytes(flags, DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly);
        if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, flags, flags.Length))
            return;
        var error = Marshal.GetLastPInvokeError();
        if (error is ErrorNotSupported or ErrorInvalidParameter)
        {
            // A file system without the extended delete, for example FAT, has no hard links. The older delete by handle
            // removes the name too.
            if (SetFileInformationByHandle(handle, FileDispositionInfoClass, [1], 1))
                return;
            error = Marshal.GetLastPInvokeError();
        }
        throw Failure(error, $"Cannot delete {full}");
    }

    /// <summary>Copies a file into a new temporary file next to the target, then puts it in place with
    /// <see cref="Replace"/>. The copy is a new file, so it never gets the read-only attribute of the source.</summary>
    public void CopyNew(string source, string target)
    {
        var temp = TempName(target);
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        Replace(temp, target);
    }

    /// <summary>Writes bytes into a new temporary file next to the target, then puts it in place with
    /// <see cref="Replace"/>.</summary>
    public void WriteNew(string target, byte[] bytes)
    {
        var temp = TempName(target);
        try
        {
            using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        Replace(temp, target);
    }

    /// <summary>Makes a new file with the read-only attribute and one byte, for the probe of a play copy folder. The
    /// file gets its attribute when the system makes it, so no code sets an attribute in a play copy.</summary>
    /// <exception cref="IOException">The file exists, or the system did not make it.</exception>
    public void CreateReadOnly(string path)
    {
        var full = Path.GetFullPath(path);
        using var handle = CreateFile(AtomicFile.Long(full), WriteAccess, 0, IntPtr.Zero, CreateNewDisposition,
            ReadOnlyAttribute, IntPtr.Zero);
        if (handle.IsInvalid)
            throw Failure(Marshal.GetLastPInvokeError(), $"Cannot make {full}");
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.WriteByte(1);
    }

    /// <summary>Makes a hard link at a new name. The tests replace it to make a link fail.</summary>
    /// <exception cref="IOException">The system did not make the link, for example because the names are on two
    /// volumes.</exception>
    protected virtual void CreateLink(string source, string newName)
    {
        if (!CreateHardLink(AtomicFile.Long(newName), AtomicFile.Long(source), IntPtr.Zero))
            throw Failure(Marshal.GetLastPInvokeError(), $"Cannot make a hard link {newName} to {source}");
    }

    /// <summary>Renames a file over a name with a handle. The tests replace it to make a rename fail.</summary>
    /// <exception cref="IOException">The rename failed.</exception>
    protected virtual void Rename(string temp, string target)
    {
        using var handle = Open(temp, DeleteAccess);
        var info = AtomicFile.RenameInfo(AtomicFile.Long(target),
            RenameReplaceIfExists | RenamePosixSemantics | RenameIgnoreReadOnly, out var size);
        if (SetFileInformationByHandle(handle, FileRenameInfoExClass, info, size))
            return;
        var error = Marshal.GetLastPInvokeError();
        if (error is not (ErrorNotSupported or ErrorInvalidParameter))
            throw Failure(error, $"Cannot rename {temp} to {target}");
        // A file system without the extended rename, for example FAT, has no hard links. The older rename by handle
        // replaces the name too. It is never a copy.
        info = AtomicFile.RenameInfo(AtomicFile.Long(target), RenameReplaceIfExists, out size);
        if (!SetFileInformationByHandle(handle, FileRenameInfoClass, info, size))
            throw Failure(Marshal.GetLastPInvokeError(), $"Cannot rename {temp} to {target}");
    }

    /// <summary>A new name for a temporary file next to the target, which no other file has.</summary>
    internal static string TempName(string target) =>
        $"{Path.GetFullPath(target)}.{Guid.NewGuid():N}{AtomicFile.TempSuffix}";

    /// <summary>Deletes a temporary name after a failure. A second failure keeps the first error.</summary>
    private void TryDelete(string path)
    {
        try
        {
            if (TryStamp(path) != null)
                Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The first error tells the cause. The temporary name stays until the play copy goes.
        }
    }

    /// <summary>Opens a handle of the name itself, also of a folder or a link.</summary>
    private static SafeFileHandle Open(string full, uint access)
    {
        var handle = CreateFile(AtomicFile.Long(full), access, ShareAll, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure(error, $"Cannot open {full}");
        }
        return handle;
    }

    private static DateTime Time(uint low, uint high) => DateTime.FromFileTimeUtc((long)high << 32 | low);

    private static IOException Failure(int error, string what)
    {
        var message = $"{what}: {new Win32Exception(error).Message}";
        return error is ErrorFileNotFound or ErrorPathNotFound
            ? new FileNotFoundException(message)
            : new IOException(message, unchecked((int)0x80070000) | error);
    }

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>. Each time is two 32-bit parts, low first.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint AccessLow;
        public uint AccessHigh;
        public uint LastWriteLow;
        public uint LastWriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newName, string existing, IntPtr security);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte[] info, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}

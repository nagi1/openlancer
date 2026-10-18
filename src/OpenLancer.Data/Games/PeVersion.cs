using System;
using System.Buffers.Binary;
using System.IO;
using LibreLancer.Data.Dll;

namespace OpenLancer.Data.Games;

/// <summary>
/// Reads the binary file version of a DLL or an exe: the <c>VS_FIXEDFILEINFO</c> structure of its version resource. The
/// engine reads the bytes of the PE file. Nothing loads the file as code. The string <c>FileVersion</c> is not used,
/// because the retail code files have "1, 0, 0, 0" there.
/// </summary>
internal static class PeVersion
{
    /// <summary>The first value of <c>VS_FIXEDFILEINFO</c>.</summary>
    private const uint Signature = 0xFEEF04BD;

    /// <summary>The binary file version, or null when the file cannot be read, has no version block, or the reader
    /// cannot parse it. The file opens for read only, and other programs can keep it open.</summary>
    public static Version? Of(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FromVersionBlock(ResourceDll.FromStream(stream, path).VersionInfo?.Data);
        }
        catch (Exception)
        {
            // The PE reader can throw many types for a file that is not a PE file or that has bad bytes. Each one means
            // that the file has no version that OpenLancer can read.
            return null;
        }
    }

    /// <summary>The file version of a <c>VS_VERSIONINFO</c> block, or null when the block has no
    /// <c>VS_FIXEDFILEINFO</c>.</summary>
    public static Version? FromVersionBlock(byte[]? block)
    {
        if (block == null)
            return null;
        // The structure follows the header and the key "VS_VERSION_INFO" on a 4-byte boundary.
        for (var offset = 0; offset + 16 <= block.Length && offset <= 256; offset += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset)) != Signature)
                continue;
            var high = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset + 8));
            var low = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset + 12));
            return new Version((int)(high >> 16), (int)(high & 0xFFFF), (int)(low >> 16), (int)(low & 0xFFFF));
        }
        return null;
    }
}

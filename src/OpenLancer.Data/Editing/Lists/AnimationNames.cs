using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>
/// The script names of an animation file (<c>.anm</c>): the names of the nodes under <c>\Animation\Script</c>
/// (section 8.6 of the M21 design). The reader reads only the header, the node block and the string block of the UTF
/// file. It never reads the data block, which holds the keys: the 11 retail files hold 101 MB.
/// </summary>
/// <remarks>
/// The layout of the UTF header and of a node is public. The MIT code of Librelancer (<c>Utf\UtfFile.cs</c> and
/// <c>Node.cs</c>) shows it: after the tag "UTF " come the version 257, the offset and the size of the node block, a
/// pad, the size of a node, the offset, the allocated size and the used size of the string block, and the offset of the
/// data block. A node starts with the offset of its next peer, the offset of its name, its flags, a pad and the offset
/// of its first child. Offsets of nodes are in the node block. A name ends with a zero byte.
/// </remarks>
internal static class AnimationNames
{
    private const int Version = 257;
    private const int HeaderSize = 4 + 9 * 4;
    private const int NodeHeader = 5 * 4;
    private const int Intermediate = 0x10;

    /// <summary>The names under <c>\Animation\Script</c>, in file order. Empty when the file has no such node.</summary>
    /// <param name="path">The path of the file, for messages.</param>
    /// <exception cref="InvalidDataException">The file is not a UTF file, or a block or a node is out of range.</exception>
    public static IReadOnlyList<string> Scripts(Stream stream, string path)
    {
        if (stream.Length < HeaderSize)
            throw new InvalidDataException($"{path} is too short for a UTF file.");
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("UTF "u8) || Int(header, 4) != Version)
            throw new InvalidDataException($"{path} is not a UTF file of version {Version}.");
        var nodes = Block(stream, path, "node", Int(header, 8), Int(header, 12));
        var strings = Block(stream, path, "string", Int(header, 24), Int(header, 32));

        // The root node is at offset 0. Its children are the top nodes of the file.
        var animation = Child(nodes, strings, path, 0, "Animation");
        var script = animation < 0 ? -1 : Child(nodes, strings, path, animation, "Script");
        var result = new List<string>();
        if (script < 0)
            return result;
        foreach (var child in Children(nodes, path, script))
            result.Add(Name(nodes, strings, path, child));
        return result;
    }

    /// <summary>The offset of the child with a name, without letter case, or -1.</summary>
    private static int Child(byte[] nodes, byte[] strings, string path, int parent, string name)
    {
        foreach (var child in Children(nodes, path, parent))
        {
            if (string.Equals(Name(nodes, strings, path, child), name, StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return -1;
    }

    /// <summary>The offsets of the children of an intermediate node, in file order.</summary>
    private static IEnumerable<int> Children(byte[] nodes, string path, int parent)
    {
        Check(nodes, path, parent);
        if ((Int(nodes, parent + 8) & Intermediate) == 0)
            yield break;
        var next = Int(nodes, parent + 16);
        // A node block of n bytes holds fewer than n nodes, so a longer peer chain is a loop.
        for (var count = 0; next > 0; count++)
        {
            if (count > nodes.Length)
                throw new InvalidDataException($"{path}: the peer chain of a node has a loop.");
            Check(nodes, path, next);
            yield return next;
            next = Int(nodes, next);
        }
    }

    private static string Name(byte[] nodes, byte[] strings, string path, int node)
    {
        var offset = Int(nodes, node + 4);
        if (offset < 0 || offset >= strings.Length)
            throw new InvalidDataException($"{path}: the name of a node is out of the string block.");
        var length = strings.AsSpan(offset).IndexOf((byte)0);
        return Encoding.ASCII.GetString(strings, offset, length < 0 ? strings.Length - offset : length);
    }

    private static void Check(byte[] nodes, string path, int node)
    {
        if (node < 0 || node + NodeHeader > nodes.Length)
            throw new InvalidDataException($"{path}: a node is out of the node block.");
    }

    private static byte[] Block(Stream stream, string path, string name, int offset, int size)
    {
        if (offset < 0 || size < 0 || (long)offset + size > stream.Length)
            throw new InvalidDataException($"{path}: the {name} block is out of range.");
        var block = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(block);
        return block;
    }

    private static int Int(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
}

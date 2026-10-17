using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Editing.Lists;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The script names of an animation file (section 8.6 of the M21 design). The test builds a small UTF file itself, so no
/// retail bytes go into the repo.
/// </summary>
public sealed class AnimationNamesTests
{
    private const int NodeSize = 44;
    private const int Intermediate = 0x10;
    private const int Leaf = 0x80;

    [Fact]
    public void TheReaderGivesTheNamesUnderAnimationScript()
    {
        var (bytes, dataOffset) = Build();
        using var stream = new GuardStream(bytes, dataOffset);

        Assert.Equal(["Sc_Walk", "Sc_Run"], AnimationNames.Scripts(stream, "test.anm"));
    }

    [Fact]
    public void AFileWithoutScriptsGivesNoName()
    {
        var (bytes, dataOffset) = Build(scripts: false);
        using var stream = new GuardStream(bytes, dataOffset);

        Assert.Empty(AnimationNames.Scripts(stream, "test.anm"));
    }

    [Fact]
    public void AFileThatIsNotUtfIsAnError()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("[Animations]\nanim = test.anm\n"));
        Assert.Throws<InvalidDataException>(() => AnimationNames.Scripts(stream, "test.anm"));
        var (bytes, _) = Build();
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 8);
        Assert.Throws<InvalidDataException>(() => AnimationNames.Scripts(new MemoryStream(bytes), "test.anm"));
    }

    /// <summary>
    /// A UTF file: the root, "Animation" with the child "Script" and a peer "Other", and two scripts under "Script"
    /// with letter case of their own. A data block of keys follows the string block.
    /// </summary>
    private static (byte[] Bytes, int DataOffset) Build(bool scripts = true)
    {
        var names = new List<string> { "\\", "animation", "SCRIPT", "Other", "Sc_Walk", "Sc_Run" };
        var nameOffsets = new List<int>();
        var strings = new MemoryStream();
        foreach (var name in names)
        {
            nameOffsets.Add((int)strings.Length);
            strings.Write(Encoding.ASCII.GetBytes(name + "\0"));
        }
        // (name, flags, peer, child) by node index.
        var nodes = new (int Name, int Flags, int Peer, int Child)[]
        {
            (0, Intermediate, 0, 1),
            (1, Intermediate, 3, scripts ? 2 : 0),
            (2, Intermediate, 0, 4),
            (3, Leaf, 0, 0),
            (4, Intermediate, 5, 0),
            (5, Leaf, 0, 0),
        };
        var nodeBlock = new MemoryStream();
        foreach (var (name, flags, peer, child) in nodes)
        {
            foreach (var value in new[] { peer * NodeSize, nameOffsets[name], flags, 0, child * NodeSize, 0, 0, 0, 0, 0, 0 })
                nodeBlock.Write(BitConverter.GetBytes(value));
        }
        const int headerSize = 4 + 9 * 4;
        var nodeOffset = headerSize;
        var stringOffset = nodeOffset + (int)nodeBlock.Length;
        var dataOffset = stringOffset + (int)strings.Length;
        var file = new MemoryStream();
        file.Write("UTF "u8);
        foreach (var value in new[] { 257, nodeOffset, (int)nodeBlock.Length, 0, NodeSize, stringOffset,
                     (int)strings.Length, (int)strings.Length, dataOffset })
            file.Write(BitConverter.GetBytes(value));
        file.Write(nodeBlock.ToArray());
        file.Write(strings.ToArray());
        file.Write(new byte[4096]);
        return (file.ToArray(), dataOffset);
    }

    /// <summary>A stream that fails the test when a read reaches the data block.</summary>
    private sealed class GuardStream(byte[] bytes, int limit) : MemoryStream(bytes, false)
    {
        public override int Read(Span<byte> buffer)
        {
            Assert.True(Position + buffer.Length <= limit, "The reader read the data block.");
            return base.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Assert.True(Position + count <= limit, "The reader read the data block.");
            return base.Read(buffer, offset, count);
        }
    }
}

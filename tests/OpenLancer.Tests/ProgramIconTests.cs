using LibreLancer;
using OpenLancer.Runtime;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The window icon of the editor and Play on Linux and macOS (assets/icon/openlancer.svg).</summary>
public sealed class ProgramIconTests
{
    private const int Size = 256;

    [Fact]
    public void TheIconIsAnImageOf256PixelsInTheByteOrderOfBgra8()
    {
        var image = ProgramIcon.Load();

        Assert.Equal((Size, Size), (image.Width, image.Height));
        var pixels = Bgra8.BufferFromBytes(image.Data);
        Assert.Equal(Size * Size, pixels.Length);
        // The round corner of the tile is clear. The middle of the top edge has the tile color #172433.
        Assert.Equal(0, pixels[0].A);
        var edge = pixels[4 * Size + Size / 2];
        Assert.Equal(((byte)0x17, (byte)0x24, (byte)0x33, (byte)0xFF), (edge.R, edge.G, edge.B, edge.A));
    }
}

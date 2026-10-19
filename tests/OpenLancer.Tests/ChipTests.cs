using System.Linq;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Mods;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The chips of the Inspector and the Mods window (decision D579 of the M11c design).</summary>
public sealed class ChipTests
{
    [Fact]
    public void TheChipsWrapAtTheRightEdge()
    {
        var places = Chip.Layout([40, 40, 40, 10], available: 100, gap: 4);

        Assert.Equal([(0f, 0), (44f, 0), (0f, 1), (44f, 1)], places);
    }

    /// <summary>A chip that is wider than the space starts its own row and does not move to the next row again.</summary>
    [Fact]
    public void AWideChipStartsItsOwnRow()
    {
        var places = Chip.Layout([30, 200, 30], available: 100, gap: 4);

        Assert.Equal([(0f, 0), (0f, 1), (0f, 2)], places);
    }

    /// <summary>Each color of the catalog has its own look. The mark "published" is an outline, and violet stays for
    /// prefab overrides.</summary>
    [Fact]
    public void EachChipColorHasALook()
    {
        var looks = System.Enum.GetValues<ChipColor>().Select(ModHeader.LookOf).ToList();

        Assert.Equal(looks.Count, looks.Distinct().Count());
        Assert.Equal(0, ModHeader.LookOf(ChipColor.GreenOutline).Fill.W);
        Assert.Equal(EditorTheme.Success, ModHeader.LookOf(ChipColor.GreenOutline).Border);
    }
}

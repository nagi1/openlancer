using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The cuts of a text that is too wide. The tests measure one unit for each character.</summary>
public sealed class TextFitTests
{
    // "..." is three units wide.
    private const float EllipsisWidth = 3;

    [Theory]
    [InlineData("C:\\base", 20, "C:\\base")]
    [InlineData("D:\\Projects\\Freelancer\\base", 12, "...ncer\\base")]
    [InlineData("abcdef", 3, "...")]
    public void CutsATextFromTheLeft(string text, float width, string expected)
    {
        Assert.Equal(expected, TextFit.CutLeft(text, width, t => t.Length));
    }

    [Theory]
    [InlineData("Countermeasure", 14, 14)]
    [InlineData("Countermeasure", 20, 14)]
    [InlineData("Countermeasure", 13, 10)]
    [InlineData("Countermeasure", 3, 0)]
    [InlineData("Countermeasure", 1, 0)]
    public void KeepsTheLongestStartThatFitsWithTheEllipsis(string text, float width, int expected)
    {
        Assert.Equal(expected, TextFit.RightCutLength(text, width, n => n, EllipsisWidth));
    }

    [Fact]
    public void DoesNotEndTheStartInASpace()
    {
        // "Suns and planets" in 12 units keeps "Suns and " (9 characters). The cut then removes the space.
        Assert.Equal(8, TextFit.RightCutLength("Suns and planets", 12, n => n, EllipsisWidth));
    }

    [Fact]
    public void DoesNotCutASurrogatePair()
    {
        // The rocket emoji is a surrogate pair at index 2 and 3. A cut after 3 characters would split it.
        var text = "ab\U0001F680cdef";
        Assert.Equal(2, TextFit.RightCutLength(text, 6, n => n, EllipsisWidth));
    }
}

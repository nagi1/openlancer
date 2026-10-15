using System;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The number rules of a THN tree: -0 becomes +0, and NaN and infinity are refused.</summary>
public sealed class ThnNumberTests
{
    /// <summary>The compiler writes the text -0 as +0, so a -0 in a tree could not survive a save. A gizmo rotation
    /// gives -0 often, for example -sin(0) in an orient matrix.</summary>
    [Fact]
    public void NegativeZeroHoldsTheBitsOfPositiveZeroAndWritesZero()
    {
        var number = new ThnNumber(-0f);

        Assert.Equal(0, BitConverter.SingleToInt32Bits(number.Value));
        Assert.Equal("0", ThnWriter.Value(number));
        Assert.Equal(new ThnNumber(0f), number);
        Assert.Equal("0", ThnWriter.Value(new ThnNumber(-MathF.Sin(0f))));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NaNAndInfinityThrow(float value)
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => new ThnNumber(value));
        Assert.Contains(ThnNumber.NotFiniteMessage, e.Message);
    }

    [Theory]
    [InlineData(float.MaxValue)]
    [InlineData(float.MinValue)]
    [InlineData(float.Epsilon)]
    [InlineData(-float.Epsilon)]
    [InlineData(-2.5f)]
    public void FiniteNumbersKeepTheirBits(float value)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(new ThnNumber(value).Value));
    }

    /// <summary>Two numbers are equal when their bits are equal. The next float after 1.5 is a different value.</summary>
    [Fact]
    public void EqualityComparesTheBits()
    {
        Assert.Equal(new ThnNumber(1.5f), new ThnNumber(1.5f));
        Assert.NotEqual(new ThnNumber(1.5f), new ThnNumber(BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(1.5f) + 1)));
    }
}

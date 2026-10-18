using System;
using System.Globalization;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The text of a value for a text INI file of the retail game (section 7.3 of the M11 design).</summary>
public sealed class RetailTextTests
{
    [Fact]
    public void ALargeFloatHasNoExponent()
    {
        // The retail integer getter reads "1E+08" as 1 with atoi.
        Assert.Equal("100000000", RetailText.Float(1e8f));
        Assert.Equal("1500000000", RetailText.Float(1.5e9f));
        // A whole number writes the exact number of the float, so that atoi reads the number of a binary file.
        Assert.Equal("-123456792", RetailText.Float(-123456789f));
        Assert.Equal("1501900032", RetailText.Float(1501900000f));
        Assert.Equal("100000000", RetailText.Of(new SingleValue(1e8f, null)));
        // From 2^31, no integer getter reads the value, so the shortest text can have an exponent.
        Assert.Equal("3E+09", RetailText.Float(3e9f));
    }

    [Fact]
    public void ASmallFloatHasNoExponent()
    {
        Assert.Equal("0.000015", RetailText.Float(1.5e-5f));
        Assert.Equal("-0.0001", RetailText.Float(-1e-4f));
        Assert.Equal("0.5", RetailText.Float(0.5f));
    }

    [Fact]
    public void TheTextGivesTheSameFloat()
    {
        var random = new Random(11);
        for (var i = 0; i < 20000; i++)
        {
            var value = BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue));
            if (!float.IsFinite(value))
                continue;
            var text = RetailText.Float(value);
            Assert.DoesNotContain("E", text.Length > 0 && float.Abs(value) < 2_147_483_648f ? text : "");
            Assert.Equal(value, float.Parse(text, CultureInfo.InvariantCulture));
            // The retail float getter reads the text with atof into a double and then keeps a float.
            Assert.Equal(value, (float)double.Parse(text, CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void ABooleanIsLowercase()
    {
        Assert.Equal("true", RetailText.Of(new BooleanValue(true)));
        Assert.Equal("false", RetailText.Of(new BooleanValue(false)));
    }

    [Fact]
    public void AHashWritesItsExactNumber()
    {
        Assert.Equal("2926089285", RetailText.Of(new SingleValue(2926089285f, 2926089285)));
        Assert.Equal("4294967295", RetailText.Of(new Int32Value(-1, unsigned: true)));
        Assert.Equal("li01_01_base", RetailText.Of(new StringValue("li01_01_base")));
    }
}

using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// Mirrors tests/libprojectM/HLSLParserTest.cpp (String_ToDouble cases).
/// </summary>
public class StringTests
{
    [Fact]
    public void ToDoubleStopsAfterNumber()
    {
        Assert.Equal(1.5, String_.ToDouble("1.5) * x;", out int end), 10);
        Assert.Equal(3, end);
    }

    [Fact]
    public void ToDoubleWithExponent()
    {
        Assert.Equal(0.001, String_.ToDouble("1e-3;", out int end), 10);
        Assert.Equal(4, end);
    }

    [Fact]
    public void ToDoubleAtEndOfInput()
    {
        Assert.Equal(0.25, String_.ToDouble(".25", out int end), 10);
        Assert.Equal(3, end);
    }

    [Fact]
    public void ToDoubleRejectsIdentifier()
    {
        Assert.Equal(0.0, String_.ToDouble("e1 = 2.0;", out int end), 10);
        Assert.Equal(0, end);
    }

    [Fact]
    public void ToDoubleStopsAtSecondDot()
    {
        Assert.Equal(1.2, String_.ToDouble("1.2.3", out int end), 10);
        Assert.Equal(3, end);
    }

    [Fact]
    public void ToDoubleIncompleteExponentFallsBack()
    {
        Assert.Equal(1.0, String_.ToDouble("1e", out int end), 10);
        Assert.Equal(1, end);
    }

    [Fact]
    public void ToIntegerStopsAtNonDigit()
    {
        Assert.Equal(42, String_.ToInteger("42abc", out int end));
        Assert.Equal(2, end);
    }

    [Fact]
    public void ToIntegerHandlesSignAndLeadingSpace()
    {
        Assert.Equal(-7, String_.ToInteger("  -7;", out int end));
        Assert.Equal(4, end);
    }

    [Fact]
    public void ToIntegerRejectsNonNumber()
    {
        Assert.Equal(0, String_.ToInteger("abc", out int end));
        Assert.Equal(0, end);
    }

    [Theory]
    [InlineData(1.0f, "float(1)")]
    [InlineData(0.5f, "float(0.5)")]
    [InlineData(1.2345678f, "float(1.23457)")]
    [InlineData(0.0f, "float(0)")]
    public void FormatFloatUsesSixSignificantDigits(float value, string expected)
    {
        Assert.Equal(expected, String_.FormatFloat(value));
    }
}

using System.Text;
using Xunit;

namespace ZeroDep.Json.Tests;

public sealed class JsonWriterTests
{
    private static string WriteString(string value) => new JsonWriter(indent: false).Value(value).ToString();

    private static string WriteDouble(double value) => new JsonWriter(indent: false).Value(value).ToString();

    // Built from code units: xunit cannot carry a lone surrogate through theory data unchanged.
    [Theory]
    [InlineData(new[] { 'A', (char)0xD800, 'B' }, "\"A\\ud800B\"")]          // lone high surrogate
    [InlineData(new[] { 'A', (char)0xDC00, 'B' }, "\"A\\udc00B\"")]          // lone low surrogate
    [InlineData(new[] { (char)0xDC00, (char)0xD800 }, "\"\\udc00\\ud800\"")] // pair in the wrong order
    [InlineData(new[] { 'A', (char)0xD800 }, "\"A\\ud800\"")]                // high surrogate at the end
    public void UnpairedSurrogatesAreEscaped(char[] value, string expected)
    {
        string json = WriteString(new string(value));

        Assert.Equal(expected, json);
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(json);
    }

    [Fact]
    public void SurrogatePairIsWrittenAsIs()
    {
        Assert.Equal("\"\uD83D\uDE00\"", WriteString("\uD83D\uDE00"));
    }

    [Fact]
    public void LineAndParagraphSeparatorsAreEscaped()
    {
        Assert.Equal("\"a\\u2028b\\u2029c\"", WriteString("a\u2028b\u2029c"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteNumbersAreWrittenAsNull(double value)
    {
        Assert.Equal("null", WriteDouble(value));
    }

    [Fact]
    public void FiniteNumbersAreUnchanged()
    {
        Assert.Equal("1.5", WriteDouble(1.5));
        Assert.Equal("0", WriteDouble(0));
    }
}

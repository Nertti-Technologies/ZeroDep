using System.Globalization;
using Xunit;
using ZeroDep.Abstractions;

namespace ZeroDep.Cli.Tests;

public sealed class ConsoleTextTests
{
    // An operating-system-command title change, a colour change, and a C1 control-sequence introducer.
    private const string Hostile = "\u001b]0;title\u0007\u001b[31mRED\u009b31m";

    [Theory]
    [InlineData("\u0000\u001f\u007f")]
    [InlineData("\u0080\u0085\u009b\u009f")]
    public void CleanReplacesC0DelAndC1Controls(string controls)
    {
        Assert.Equal(new string(' ', controls.Length), ConsoleText.Clean(controls));
    }

    [Fact]
    public void CleanKeepsPrintableText()
    {
        Assert.Equal("Name \u00e9 \u540d\u524d \u00a0", ConsoleText.Clean("Name \u00e9 \u540d\u524d \u00a0"));
    }

    [Fact]
    public void FieldLineEscapesEveryPdfDerivedValue()
    {
        var field = new FormFieldInfo
        {
            FullyQualifiedName = "name" + Hostile,
            FieldType = "Tx" + Hostile,
            Value = "value" + Hostile,
            Label = "label" + Hostile,
        };

        AssertNoControls(ConsoleText.FieldLine(field));
    }

    [Fact]
    public void ImageLineEscapesTheResourceName()
    {
        var image = new ImageDpiInfo { ResourceName = "Im1" + Hostile };

        AssertNoControls(ConsoleText.ImageLine(image));
    }

    [Fact]
    public void ExistingFileIsNotTakenAsTheThreshold()
    {
        Assert.False(CliArguments.TryParseThreshold("300", _ => true, out _));
        Assert.True(CliArguments.TryParseThreshold("300", _ => false, out int value));
        Assert.Equal(300, value);
    }

    [Fact]
    public void NumbersAreParsedIndependentlyOfTheCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        try
        {
            CultureInfo.CurrentCulture = culture;
            Assert.False(CliArguments.TryParseInt("~5", out _));
            Assert.True(CliArguments.TryParseInt("-5", out int value));
            Assert.Equal(-5, value);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static void AssertNoControls(string line)
    {
        foreach (char c in line)
        {
            Assert.False(char.IsControl(c), $"control character U+{(int)c:X4} reached the terminal");
        }
    }
}

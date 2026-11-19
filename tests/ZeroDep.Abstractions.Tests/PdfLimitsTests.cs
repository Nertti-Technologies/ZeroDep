using System;
using Xunit;
using ZeroDep.Abstractions;

namespace ZeroDep.Abstractions.Tests;

public sealed class PdfLimitsTests
{
    [Fact]
    public void Defaults_AreTheFixedValuesTheyReplace()
    {
        PdfLimits limits = PdfLimits.Default;

        Assert.Equal(256L * 1024 * 1024, limits.MaxDecodedStreamBytes);
        Assert.Equal(1L << 28, limits.MaxImagePixels);
        Assert.Equal(50_000_000L, limits.MaxWorkPerPage);
        Assert.Equal(250_000_000L, limits.MaxWorkPerDocument);
        Assert.Equal(100_000, limits.MaxFormFields);
        Assert.Equal(1_048_576, limits.MaxCMapEntries);
        Assert.Equal(256L, limits.WorkPerSourceByte);
    }

    [Fact]
    public void NewInstance_HasTheDefaults()
    {
        var limits = new PdfLimits();

        Assert.Equal(PdfLimits.Default.MaxDecodedStreamBytes, limits.MaxDecodedStreamBytes);
        Assert.Equal(PdfLimits.Default.MaxImagePixels, limits.MaxImagePixels);
        Assert.Equal(PdfLimits.Default.MaxWorkPerPage, limits.MaxWorkPerPage);
        Assert.Equal(PdfLimits.Default.MaxWorkPerDocument, limits.MaxWorkPerDocument);
        Assert.Equal(PdfLimits.Default.MaxFormFields, limits.MaxFormFields);
        Assert.Equal(PdfLimits.Default.MaxCMapEntries, limits.MaxCMapEntries);
        Assert.Equal(PdfLimits.Default.WorkPerSourceByte, limits.WorkPerSourceByte);
    }

    [Fact]
    public void PositiveValues_AreKept()
    {
        var limits = new PdfLimits
        {
            MaxDecodedStreamBytes = 1,
            MaxImagePixels = 2,
            MaxWorkPerPage = 3,
            MaxWorkPerDocument = long.MaxValue,
            MaxFormFields = 5,
            MaxCMapEntries = int.MaxValue,
        };

        Assert.Equal(1, limits.MaxDecodedStreamBytes);
        Assert.Equal(2, limits.MaxImagePixels);
        Assert.Equal(3, limits.MaxWorkPerPage);
        Assert.Equal(long.MaxValue, limits.MaxWorkPerDocument);
        Assert.Equal(5, limits.MaxFormFields);
        Assert.Equal(int.MaxValue, limits.MaxCMapEntries);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void ZeroOrNegativeValues_AreRejected(long value)
    {
        int small = value < int.MinValue ? int.MinValue : (int)value;
        AssertRejected(nameof(PdfLimits.MaxDecodedStreamBytes), () => new PdfLimits { MaxDecodedStreamBytes = value });
        AssertRejected(nameof(PdfLimits.MaxImagePixels), () => new PdfLimits { MaxImagePixels = value });
        AssertRejected(nameof(PdfLimits.MaxWorkPerPage), () => new PdfLimits { MaxWorkPerPage = value });
        AssertRejected(nameof(PdfLimits.MaxWorkPerDocument), () => new PdfLimits { MaxWorkPerDocument = value });
        AssertRejected(nameof(PdfLimits.MaxFormFields), () => new PdfLimits { MaxFormFields = small });
        AssertRejected(nameof(PdfLimits.MaxCMapEntries), () => new PdfLimits { MaxCMapEntries = small });
    }

    [Fact]
    public void WorkPerSourceByte_AllowsZeroAndRejectsNegative()
    {
        Assert.Equal(0, new PdfLimits { WorkPerSourceByte = 0 }.WorkPerSourceByte);
        AssertRejected(nameof(PdfLimits.WorkPerSourceByte), () => new PdfLimits { WorkPerSourceByte = -1 });
    }

    [Theory]
    [InlineData(1_000L, 0L, 0L, 1_000L)]
    [InlineData(1_000L, 0L, 5_000L, 1_000L)]
    [InlineData(1_000L, 256L, 0L, 1_000L)]
    [InlineData(1_000L, 256L, 10L, 3_560L)]
    [InlineData(250_000_000L, 256L, 4_152_398L, 1_313_013_888L)] // the scanned book from ADR-0011
    public void DocumentWorkBudget_IsTheBasePlusThePerByteAllowance(long baseWork, long perByte, long length, long expected)
    {
        var limits = new PdfLimits { MaxWorkPerDocument = baseWork, WorkPerSourceByte = perByte };

        Assert.Equal(expected, limits.DocumentWorkBudget(length));
    }

    [Fact]
    public void DocumentWorkBudget_SaturatesAndNeverLowersTheBase()
    {
        var limits = new PdfLimits { MaxWorkPerDocument = long.MaxValue - 10, WorkPerSourceByte = long.MaxValue };

        Assert.Equal(long.MaxValue, limits.DocumentWorkBudget(1));
        Assert.Equal(long.MaxValue, PdfLimits.Default.DocumentWorkBudget(long.MaxValue));
        Assert.True(PdfLimits.Default.DocumentWorkBudget(1) >= PdfLimits.Default.MaxWorkPerDocument);
    }

    [Fact]
    public void DocumentWorkBudget_RejectsANegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfLimits.Default.DocumentWorkBudget(-1));
    }

    [Fact]
    public void AnalyzerOptions_UseTheDefaultLimits()
    {
        Assert.Same(PdfLimits.Default, new AnalyzerOptions().Limits);
    }

    [Fact]
    public void AnalyzerOptions_RejectNullLimits()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new AnalyzerOptions { Limits = null! });
        Assert.Equal(nameof(AnalyzerOptions.Limits), ex.ParamName);
    }

    [Fact]
    public void LimitExceededException_IsASyntaxExceptionNamingTheLimit()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new PdfLimitExceededException("MaxWorkPerPage", 42, "too much", inner);

        Assert.IsAssignableFrom<PdfSyntaxException>(ex);
        Assert.Equal("MaxWorkPerPage", ex.LimitName);
        Assert.Equal(42, ex.Limit);
        Assert.Equal("too much", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(string.Empty, new PdfLimitExceededException("m").LimitName);
        Assert.Equal(string.Empty, new PdfLimitExceededException().LimitName);
    }

    [Fact]
    public void SyntaxException_CanBeDerivedFrom()
    {
        Assert.False(typeof(PdfSyntaxException).IsSealed);
        Assert.True(typeof(PdfLimitExceededException).IsSealed);
    }

    [Fact]
    public void ResourceLimitExceeded_IsAppendedWithoutRenumbering()
    {
        Assert.Equal(8, (int)RejectionReason.EncryptedPasswordRequired);
        Assert.Equal(9, (int)RejectionReason.ResourceLimitExceeded);
        Assert.Equal(RejectionReason.ResourceLimitExceeded, Enum.GetValues<RejectionReason>()[^1]);
    }

    [Fact]
    public void PageClassification_IsCompleteByDefault()
    {
        var page = new PageClassification();

        Assert.False(page.Incomplete);
        Assert.Empty(page.IncompleteReasons);
    }

    private static void AssertRejected(string name, Func<PdfLimits> create)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => create());
        Assert.Equal(name, ex.ParamName);
    }
}

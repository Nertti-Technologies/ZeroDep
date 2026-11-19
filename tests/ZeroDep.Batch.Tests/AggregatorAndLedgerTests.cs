using System.Collections.Generic;
using System.Linq;
using Xunit;
using ZeroDep.Abstractions;

namespace ZeroDep.Batch.Tests;

public sealed class AggregatorAndLedgerTests
{
    [Fact]
    public void FileOutcomeRoundTripsThroughLedgerLine()
    {
        var outcome = new FileOutcome
        {
            Id = "abc123",
            InputHash = "def456",
            Status = DocumentStatus.Processed,
            Category = DocumentCategory.ScannedWithOcr,
            PageCount = 12,
            OcrPresent = true,
            FormPresent = false,
            EncryptionAlgorithm = EncryptionAlgorithm.Aes256,
            Encrypted = true,
            EncryptedUnreadable = false,
            Rejection = RejectionReason.None,
            Threshold = 150,
            BelowThresholdCount = 3,
            EffectiveDpis = new double[] { 72, 200, 600 },
            PasswordSupplied = true,
            EngineVersion = "2.1.1",
        };

        FileOutcome? parsed = FileOutcome.FromLedgerLine(outcome.ToLedgerLine());

        Assert.NotNull(parsed);
        Assert.Equal(outcome.Id, parsed!.Id);
        Assert.Equal(outcome.InputHash, parsed.InputHash);
        Assert.Equal(outcome.Category, parsed.Category);
        Assert.Equal(outcome.PageCount, parsed.PageCount);
        Assert.True(parsed.OcrPresent);
        Assert.Equal(EncryptionAlgorithm.Aes256, parsed.EncryptionAlgorithm);
        Assert.Equal(3, parsed.BelowThresholdCount);
        Assert.Equal(new double[] { 72, 200, 600 }, parsed.EffectiveDpis);
        Assert.True(parsed.PasswordSupplied);
        Assert.Equal("2.1.1", parsed.EngineVersion);
    }

    [Fact]
    public void LedgerLineInTheEarlierFormatParsesButNeverMatchesARun()
    {
        string oldLine = string.Join("\t", new[]
        {
            "abc123", "def456", "Processed", "DigitalText", "1", "0", "0", "None", "0", "0", "None", "150", "0", string.Empty,
        });

        FileOutcome? parsed = FileOutcome.FromLedgerLine(oldLine);

        Assert.NotNull(parsed);
        Assert.Equal(string.Empty, parsed!.EngineVersion);
        Assert.False(parsed.MatchesRun("def456", 150, passwordSupplied: false, engineVersion: "2.1.1"));
    }

    [Theory]
    [InlineData("other", 150, false, "2.1.1")]
    [InlineData("def456", 300, false, "2.1.1")]
    [InlineData("def456", 150, true, "2.1.1")]
    [InlineData("def456", 150, false, "2.2.0")]
    public void OutcomeMatchesOnlyTheSameInputAndRunDetails(string hash, int threshold, bool password, string engine)
    {
        var outcome = new FileOutcome { InputHash = "def456", Threshold = 150, PasswordSupplied = false, EngineVersion = "2.1.1" };

        Assert.True(outcome.MatchesRun("def456", 150, passwordSupplied: false, engineVersion: "2.1.1"));
        Assert.False(outcome.MatchesRun(hash, threshold, password, engine));
    }

    [Fact]
    public void ReportedThresholdIsTheRunThresholdWhateverTheOutcomeOrder()
    {
        var outcomes = new List<FileOutcome>
        {
            Processed(DocumentCategory.DigitalText, ocr: false, dpis: new double[] { 300 }),
            Processed(DocumentCategory.ScannedImageOnly, ocr: false, dpis: new double[] { 72 }),
        };
        outcomes[0].Threshold = 100;
        outcomes[1].Threshold = 200;

        AggregateStatistics forward = Aggregator.Build(outcomes, ClassificationThresholds.Default, 150);
        outcomes.Reverse();
        AggregateStatistics reversed = Aggregator.Build(outcomes, ClassificationThresholds.Default, 150);

        Assert.Equal(150, forward.Dpi.Threshold);
        Assert.Equal(150, reversed.Dpi.Threshold);
    }

    [Fact]
    public void AggregateCountsAndPercentagesAreCorrect()
    {
        var outcomes = new List<FileOutcome>
        {
            Processed(DocumentCategory.DigitalText, ocr: false, dpis: new double[] { 300, 300 }),
            Processed(DocumentCategory.ScannedWithOcr, ocr: true, dpis: new double[] { 100 }, below: 1),
            Processed(DocumentCategory.FormBased, ocr: false, form: true, dpis: System.Array.Empty<double>()),
            Rejected(RejectionReason.TruncatedStream),
        };

        AggregateStatistics stats = Aggregator.Build(outcomes, ClassificationThresholds.Default, 150);

        Assert.Equal(4, stats.Corpus.Total);
        Assert.Equal(3, stats.Corpus.Processed);
        Assert.Equal(1, stats.Corpus.Rejected);
        Assert.Equal(3, stats.Dpi.ImagesMeasured);
        Assert.Equal(100, stats.Dpi.Min);
        Assert.Contains(stats.Categories, c => c.Category == "DigitalText" && c.Count == 1 && c.Percent == 25.0);
        Assert.Contains(stats.Rejections, r => r.Reason == "TruncatedStream" && r.Count == 1);
        // 1 of 3 measured images below threshold -> 33.3%
        Assert.Equal(33.3, stats.Dpi.BelowThresholdPercent);
        // form present in 1 of 3 processed -> 33.3%
        Assert.Equal(33.3, stats.FormPresentPercent);
    }

    [Fact]
    public void AggregationIsOrderIndependent()
    {
        var outcomes = new List<FileOutcome>
        {
            Processed(DocumentCategory.DigitalText, ocr: false, dpis: new double[] { 150, 250, 350 }),
            Processed(DocumentCategory.ScannedImageOnly, ocr: false, dpis: new double[] { 72, 96 }),
            Rejected(RejectionReason.MissingHeader),
        };

        AggregateStatistics forward = Aggregator.Build(outcomes, ClassificationThresholds.Default, 150);
        outcomes.Reverse();
        AggregateStatistics reversed = Aggregator.Build(outcomes, ClassificationThresholds.Default, 150);

        // Compare deterministic aggregates (excluding the wall-clock generatedUtc timestamp).
        Assert.Equal(forward.Corpus.Total, reversed.Corpus.Total);
        Assert.Equal(forward.Corpus.Processed, reversed.Corpus.Processed);
        Assert.Equal(forward.Corpus.Rejected, reversed.Corpus.Rejected);
        Assert.Equal(forward.Dpi.Min, reversed.Dpi.Min);
        Assert.Equal(forward.Dpi.Median, reversed.Dpi.Median);
        Assert.Equal(forward.Dpi.P95, reversed.Dpi.P95);
        Assert.Equal(forward.Dpi.ImagesMeasured, reversed.Dpi.ImagesMeasured);
        Assert.Equal(
            forward.Categories.Select(c => c.Category + ":" + c.Count),
            reversed.Categories.Select(c => c.Category + ":" + c.Count));
        Assert.Equal(
            forward.Dpi.Histogram.Select(b => b.Bucket + ":" + b.Count),
            reversed.Dpi.Histogram.Select(b => b.Bucket + ":" + b.Count));
    }

    private static FileOutcome Processed(DocumentCategory category, bool ocr, IReadOnlyList<double> dpis, bool form = false, int below = 0)
        => new FileOutcome
        {
            Id = category.ToString() + dpis.Count,
            InputHash = "h",
            Status = DocumentStatus.Processed,
            Category = category,
            OcrPresent = ocr,
            FormPresent = form,
            Threshold = 150,
            BelowThresholdCount = below,
            EffectiveDpis = dpis.ToList(),
        };

    private static FileOutcome Rejected(RejectionReason reason)
        => new FileOutcome
        {
            Id = "rej" + reason,
            InputHash = "h",
            Status = DocumentStatus.Rejected,
            Category = DocumentCategory.Rejected,
            Rejection = reason,
            Threshold = 150,
        };
}

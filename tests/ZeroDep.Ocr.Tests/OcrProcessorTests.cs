using System;
using System.Linq;
using System.Reflection;
using Xunit;
using ZeroDep.Abstractions;
using ZeroDep.Ocr;

namespace ZeroDep.Ocr.Tests;

/// <summary>
/// O2 orchestration: OCR over a document's images appends OcrGenerated text runs (tagged, with
/// confidence) without disturbing existing text. A fake engine stands in for a real one.
/// </summary>
public sealed class OcrProcessorTests
{
    private const string Gray8 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/wAALCAAIAAgBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAAAP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AP//Z";

    private sealed class FakeOcrEngine : IOcrEngine
    {
        public OcrResult Recognize(DecodedImage image, OcrOptions options)
            => new OcrResult
            {
                Lines = new[]
                {
                    new OcrLine { Text = "recovered text", Confidence = 0.9, Bounds = new BoundingBox(10, 20, 100, 12) },
                    new OcrLine { Text = "low conf", Confidence = 0.2, Bounds = new BoundingBox(10, 40, 80, 12) },
                },
            };
    }

    [Fact]
    public void Augment_AppendsTaggedOcrRuns_AndHonorsConfidence()
    {
        var analysis = new DocumentAnalysis
        {
            PageCount = 1,
            ImageAreaFraction = 0.95,
            TextRuns = Array.Empty<TextRunInfo>(),
        };
        var images = new[]
        {
            new PdfImageInfo { PageIndex = 0, Filter = "DCTDecode", DeclaredWidth = 8, DeclaredHeight = 8, EncodedData = Convert.FromBase64String(Gray8) },
        };

        DocumentAnalysis result = OcrProcessor.Augment(analysis, images, new FakeOcrEngine(), new OcrOptions { MinConfidence = 0.5 });

        var ocr = result.TextRuns.Where(r => r.Source == TextSource.OcrGenerated).ToList();
        TextRunInfo run = Assert.Single(ocr);   // the low-confidence line is dropped
        Assert.Equal("recovered text", run.Text);
        Assert.Equal(0.9, run.Confidence, 3);
        Assert.Equal(0, run.PageIndex);
        Assert.Equal(100, run.Width, 3);
    }

    [Fact]
    public void Augment_KeepsEveryOtherAnalysisProperty()
    {
        var analysis = new DocumentAnalysis
        {
            SchemaVersion = "9.9",
            Status = DocumentStatus.Rejected,   // not a realistic pairing; it gives each property a non-default value
            Rejection = new RejectionInfo { Reason = RejectionReason.MalformedObject, Detail = "d" },
            PageCount = 1,
            Security = new SecurityInfo { IsEncrypted = true },
            Images = new[] { new ImageDpiInfo { PageIndex = 0 } },
            TextRuns = Array.Empty<TextRunInfo>(),
            Form = new AcroFormReport { HasAcroForm = true },
            Coverage = new[] { new CoverageItem { Id = "t0", Kind = "text", Value = "v" } },
            Pages = new[] { new PageClassification { PageIndex = 0 } },
            ImageAreaFraction = 0.95,
        };

        DocumentAnalysis result = OcrProcessor.Augment(analysis, OneJpegImage(), new FakeOcrEngine(), new OcrOptions());

        Assert.NotSame(analysis, result);
        foreach (PropertyInfo property in typeof(DocumentAnalysis).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == nameof(DocumentAnalysis.TextRuns))
            {
                continue;
            }

            object? expected = property.GetValue(analysis);
            Assert.NotEqual(property.GetValue(new DocumentAnalysis()), expected); // the fixture sets every property
            Assert.Equal(expected, property.GetValue(result));
        }
    }

    private static PdfImageInfo[] OneJpegImage()
        => new[]
        {
            new PdfImageInfo { PageIndex = 0, Filter = "DCTDecode", DeclaredWidth = 8, DeclaredHeight = 8, EncodedData = Convert.FromBase64String(Gray8) },
        };

    [Fact]
    public void Augment_WithNoImages_ReturnsOriginalAnalysis()
    {
        var analysis = new DocumentAnalysis { PageCount = 1 };
        DocumentAnalysis result = OcrProcessor.Augment(analysis, Array.Empty<PdfImageInfo>(), new FakeOcrEngine(), new OcrOptions());
        Assert.Same(analysis, result);
    }
}

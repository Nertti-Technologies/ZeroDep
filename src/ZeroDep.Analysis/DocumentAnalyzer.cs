using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ZeroDep.Abstractions;
using ZeroDep.Model;
using ZeroDep.Objects;
using ZeroDep.Validation;

namespace ZeroDep.Analysis;

/// <summary>
/// Runs all structural features over a single opened document and assembles the unified
/// <see cref="DocumentAnalysis"/>. Corruption is reported as a structured rejection, never thrown:
/// any failure other than <see cref="OutOfMemoryException"/> becomes a rejected result.
/// </summary>
internal static class DocumentAnalyzer
{
    /// <summary>Opens a PDF stream and produces the full analysis (or a structured rejection).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static DocumentAnalysis Analyze(Stream stream, int dpiThreshold, string? password = null)
        => Analyze(stream, dpiThreshold, password, PdfLimits.Default);

    /// <summary>
    /// Opens a PDF stream within <paramref name="limits"/> and produces the full analysis (or a structured
    /// rejection). A limit breach that ends the document, including the document work budget, is reported
    /// as <see cref="RejectionReason.ResourceLimitExceeded"/>; a page that loses content to a limit is kept
    /// and marked incomplete.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="limits"/> is null.</exception>
    public static DocumentAnalysis Analyze(Stream stream, int dpiThreshold, string? password, PdfLimits limits)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        try
        {
            RejectionReason? preflight = PdfValidator.Preflight(stream);
            if (preflight.HasValue)
            {
                return Rejected(preflight.Value, "Pre-flight integrity check failed.");
            }

            using PdfDocument document = PdfDocument.Open(stream, password, limits);

            // The analyzers share only this collector: each records the pages it could not read in full,
            // and the classifier reports them (ADR-0010 option C2).
            var incomplete = new PageIncompleteness();
            SecurityInfo security = BuildSecurity(document);
            IReadOnlyList<ImageDpiInfo> images = DpiAnalyzer.Analyze(document, dpiThreshold, incomplete);
            IReadOnlyList<TextRunInfo> textRuns = TextAnalyzer.AnalyzeWithStructure(document, out IReadOnlyList<PageStructure> pageStructures, incomplete);
            AcroFormReport form = AcroFormAnalyzer.Analyze(document);
            IReadOnlyList<PageClassification> pages = PageClassifier.Classify(document, textRuns, images, form, pageStructures, incomplete);

            return new DocumentAnalysis
            {
                Status = DocumentStatus.Processed,
                PageCount = document.PageCount,
                Security = security,
                Images = images,
                TextRuns = textRuns,
                Form = form,
                Coverage = BuildCoverage(textRuns, form),
                ImageAreaFraction = ComputeImageAreaFraction(document, images),
                Pages = pages,
            };
        }
        catch (PdfLimitExceededException ex)
        {
            return Rejected(RejectionReason.ResourceLimitExceeded, ex.Message);
        }
        catch (PdfSyntaxException ex)
        {
            return Rejected(PdfValidator.Classify(ex.Message), ex.Message);
        }
        catch (Exception ex) when (ResourceBudget.AsLimitExceeded(ex) is { } limit)
        {
            return Rejected(RejectionReason.ResourceLimitExceeded, limit.Message);
        }
        catch (Exception ex) when (ExceptionBoundary.IsTranslatable(ex))
        {
            // Any other failure inside the engine means the file drove it somewhere it could not handle:
            // report it as a malformed object rather than letting an undocumented type escape.
            return Rejected(RejectionReason.MalformedObject, ExceptionBoundary.Translate(ex).Message);
        }
    }

    /// <summary>
    /// Computes the largest fraction of any page's area covered by raster images — a content-free
    /// structural signal for "image-dominated" (scanned) classification. Per-page image area is
    /// summed and capped at the page area; the maximum across pages is returned.
    /// </summary>
    private static double ComputeImageAreaFraction(PdfDocument document, IReadOnlyList<ImageDpiInfo> images)
    {
        if (images.Count == 0)
        {
            return 0;
        }

        var areaByPage = new Dictionary<int, double>();
        foreach (ImageDpiInfo image in images)
        {
            double area = image.RenderedWidthPoints * image.RenderedHeightPoints;
            if (area <= 0)
            {
                continue;
            }

            areaByPage.TryGetValue(image.PageIndex, out double accumulated);
            areaByPage[image.PageIndex] = accumulated + area;
        }

        double max = 0;
        foreach (PdfPage page in document.Pages)
        {
            if (!areaByPage.TryGetValue(page.Index, out double imageArea))
            {
                continue;
            }

            double pageArea = page.MediaBox.Width * page.MediaBox.Height;
            if (pageArea <= 0)
            {
                continue;
            }

            double fraction = imageArea / pageArea;
            if (fraction > 1.0)
            {
                fraction = 1.0;
            }

            if (fraction > max)
            {
                max = fraction;
            }
        }

        return max;
    }

    private static SecurityInfo BuildSecurity(PdfDocument document)
    {
        return new SecurityInfo
        {
            IsEncrypted = document.IsEncrypted,
            HandlerSupported = document.HandlerSupported,
            Algorithm = document.Algorithm,
            Revision = document.EncryptionRevision,
            Authentication = document.Authentication,
            MetadataEncrypted = document.IsEncrypted && document.EncryptMetadata,
            Permissions = document.Permissions,
        };
    }

    private static DocumentAnalysis Rejected(RejectionReason reason, string detail)
        => new DocumentAnalysis
        {
            Status = DocumentStatus.Rejected,
            Rejection = new RejectionInfo { Reason = reason, Detail = detail },
        };

    private static IReadOnlyList<CoverageItem> BuildCoverage(IReadOnlyList<TextRunInfo> textRuns, AcroFormReport form)
    {
        var coverage = new List<CoverageItem>();

        int t = 0;
        foreach (TextRunInfo run in textRuns)
        {
            coverage.Add(new CoverageItem
            {
                Id = "t" + t.ToString(CultureInfo.InvariantCulture),
                Kind = "text",
                Value = run.Text,
                Page = run.PageIndex,
                Bounds = new BoundingBox(run.X, run.Y, run.Width, run.FontSize),
            });
            t++;
        }

        int f = 0;
        foreach (FormFieldInfo field in form.Fields)
        {
            if (field.IsChecked is bool isChecked)
            {
                coverage.Add(new CoverageItem
                {
                    Id = "f" + f.ToString(CultureInfo.InvariantCulture),
                    Kind = "checkbox",
                    Value = isChecked ? "true" : "false",
                    Page = field.PageIndex ?? -1,
                    Bounds = field.Rect ?? default,
                });
                f++;
            }
            else if (!string.IsNullOrEmpty(field.Value))
            {
                coverage.Add(new CoverageItem
                {
                    Id = "f" + f.ToString(CultureInfo.InvariantCulture),
                    Kind = "field",
                    Value = field.Value!,
                    Page = field.PageIndex ?? -1,
                    Bounds = field.Rect ?? default,
                });
                f++;
            }
        }

        return coverage;
    }
}

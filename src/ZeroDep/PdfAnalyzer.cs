using System;
using System.Collections.Generic;
using System.IO;
using ZeroDep.Abstractions;
using ZeroDep.Analysis;
using ZeroDep.Json;

namespace ZeroDep;

/// <summary>Public entry point for ZeroDep structural analysis features.</summary>
/// <remarks>
/// <para>
/// Exception contract: <c>Analyze</c> and <c>ToJson</c> report an unreadable document as a rejected result
/// rather than throwing. The extraction methods throw <see cref="PdfSyntaxException"/> for an unreadable
/// document; any other unexpected failure while reading the file is wrapped in a
/// <see cref="PdfSyntaxException"/> as well. A null <c>stream</c> or <c>options</c> throws
/// <see cref="ArgumentNullException"/>. <see cref="OutOfMemoryException"/> is never caught or wrapped.
/// </para>
/// <para>
/// Resource limits: the overloads that take <see cref="AnalyzerOptions"/> apply
/// <see cref="AnalyzerOptions.Limits"/>; the other overloads apply <see cref="PdfLimits.Default"/>. A document
/// that exceeds a limit is rejected with <see cref="RejectionReason.ResourceLimitExceeded"/> by
/// <c>Analyze</c> and <c>ToJson</c>, and makes the extraction methods throw
/// <see cref="PdfLimitExceededException"/> (a <see cref="PdfSyntaxException"/>). A page whose content exceeds a
/// per-page limit is kept and marked <see cref="PageClassification.Incomplete"/>; an image that exceeds a limit
/// is skipped by the image extraction methods.
/// </para>
/// </remarks>
public static class PdfAnalyzer
{
    /// <summary>Extracts the interactive form (AcroForm) fields from a PDF stream (Feature C).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static AcroFormReport ExtractForm(Stream stream, string? password = null)
        => ExtractForm(stream, new AnalyzerOptions { Password = password });

    /// <summary>Extracts the interactive form (AcroForm) fields from a PDF stream (Feature C).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="options">The password and the resource limits to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="PdfLimitExceededException">The document exceeds a limit, for example <see cref="PdfLimits.MaxFormFields"/>.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static AcroFormReport ExtractForm(Stream stream, AnalyzerOptions options)
    {
        ThrowIfNull(stream, options);
        return ExceptionBoundary.Run(() => AcroFormAnalyzer.Analyze(stream, options.Password, options.Limits));
    }

    /// <summary>Computes effective DPI for every placed image and flags those below a threshold (Feature A).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="dpiThreshold">Images with effective DPI below this value are flagged.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<ImageDpiInfo> AnalyzeImageDpi(Stream stream, int dpiThreshold = AnalyzerOptions.DefaultDpiThreshold, string? password = null)
        => AnalyzeImageDpi(stream, new AnalyzerOptions { DpiThreshold = dpiThreshold, Password = password });

    /// <summary>Computes effective DPI for every placed image and flags those below a threshold (Feature A).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="options">The DPI threshold, the password and the resource limits to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="PdfLimitExceededException">The document exceeds a limit that ends it, such as <see cref="PdfLimits.MaxWorkPerDocument"/>.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<ImageDpiInfo> AnalyzeImageDpi(Stream stream, AnalyzerOptions options)
    {
        ThrowIfNull(stream, options);
        return ExceptionBoundary.Run(() => DpiAnalyzer.Analyze(stream, options.DpiThreshold, options.Password, options.Limits));
    }

    /// <summary>Extracts positioned text runs (including the invisible OCR layer) from a PDF stream (Feature B).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<TextRunInfo> ExtractText(Stream stream, string? password = null)
        => ExtractText(stream, new AnalyzerOptions { Password = password });

    /// <summary>Extracts positioned text runs (including the invisible OCR layer) from a PDF stream (Feature B).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="options">The password and the resource limits to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="PdfLimitExceededException">The document exceeds a limit that ends it, such as <see cref="PdfLimits.MaxWorkPerDocument"/>.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<TextRunInfo> ExtractText(Stream stream, AnalyzerOptions options)
    {
        ThrowIfNull(stream, options);
        return ExceptionBoundary.Run(() => TextAnalyzer.Analyze(stream, options.Password, options.Limits));
    }

    /// <summary>Extracts a simple line-grouped plain-text rendering of a PDF stream.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static string ExtractPlainText(Stream stream, string? password = null)
        => ExtractPlainText(stream, new AnalyzerOptions { Password = password });

    /// <summary>Extracts a simple line-grouped plain-text rendering of a PDF stream.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="options">The password and the resource limits to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="PdfLimitExceededException">The document exceeds a limit that ends it, such as <see cref="PdfLimits.MaxWorkPerDocument"/>.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static string ExtractPlainText(Stream stream, AnalyzerOptions options)
    {
        ThrowIfNull(stream, options);
        return ExceptionBoundary.Run(() => TextAnalyzer.GetPlainText(stream, options.Password, options.Limits));
    }

    /// <summary>Runs all features and returns the unified document analysis (with coverage manifest).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="dpiThreshold">Images with effective DPI below this value are flagged.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <returns>
    /// The analysis. A document that cannot be read is returned with <see cref="DocumentStatus.Rejected"/>
    /// and a <see cref="RejectionInfo"/>; it is not thrown.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static DocumentAnalysis Analyze(Stream stream, int dpiThreshold = AnalyzerOptions.DefaultDpiThreshold, string? password = null)
        => Analyze(stream, new AnalyzerOptions { DpiThreshold = dpiThreshold, Password = password });

    /// <summary>Runs all features and returns the unified document analysis (with coverage manifest).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="options">The DPI threshold, the password and the resource limits to apply.</param>
    /// <returns>
    /// The analysis. A document that cannot be read, or that exceeds a limit that ends it, is returned with
    /// <see cref="DocumentStatus.Rejected"/> and a <see cref="RejectionInfo"/>; it is not thrown. A page that
    /// loses content to a limit is marked <see cref="PageClassification.Incomplete"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    public static DocumentAnalysis Analyze(Stream stream, AnalyzerOptions options)
    {
        ThrowIfNull(stream, options);
        return DocumentAnalyzer.Analyze(stream, options.DpiThreshold, options.Password, options.Limits);
    }

    /// <summary>Runs all features and serializes the result to the versioned JSON schema.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="indent">Whether to pretty-print the JSON.</param>
    /// <param name="dpiThreshold">Images with effective DPI below this value are flagged.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <returns>The JSON document. A document that cannot be read is serialized as a rejected result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The result could not be serialized.</exception>
    public static string ToJson(Stream stream, bool indent = false, int dpiThreshold = AnalyzerOptions.DefaultDpiThreshold, string? password = null)
        => ToJson(stream, new AnalyzerOptions { DpiThreshold = dpiThreshold, Password = password }, indent);

    /// <summary>Runs all features and serializes the result to the versioned JSON schema.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="options">The DPI threshold, the password and the resource limits to apply.</param>
    /// <param name="indent">Whether to pretty-print the JSON.</param>
    /// <returns>The JSON document. A document that cannot be read is serialized as a rejected result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The result could not be serialized.</exception>
    public static string ToJson(Stream stream, AnalyzerOptions options, bool indent = false)
    {
        DocumentAnalysis analysis = Analyze(stream, options);
        return ExceptionBoundary.Run(() => DocumentJson.Write(analysis, indent));
    }

    /// <summary>Enumerates the embedded image XObjects in a PDF, with their declared size, filter, and raw bytes.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<PdfImageInfo> ExtractImages(Stream stream, string? password = null)
        => ExtractImages(stream, new AnalyzerOptions { Password = password });

    /// <summary>Enumerates the embedded image XObjects in a PDF, with their declared size, filter, and raw bytes.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="options">The password and the resource limits to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="PdfLimitExceededException">The document exceeds a limit that ends it, such as <see cref="PdfLimits.MaxWorkPerDocument"/>.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<PdfImageInfo> ExtractImages(Stream stream, AnalyzerOptions options)
    {
        ThrowIfNull(stream, options);
        return ExceptionBoundary.Run(() => ImageExtractor.Extract(stream, options.Password, options.Limits));
    }

    /// <summary>
    /// Decodes embedded images to colour-correct RGB (or grayscale), applying each image's PDF colour space
    /// — DeviceGray/RGB/CMYK, Indexed (palette), ICCBased, CalGray/CalRGB/Lab, Separation/DeviceN — over
    /// the pure-BCL image decoders (JPEG, CCITT, JBIG2, JPEG 2000, Flate/LZW raster) (Feature: colour pipeline).
    /// </summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<ColorImage> ExtractColorImages(Stream stream, string? password = null)
        => ExtractColorImages(stream, new AnalyzerOptions { Password = password });

    /// <summary>
    /// Decodes embedded images to colour-correct RGB (or grayscale), applying each image's PDF colour space,
    /// within the resource limits of <paramref name="options"/>. An image that cannot be decoded, or that exceeds
    /// a limit such as <see cref="PdfLimits.MaxImagePixels"/>, is skipped.
    /// </summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="options">The password and the resource limits to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="PdfLimitExceededException">The document exceeds a limit that ends it, such as <see cref="PdfLimits.MaxWorkPerDocument"/>.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<ColorImage> ExtractColorImages(Stream stream, AnalyzerOptions options)
    {
        ThrowIfNull(stream, options);
        return ExceptionBoundary.Run(() => ColorImageDecoder.Extract(stream, options.Password, options.Limits));
    }

    private static void ThrowIfNull(Stream stream, AnalyzerOptions options)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using ZeroDep.Abstractions;
using ZeroDep.Analysis;
using ZeroDep.Json;

namespace ZeroDep;

/// <summary>Public entry point for ZeroDep structural analysis features.</summary>
/// <remarks>
/// Exception contract: <see cref="Analyze"/> and <see cref="ToJson"/> report an unreadable document as a
/// rejected result rather than throwing. The extraction methods throw <see cref="PdfSyntaxException"/>
/// for an unreadable document; any other unexpected failure while reading the file is wrapped in a
/// <see cref="PdfSyntaxException"/> as well. A null <c>stream</c> throws
/// <see cref="ArgumentNullException"/>. <see cref="OutOfMemoryException"/> is never caught or wrapped.
/// </remarks>
public static class PdfAnalyzer
{
    /// <summary>Extracts the interactive form (AcroForm) fields from a PDF stream (Feature C).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static AcroFormReport ExtractForm(Stream stream, string? password = null)
    {
        ThrowIfNull(stream);
        return ExceptionBoundary.Run(() => AcroFormAnalyzer.Analyze(stream, password));
    }

    /// <summary>Computes effective DPI for every placed image and flags those below a threshold (Feature A).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="dpiThreshold">Images with effective DPI below this value are flagged.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<ImageDpiInfo> AnalyzeImageDpi(Stream stream, int dpiThreshold = AnalyzerOptions.DefaultDpiThreshold, string? password = null)
    {
        ThrowIfNull(stream);
        return ExceptionBoundary.Run(() => DpiAnalyzer.Analyze(stream, dpiThreshold, password));
    }

    /// <summary>Extracts positioned text runs (including the invisible OCR layer) from a PDF stream (Feature B).</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<TextRunInfo> ExtractText(Stream stream, string? password = null)
    {
        ThrowIfNull(stream);
        return ExceptionBoundary.Run(() => TextAnalyzer.Analyze(stream, password));
    }

    /// <summary>Extracts a simple line-grouped plain-text rendering of a PDF stream.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static string ExtractPlainText(Stream stream, string? password = null)
    {
        ThrowIfNull(stream);
        return ExceptionBoundary.Run(() => TextAnalyzer.GetPlainText(stream, password));
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
        => DocumentAnalyzer.Analyze(stream, dpiThreshold, password);

    /// <summary>Runs all features and serializes the result to the versioned JSON schema.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="indent">Whether to pretty-print the JSON.</param>
    /// <param name="dpiThreshold">Images with effective DPI below this value are flagged.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <returns>The JSON document. A document that cannot be read is serialized as a rejected result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The result could not be serialized.</exception>
    public static string ToJson(Stream stream, bool indent = false, int dpiThreshold = AnalyzerOptions.DefaultDpiThreshold, string? password = null)
    {
        DocumentAnalysis analysis = DocumentAnalyzer.Analyze(stream, dpiThreshold, password);
        return ExceptionBoundary.Run(() => DocumentJson.Write(analysis, indent));
    }

    /// <summary>Enumerates the embedded image XObjects in a PDF, with their declared size, filter, and raw bytes.</summary>
    /// <param name="stream">A readable PDF stream.</param>
    /// <param name="password">Password for an encrypted document (empty/default if null).</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="PdfSyntaxException">The document cannot be read.</exception>
    public static IReadOnlyList<PdfImageInfo> ExtractImages(Stream stream, string? password = null)
    {
        ThrowIfNull(stream);
        return ExceptionBoundary.Run(() => ImageExtractor.Extract(stream, password));
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
    {
        ThrowIfNull(stream);
        return ExceptionBoundary.Run(() => ColorImageDecoder.Extract(stream, password));
    }

    private static void ThrowIfNull(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
    }
}

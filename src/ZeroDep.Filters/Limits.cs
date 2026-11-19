namespace ZeroDep.Filters;

/// <summary>
/// Fixed limits of the stream and image decoders (ADR-0010 §3A). The caller-configurable budgets (decoded
/// stream size, image pixels and the decode working set derived from them) come from
/// <see cref="ZeroDep.Abstractions.PdfLimits"/>, passed to each decoder; only the structural invariants and
/// per-format constants stay here.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// How far an image's own declared size may exceed the PDF <c>/Width</c> or <c>/Height</c>: a factor of 4
    /// per dimension. Estimate: real files agree exactly; 4 tolerates a mis-declared resolution level.
    /// </summary>
    public const int MaxDimensionMismatchFactor = 4;

    /// <summary>LZW code table size: 4096 entries, the 12-bit code space (ISO 32000-2 §7.4.4).</summary>
    public const int LzwMaxTableEntries = 4096;

    /// <summary>
    /// Largest predictor <c>/Colors</c>: 32. Estimate: matches the 32-colourant DeviceN implementation limit
    /// (ISO 32000-2 Annex C); real predictor streams use 1 to 4.
    /// </summary>
    public const int MaxPredictorColors = 32;

    /// <summary>
    /// JPEG 2000 working set per tile sample (one pixel of one component), apart from code blocks: 7 bytes.
    /// Measured on a 1024x1024 single-tile codestream with 64x64 code blocks: 6.4 bytes per sample
    /// (coefficient arrays, wavelet lines, output canvas), rounded up.
    /// </summary>
    public const long JpxBytesPerSample = 7;

    /// <summary>
    /// JPEG 2000 working set per code block: 160 bytes. Measured on the same codestream with 1x1, 4x4 and 8x8
    /// code blocks: 166, 16.2 and 8.8 bytes per sample, which fits 6.4 + 160 / (code-block area). Tiny code
    /// blocks (a missing COD, or 1-pixel precincts) make this the dominant cost: at the default 1 GiB working set
    /// (4 bytes per pixel of the default pixel limit), 1 GiB / 167 B ≈ 6.4 M samples.
    /// </summary>
    public const long JpxBytesPerCodeBlock = 160;

    /// <summary>
    /// Largest number of scans decoded in one progressive JPEG: 64. Estimate: common encoder scan scripts use
    /// 6 to 12 scans; 64 leaves 5x headroom while bounding the work, since each scan may visit every block.
    /// </summary>
    public const int MaxProgressiveJpegScans = 64;

    /// <summary>
    /// Largest number of symbols one JBIG2 symbol dictionary, or one text region's symbol set, may hold:
    /// 65,536. Estimate: dictionaries in scanned documents hold hundreds to a few thousand symbols.
    /// </summary>
    public const int MaxJbig2Symbols = 1 << 16;

    /// <summary>
    /// Largest number of symbol instances placed by one JBIG2 text region: 1,048,576. Estimate: a dense A4
    /// page at 600 dpi has under 10,000 glyphs.
    /// </summary>
    public const int MaxJbig2TextInstances = 1 << 20;

    /// <summary>
    /// Largest number of referred-to segments one JBIG2 segment header may list: 4096. Estimate: real
    /// segments refer to at most a handful of dictionaries.
    /// </summary>
    public const int MaxJbig2ReferredSegments = 4096;
}

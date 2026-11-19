namespace ZeroDep.Filters;

/// <summary>
/// Fixed resource limits for the stream and image decoders (ADR-0010 §3A and §5.1, phase 1). Phase 2
/// replaces the budget values here with caller-supplied limits; the structural invariants stay fixed.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// Largest decoded output of one stream filter: 256 MiB. Estimate (ADR-0010 §5.1): ten times the largest
    /// content stream expected in office documents.
    /// </summary>
    public const long MaxDecodedStreamBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Largest image, in pixels: 2^28 (268,435,456). Estimate (ADR-0010 §5.1): an A4 page at 1200 dpi is
    /// 8.27 x 11.69 in x 1200^2 ≈ 139 M px, so 2^28 gives about 1.9x headroom.
    /// </summary>
    public const long MaxImagePixels = 1L << 28;

    /// <summary>
    /// Target peak allocation of one image decode: 1 GiB (the ADR-0010 phase-1 goal). Decoders whose working
    /// set per pixel is large derive a stricter pixel cap from it.
    /// </summary>
    public const long MaxDecodeWorkingSetBytes = 1L << 30;

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
    /// blocks (a missing COD, or 1-pixel precincts) make this the dominant cost: 1 GiB / 167 B ≈ 6.4 M samples.
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

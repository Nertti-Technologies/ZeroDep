namespace ZeroDep.Raster;

/// <summary>
/// Resource limits for glyph rasterization (ADR-0010 §3A, amendment 1). Each value is a labelled estimate.
/// These stay internal to the raster code: nothing on the analyzer path calls it, so they are not part of
/// the caller-facing limits.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// Glyph bitmap width or height in pixels (8192). <b>Estimate</b> (ADR-0010 §5.1): a 1000 pt glyph at
    /// 600 dpi is 8333 px; real glyphs are far smaller. At the cap the coverage buffer (4 bytes per pixel)
    /// plus the 8-bit bitmap is 5 × 8192² bytes = 320 MiB, within the 1 GiB per-image target.
    /// </summary>
    public const int MaxGlyphBitmapEdge = 8192;

    /// <summary>
    /// Extent of an outline's points, control points included, in device pixels (4 × 8192). <b>Estimate</b>:
    /// rejecting such outlines before flattening keeps curve subdivision bounded; a curve whose control points
    /// lie that far out cannot fit the bitmap limit anyway.
    /// </summary>
    public const double MaxOutlineExtent = 4.0 * MaxGlyphBitmapEdge;

    /// <summary>
    /// Flattened outline points per glyph (4,000,000, i.e. 64 MB of coordinates). <b>Estimate</b>: a
    /// detailed glyph flattens to a few thousand points.
    /// </summary>
    public const long MaxFlattenedPoints = 4_000_000;

    /// <summary>Vertical supersampling factor (64). <b>Estimate</b>: 8 is the default; 64 gives 6-bit coverage.</summary>
    public const int MaxSubSamples = 64;

    /// <summary>
    /// Scanline work per glyph: rows × subsamples × edges (2 × 10^9). <b>Estimate</b>: a detailed glyph of
    /// 2,000 edges at the bitmap limit with 8 subsamples is about 1.3 × 10^8; at about 1 ns per edge test
    /// the cap bounds one glyph to a few seconds of CPU.
    /// </summary>
    public const long MaxScanlineWork = 2_000_000_000;
}

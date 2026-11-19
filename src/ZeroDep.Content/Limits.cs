namespace ZeroDep.Content;

/// <summary>
/// Resource limits for content-stream interpretation and font maps (ADR-0010 §3A). Each value is either a
/// spec limit or a labelled estimate. The page work budget and the font-map entry budget come from the caller's
/// <see cref="ZeroDep.Abstractions.PdfLimits"/> (<c>MaxWorkPerPage</c>, <c>MaxCMapEntries</c>); the structural
/// invariants and the per-unit costs stay fixed here.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// Nesting depth of array and dictionary operands in a content stream (256). <b>Estimate</b>: real
    /// operands nest at most two levels (a TJ array, an inline-image /DecodeParms); 256 recursive frames stay
    /// far below the 1 MB default thread stack.
    /// </summary>
    public const int MaxOperandDepth = 256;

    /// <summary>
    /// Content bytes charged as one work unit when a Form XObject runs (64). <b>Estimate</b>: the lexer's
    /// cost per byte of whitespace or comment is far below one operator's.
    /// </summary>
    public const int FormBytesPerWorkUnit = 64;

    /// <summary>
    /// Graphics-state (<c>q</c>) nesting kept per content stream (256); deeper <c>q</c> operators are
    /// ignored. <b>Estimate</b>: PDF 1.7 implementation limits (ISO 32000-1 Annex C) list 28 levels.
    /// </summary>
    public const int MaxGraphicsStateDepth = 256;

    /// <summary>
    /// Work units charged per font-map entry built (10), so at the default page budget the fonts loaded for one
    /// page hold at most 5 × 10^6 entries (about 300 MB at an estimated 60 bytes per entry). <b>Estimate</b>: inserting a map
    /// entry allocates a string and a hash slot, about ten simple operators' worth.
    /// </summary>
    public const int WorkPerMapEntry = 10;
}

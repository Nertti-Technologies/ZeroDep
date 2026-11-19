namespace ZeroDep.Color;

/// <summary>
/// Resource limits for colour-space, function and image-conversion work (ADR-0010 §3A). Each value is
/// either a spec limit or a labelled estimate. Phase 2 replaces the budgets here with values passed in
/// from the caller's limits; the structural invariants stay fixed.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// Colour-space nesting depth (8). <b>Estimate</b>: the spec nests at most about 3 levels
    /// (Indexed → ICCBased → Alternate); 8 leaves headroom while bounding recursion.
    /// </summary>
    public const int MaxColorSpaceDepth = 8;

    /// <summary>
    /// Colourants in one colour space or components per image sample (32). PDF 1.7 implementation limits
    /// (ISO 32000-1 Annex C) allow at most 32 DeviceN colourants.
    /// </summary>
    public const int MaxComponents = 32;

    /// <summary>Function nesting depth (16). <b>Estimate</b>: real tint transforms nest one or two levels.</summary>
    public const int MaxFunctionDepth = 16;

    /// <summary>
    /// Function objects parsed for one top-level function, counting every sub-function (4096). <b>Estimate</b>:
    /// a stitched gradient uses one sub-function per stop, rarely more than a few hundred. Bounds fan-out
    /// through shared sub-functions, which a visited set alone does not.
    /// </summary>
    public const int MaxFunctionNodes = 4096;

    /// <summary>Sampled-function inputs m (16). <b>Estimate</b>; evaluation visits 2^m grid corners.</summary>
    public const int MaxSampledInputs = 16;

    /// <summary>PostScript calculator brace nesting (100). <b>Estimate</b>.</summary>
    public const int MaxPostScriptBraceDepth = 100;

    /// <summary>PostScript calculator operand stack (100 entries), as ISO 32000 §7.10.5 specifies.</summary>
    public const int MaxPostScriptStack = 100;

    /// <summary>
    /// PostScript calculator steps per evaluation (1,000,000), including the element counts of
    /// <c>copy</c>/<c>index</c>/<c>roll</c>. <b>Estimate</b>: real programs run tens of operators.
    /// </summary>
    public const int MaxPostScriptSteps = 1_000_000;

    /// <summary>Highest Indexed <c>hival</c> (255), as ISO 32000-2 §8.6.6.3 specifies.</summary>
    public const int MaxIndexedHival = 255;

    /// <summary>
    /// Pixels per converted image (2^28 = 268,435,456). <b>Estimate</b> (ADR-0010 §5.1): an A4 page at
    /// 1200 dpi is about 139 M px. The RGB output is 3 bytes per pixel, so the peak allocation is at most
    /// 3 × 2^28 = 768 MiB, within the 1 GiB per-image target.
    /// </summary>
    public const long MaxImagePixels = 1L << 28;

    /// <summary>
    /// Tint-transform work per converted image (2 × 10^9 cost units, about one unit per function operation).
    /// <b>Estimate</b>: a 4-ink DeviceN photo with a 4-output sampled tint transform costs
    /// 2^4 × (4 + 4 + 1) = 144 units per distinct colour, so this admits about 13.9 M uncached colours; at a
    /// few ns per unit it bounds one image to seconds of CPU. Repeated sample tuples are cached, so only
    /// distinct colours are charged.
    /// </summary>
    public const long MaxTintWork = 2_000_000_000;

    /// <summary>Cached tint results per converted image (65,536). <b>Estimate</b>: covers every 16-bit grey level.</summary>
    public const int MaxTintCacheEntries = 65_536;
}

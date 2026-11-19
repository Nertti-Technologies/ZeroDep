namespace ZeroDep.Fonts;

/// <summary>
/// Resource limits for font-program parsing, glyph interpretation and hinting (ADR-0010 §3A, amendment 1).
/// Each value is either a spec limit or a labelled estimate. These stay internal to the font code: nothing
/// on the analyzer path calls it, so they are not part of the caller-facing limits.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// Work units per glyph (10^6): charstring bytes and operators, subroutine calls, composite components
    /// and their points, and hinter instructions including every call and loop iteration. <b>Estimate</b>
    /// (ADR-0010 §5.1): real glyph programs run hundreds to low thousands of operations. The same budget
    /// applies separately to each run of the <c>fpgm</c> and <c>prep</c> programs.
    /// </summary>
    public const long MaxWorkPerGlyph = 1_000_000;

    /// <summary>TrueType composite-glyph nesting depth (8). <b>Estimate</b>; real composites nest one or two levels.</summary>
    public const int MaxCompositeDepth = 8;

    /// <summary>CFF Type 2 subroutine nesting depth (10), as the Type 2 charstring format specifies (TN #5177).</summary>
    public const int MaxCffSubrDepth = 10;

    /// <summary>Type 1 subroutine nesting depth (30). <b>Estimate</b>; real fonts nest a few levels.</summary>
    public const int MaxType1SubrDepth = 30;

    /// <summary>TrueType hinter function-call depth (128). <b>Estimate</b>; real fonts nest a few levels.</summary>
    public const int MaxHinterCallDepth = 128;

    /// <summary>
    /// Headroom added to the hinter's declared <c>maxp.maxStackElements</c> (64), so slightly inaccurate
    /// fonts still hint; a deeper stack is a fault. <b>Estimate</b>.
    /// </summary>
    public const int HinterStackHeadroom = 64;
}

/// <summary>
/// The work counter for one glyph (or one hinting program run). <see cref="Charge"/> throws once the total
/// passes <see cref="Limits.MaxWorkPerGlyph"/>; callers catch at their public boundary and return an empty
/// outline (or fall back, for hinting).
/// </summary>
internal sealed class GlyphWork
{
    private long _used;

    /// <summary>True once the budget has been exceeded.</summary>
    public bool Exhausted { get; private set; }

    public void Charge(long units)
    {
        _used += units;
        if (_used > Limits.MaxWorkPerGlyph)
        {
            Exhausted = true;
            throw new System.InvalidOperationException($"Glyph exceeds the work limit of {Limits.MaxWorkPerGlyph}.");
        }
    }
}

namespace ZeroDep.Objects;

/// <summary>
/// Fixed resource limits for the object layer (ADR-0010 §3A, phase 1). Phase 2 replaces the budget values
/// here with caller-supplied limits; the structural invariants stay fixed.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// Maximum nesting depth of arrays and dictionaries inside one object, also applied to the decryption walk.
    /// Estimate: real files nest fewer than 10 levels; 256 frames of the recursive parser stay far below the
    /// 1 MB default thread stack.
    /// </summary>
    public const int MaxNestingDepth = 256;

    /// <summary>
    /// First window read when loading an indirect object that has no nearby <c>endobj</c>. 64 KiB, from
    /// ADR-0010 §5.2 item 6 (estimate: covers almost every non-stream object in one read).
    /// </summary>
    public const int InitialObjectWindow = 64 * 1024;

    /// <summary>
    /// Largest window one indirect object may need: the decoded-stream budget of 256 MiB (ADR-0010 §5.1,
    /// estimate: ten times the largest content stream expected in office documents).
    /// </summary>
    public const int MaxObjectWindow = 256 * 1024 * 1024;

    /// <summary>Largest byte width of one cross-reference stream <c>/W</c> field: 8 bytes, the range of a <see cref="long"/>.</summary>
    public const int MaxXrefFieldWidth = 8;
}

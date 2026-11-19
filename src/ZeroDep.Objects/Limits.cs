namespace ZeroDep.Objects;

/// <summary>
/// Fixed limits of the object layer (ADR-0010 §3A). The largest object window is the caller's
/// <see cref="ZeroDep.Abstractions.PdfLimits.MaxDecodedStreamBytes"/>, and object reads are charged to the
/// document work budget (<see cref="ResourceBudget"/>); only the structural invariants stay here.
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
    /// Largest window any limit can allow: the largest byte array the runtime supports (0x7FFFFFC7), since a
    /// window is read into one array.
    /// </summary>
    public const int MaxArrayLength = 0x7FFFFFC7;

    /// <summary>
    /// Bytes of object window charged as one unit of document work (64), as for Form XObject content. ADR-0010
    /// amendment 4; <b>estimate</b>: scanning and copying a byte costs far less than interpreting an operator.
    /// </summary>
    public const int ObjectBytesPerWorkUnit = 64;

    /// <summary>Largest byte width of one cross-reference stream <c>/W</c> field: 8 bytes, the range of a <see cref="long"/>.</summary>
    public const int MaxXrefFieldWidth = 8;
}

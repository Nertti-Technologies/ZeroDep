namespace ZeroDep.Analysis;

/// <summary>
/// Fixed resource limits for the analysis layer. Each value is a single constant so that a later
/// change can replace it with a caller-supplied budget without touching the enforcement points.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// Maximum number of AcroForm field nodes visited in one document: 100,000. <b>Estimate</b>: large
    /// real forms carry a few thousand fields, so this leaves well over an order of magnitude of
    /// headroom while bounding the walk to a fraction of a second.
    /// </summary>
    internal const int MaxFormFields = 100_000;
}

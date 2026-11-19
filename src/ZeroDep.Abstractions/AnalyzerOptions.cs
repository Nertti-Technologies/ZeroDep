using System;

namespace ZeroDep.Abstractions;

/// <summary>Immutable options controlling a ZeroDep analysis run.</summary>
public sealed class AnalyzerOptions
{
    /// <summary>The default DPI threshold below which an image is flagged (150).</summary>
    public const int DefaultDpiThreshold = 150;

    /// <summary>
    /// Effective DPI at or above which an image is considered acceptable.
    /// Images below this value are flagged. Defaults to <see cref="DefaultDpiThreshold"/>.
    /// </summary>
    public int DpiThreshold { get; init; } = DefaultDpiThreshold;

    /// <summary>
    /// Password used to authenticate an encrypted document. When <see langword="null"/>,
    /// the empty/default user password is attempted.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Maximum number of pages to process concurrently in batch scenarios.
    /// <c>0</c> (default) lets the engine choose based on available processors.
    /// <c>PdfAnalyzer</c> does not use this value; it reads each document on the calling thread.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>
    /// The resource budgets applied while reading the document. Defaults to <see cref="PdfLimits.Default"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public PdfLimits Limits
    {
        get => _limits;
        init => _limits = value ?? throw new ArgumentNullException(nameof(Limits));
    }

    private readonly PdfLimits _limits = PdfLimits.Default;
}

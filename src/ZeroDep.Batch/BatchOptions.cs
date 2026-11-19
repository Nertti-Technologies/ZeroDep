using System;
using ZeroDep.Abstractions;

namespace ZeroDep.Batch;

/// <summary>Configuration for a corpus batch run (ADR Feature G).</summary>
public sealed class BatchOptions
{
    /// <summary>The root directory scanned recursively for <c>*.pdf</c> files.</summary>
    public string InputDirectory { get; init; } = string.Empty;

    /// <summary>
    /// The directory where the resumable ledger, the publishable aggregate statistics, and (when
    /// enabled) the per-file verification JSON are written.
    /// </summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>
    /// The default for <see cref="MaxConcurrency"/>: the processor count, but at most 4 (ADR-0013). <b>Estimate</b>:
    /// one large book's analysis peaks at about 320 MB, so 4 at once stay near 1.3 GB, where one worker per core
    /// on a 16-core machine could reach about 5 GB.
    /// </summary>
    public static int DefaultMaxConcurrency => Math.Min(Environment.ProcessorCount, 4);

    /// <summary>The maximum number of files processed concurrently. Defaults to <see cref="DefaultMaxConcurrency"/>.</summary>
    public int MaxConcurrency { get; init; } = DefaultMaxConcurrency;

    /// <summary>The effective-DPI threshold below which images are flagged.</summary>
    public int DpiThreshold { get; init; } = AnalyzerOptions.DefaultDpiThreshold;

    /// <summary>An optional password applied to encrypted documents (empty/default when null).</summary>
    public string? Password { get; init; }

    /// <summary>
    /// When true, files already recorded in the ledger with a matching input hash are skipped, so an
    /// interrupted run resumes where it left off. A recorded outcome is reused only when it was produced
    /// with the same <see cref="DpiThreshold"/>, the same password presence and the same engine version.
    /// </summary>
    public bool Resume { get; init; } = true;

    /// <summary>
    /// Whether to write the full per-file §8.1 verification JSON for each processed file (keyed by
    /// its anonymized id). This output is internal and never part of the published artifact.
    /// </summary>
    public bool WritePerFileJson { get; init; } = true;

    /// <summary>The structural-classification thresholds.</summary>
    public ClassificationThresholds Thresholds { get; init; } = ClassificationThresholds.Default;
}

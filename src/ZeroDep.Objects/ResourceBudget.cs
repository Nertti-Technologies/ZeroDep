using System;
using System.IO;
using ZeroDep.Abstractions;

namespace ZeroDep.Objects;

/// <summary>
/// The per-document resource context (ADR-0010 option A3): the caller's <see cref="PdfLimits"/> plus the
/// document-wide work counter. <see cref="PdfFile"/> owns one instance and every layer that reads the
/// document receives it explicitly; there is no ambient or static state, so documents read on different
/// threads share nothing.
/// </summary>
/// <remarks>
/// One document is read on one thread at a time, so the counter is not synchronized. Once the document
/// budget is exceeded every later <see cref="Charge"/> throws again, so a breach cannot be lost in a
/// per-page catch: it surfaces at the next charge.
/// </remarks>
internal sealed class ResourceBudget
{
    private long _work;
    private bool _exceeded;

    /// <summary>Creates a budget with no per-byte allowance (for content run without a source file).</summary>
    public ResourceBudget(PdfLimits limits)
        : this(limits, 0)
    {
    }

    /// <summary>Creates the budget for a file of <paramref name="sourceLength"/> bytes (ADR-0011).</summary>
    public ResourceBudget(PdfLimits limits, long sourceLength)
    {
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        SourceLength = sourceLength;
        MaxWork = limits.DocumentWorkBudget(sourceLength);
    }

    /// <summary>The caller's limits for this document.</summary>
    public PdfLimits Limits { get; }

    /// <summary>The length of the source file in bytes; zero when there is none.</summary>
    public long SourceLength { get; }

    /// <summary>The document work budget: the base plus the per-byte allowance for <see cref="SourceLength"/>.</summary>
    public long MaxWork { get; }

    /// <summary>Work units charged to the document so far.</summary>
    public long Work => _work;

    /// <summary>Charges <paramref name="units"/> to the document work budget.</summary>
    /// <exception cref="PdfLimitExceededException">The total exceeds <see cref="MaxWork"/>.</exception>
    public void Charge(long units)
    {
        // Compared before adding, so the counter cannot overflow even when MaxWork saturates at long.MaxValue.
        if (_exceeded || units > MaxWork - _work)
        {
            _exceeded = true;
            // The limit name stays MaxWorkPerDocument: IsDocumentLimit and callers match on it.
            throw new PdfLimitExceededException(nameof(PdfLimits.MaxWorkPerDocument), MaxWork, LimitMessage());
        }

        _work += units;
    }

    private string LimitMessage()
    {
        string message = FormattableString.Invariant($"Document exceeds the work limit of {MaxWork}");
        if (MaxWork == Limits.MaxWorkPerDocument) return message + ".";
        return message + FormattableString.Invariant(
            $" ({Limits.MaxWorkPerDocument} base + {Limits.WorkPerSourceByte} per byte for {SourceLength} bytes).");
    }

    /// <summary>
    /// Whether <paramref name="ex"/> ends the whole document: the document work budget was exceeded. Per-page and
    /// per-image catches must let such an exception through instead of skipping the part.
    /// </summary>
    public static bool IsDocumentLimit(Exception ex)
        => AsLimitExceeded(ex) is { LimitName: nameof(PdfLimits.MaxWorkPerDocument) };

    /// <summary>
    /// The limit breach behind <paramref name="ex"/>, if any. The public decoders report a breach as an
    /// <see cref="InvalidDataException"/> whose inner exception is the <see cref="PdfLimitExceededException"/>;
    /// the document pipeline uses this to surface the breach under its own type again.
    /// </summary>
    public static PdfLimitExceededException? AsLimitExceeded(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is PdfLimitExceededException limit) return limit;
            if (e is not InvalidDataException && e is not PdfSyntaxException) return null;
        }

        return null;
    }
}

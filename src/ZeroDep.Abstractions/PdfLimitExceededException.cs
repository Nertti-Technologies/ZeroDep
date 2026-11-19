using System;

namespace ZeroDep.Abstractions;

/// <summary>
/// Thrown when a document needs more than a resource limit allows: one of the configurable
/// <see cref="PdfLimits"/> budgets, or a fixed structural limit such as a nesting depth. It derives from
/// <see cref="PdfSyntaxException"/>, so code that handles that type keeps working; the document analysis
/// reports it as <see cref="RejectionReason.ResourceLimitExceeded"/>.
/// </summary>
public sealed class PdfLimitExceededException : PdfSyntaxException
{
    /// <summary>Initializes a new instance.</summary>
    public PdfLimitExceededException()
        : this(string.Empty, 0, "A resource limit was exceeded.")
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">A description of the limit that was exceeded.</param>
    public PdfLimitExceededException(string message)
        : this(string.Empty, 0, message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">A description of the limit that was exceeded.</param>
    /// <param name="innerException">The underlying cause.</param>
    public PdfLimitExceededException(string message, Exception innerException)
        : this(string.Empty, 0, message, innerException)
    {
    }

    /// <summary>Initializes a new instance naming the limit and its value.</summary>
    /// <param name="limitName">
    /// The name of the limit: a <see cref="PdfLimits"/> property name such as
    /// <c>MaxDecodedStreamBytes</c>, or the name of a fixed structural limit such as <c>MaxNestingDepth</c>.
    /// </param>
    /// <param name="limit">The value of the limit that was exceeded.</param>
    /// <param name="message">A description of the limit that was exceeded.</param>
    public PdfLimitExceededException(string limitName, long limit, string message)
        : base(message)
    {
        LimitName = limitName ?? string.Empty;
        Limit = limit;
    }

    /// <summary>Initializes a new instance naming the limit and its value, with an inner exception.</summary>
    /// <param name="limitName">The name of the limit (see <see cref="LimitName"/>).</param>
    /// <param name="limit">The value of the limit that was exceeded.</param>
    /// <param name="message">A description of the limit that was exceeded.</param>
    /// <param name="innerException">The underlying cause.</param>
    public PdfLimitExceededException(string limitName, long limit, string message, Exception innerException)
        : base(message, innerException)
    {
        LimitName = limitName ?? string.Empty;
        Limit = limit;
    }

    /// <summary>
    /// The name of the limit: a <see cref="PdfLimits"/> property name (for example
    /// <c>MaxWorkPerDocument</c>), or the name of a fixed structural limit (for example
    /// <c>MaxNestingDepth</c>). Empty when not known.
    /// </summary>
    public string LimitName { get; }

    /// <summary>
    /// The value of the limit that was exceeded, or 0 when not known. For <c>MaxWorkPerDocument</c> it is the budget
    /// actually enforced for that file, <see cref="PdfLimits.DocumentWorkBudget(long)"/>: the base plus the per-byte
    /// allowance, which can be larger than the <see cref="PdfLimits.MaxWorkPerDocument"/> setting.
    /// </summary>
    public long Limit { get; }
}

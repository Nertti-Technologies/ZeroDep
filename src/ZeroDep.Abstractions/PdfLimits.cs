using System;

namespace ZeroDep.Abstractions;

/// <summary>
/// The resource budgets applied while reading one document (ADR-0010 §3B). A file that needs more than a
/// budget allows is rejected with <see cref="RejectionReason.ResourceLimitExceeded"/>, or the part that needs
/// it is skipped and its page marked <see cref="PageClassification.Incomplete"/>, so that every input
/// finishes in bounded time and memory.
/// </summary>
/// <remarks>
/// <para>
/// Every default is an <b>estimate</b>, chosen to admit large legitimate office and scanned documents with
/// headroom; raise a budget for unusually large files, or lower it to bound the cost per file more tightly.
/// Instances are immutable, so one instance can be shared across threads and documents.
/// </para>
/// <para>
/// Structural invariants such as nesting depths and the limits taken from the PDF specification are fixed
/// and are not part of this type.
/// </para>
/// </remarks>
public sealed class PdfLimits
{
    private const long DefaultMaxDecodedStreamBytes = 256L * 1024 * 1024;
    private const long DefaultMaxImagePixels = 1L << 28;
    private const long DefaultMaxWorkPerPage = 50_000_000;
    private const long DefaultMaxWorkPerDocument = 250_000_000;
    private const int DefaultMaxFormFields = 100_000;
    private const int DefaultMaxCMapEntries = 1_048_576;

    private readonly long _maxDecodedStreamBytes = DefaultMaxDecodedStreamBytes;
    private readonly long _maxImagePixels = DefaultMaxImagePixels;
    private readonly long _maxWorkPerPage = DefaultMaxWorkPerPage;
    private readonly long _maxWorkPerDocument = DefaultMaxWorkPerDocument;
    private readonly int _maxFormFields = DefaultMaxFormFields;
    private readonly int _maxCMapEntries = DefaultMaxCMapEntries;

    /// <summary>The default budgets. Every value is an estimate; see each property.</summary>
    public static PdfLimits Default { get; } = new PdfLimits();

    /// <summary>
    /// Largest decoded output of one stream, and the largest window read for one indirect object, in bytes.
    /// Default 256 MiB (268,435,456). <b>Estimate</b>: ten times the largest content stream expected in office
    /// documents.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public long MaxDecodedStreamBytes
    {
        get => _maxDecodedStreamBytes;
        init => _maxDecodedStreamBytes = Positive(value, nameof(MaxDecodedStreamBytes));
    }

    /// <summary>
    /// Largest image one decode may produce, in pixels; image decoders also derive their working-memory cap
    /// from it (4 bytes per pixel). Default 2^28 (268,435,456). <b>Estimate</b>: an A4 page at 1200 dpi is
    /// 8.27 × 11.69 in × 1200² ≈ 139 M pixels, so the default gives about 1.9× headroom and a peak of about
    /// 1 GiB per image.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public long MaxImagePixels
    {
        get => _maxImagePixels;
        init => _maxImagePixels = Positive(value, nameof(MaxImagePixels));
    }

    /// <summary>
    /// Work units one page's content may use: about one unit per content operator or operand, per shown string
    /// byte, per 64 bytes of each Form XObject run, and per ten font-map entries built. Default 50,000,000.
    /// <b>Estimate</b>: a dense text page uses about 10^5 units. A page over the budget is skipped and marked
    /// incomplete.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public long MaxWorkPerPage
    {
        get => _maxWorkPerPage;
        init => _maxWorkPerPage = Positive(value, nameof(MaxWorkPerPage));
    }

    /// <summary>
    /// Work units the whole document may use: the content work of every page in every analysis pass, plus one
    /// unit per 64 bytes read while loading indirect objects. Default 250,000,000. <b>Estimate</b>: five pages
    /// at the page budget; a 1,000-page text document at about 10^5 units per page uses 10^8 units per pass. A
    /// document over the budget is rejected with <see cref="RejectionReason.ResourceLimitExceeded"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public long MaxWorkPerDocument
    {
        get => _maxWorkPerDocument;
        init => _maxWorkPerDocument = Positive(value, nameof(MaxWorkPerDocument));
    }

    /// <summary>
    /// Largest number of interactive-form field nodes visited in one document. Default 100,000.
    /// <b>Estimate</b>: large real forms carry a few thousand fields.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxFormFields
    {
        get => _maxFormFields;
        init => _maxFormFields = (int)Positive(value, nameof(MaxFormFields));
    }

    /// <summary>
    /// Largest number of entries in one font's <c>/ToUnicode</c> CMap, and separately in its CIDFont <c>/W</c>
    /// array. Default 1,048,576 (2^20). <b>Estimate</b>: a CID font has at most 65,536 glyphs, so 16× headroom. A
    /// map over the budget is dropped and its page marked incomplete.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxCMapEntries
    {
        get => _maxCMapEntries;
        init => _maxCMapEntries = (int)Positive(value, nameof(MaxCMapEntries));
    }

    private static long Positive(long value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "A resource limit must be greater than zero.");
        }

        return value;
    }
}

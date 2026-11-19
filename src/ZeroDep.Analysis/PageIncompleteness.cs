using System;
using System.Collections.Generic;
using ZeroDep.Abstractions;

namespace ZeroDep.Analysis;

/// <summary>
/// Collects, per page, the parts that an analyzer skipped (ADR-0010 option C2), so the result can say that a
/// page is partial instead of dropping the part silently. Each analyzer records into the instance it is
/// given and knows nothing of the others; <see cref="PageClassifier"/> reads it when it builds each
/// <see cref="PageClassification"/>.
/// </summary>
internal sealed class PageIncompleteness
{
    /// <summary>The page content exceeded <see cref="PdfLimits.MaxWorkPerPage"/>.</summary>
    public const string PageWorkLimit = "PageWorkLimit";

    /// <summary>A content stream exceeded <see cref="PdfLimits.MaxDecodedStreamBytes"/> or another limit.</summary>
    public const string ContentLimitExceeded = "ContentLimitExceeded";

    /// <summary>The page content could not be decoded or interpreted.</summary>
    public const string ContentUnreadable = "ContentUnreadable";

    /// <summary>A font map exceeded <see cref="PdfLimits.MaxCMapEntries"/> and was dropped.</summary>
    public const string FontMapLimitExceeded = "FontMapLimitExceeded";

    // The fixed order in which codes are reported, so the output is deterministic whatever order the
    // analyzers ran in.
    private static readonly string[] Order = { PageWorkLimit, ContentLimitExceeded, ContentUnreadable, FontMapLimitExceeded };

    private readonly Dictionary<int, HashSet<string>> _byPage = new Dictionary<int, HashSet<string>>();

    /// <summary>Records that <paramref name="code"/> applies to the page at <paramref name="pageIndex"/>.</summary>
    public void Record(int pageIndex, string code)
    {
        if (!_byPage.TryGetValue(pageIndex, out HashSet<string>? codes))
        {
            codes = new HashSet<string>(StringComparer.Ordinal);
            _byPage[pageIndex] = codes;
        }

        codes.Add(code);
    }

    /// <summary>Records why a page's content was skipped, from the exception that ended it.</summary>
    public void RecordContentFailure(int pageIndex, Exception ex) => Record(pageIndex, ContentCode(ex));

    /// <summary>The codes recorded for a page, in the fixed order; empty when the page is complete.</summary>
    public IReadOnlyList<string> ReasonsFor(int pageIndex)
    {
        if (!_byPage.TryGetValue(pageIndex, out HashSet<string>? codes) || codes.Count == 0)
        {
            return Array.Empty<string>();
        }

        var ordered = new List<string>(codes.Count);
        foreach (string code in Order)
        {
            if (codes.Contains(code)) ordered.Add(code);
        }

        return ordered;
    }

    // The code for a failure that ended a page's content: the page budget, another limit, or anything else.
    private static string ContentCode(Exception ex)
    {
        if (ZeroDep.Objects.ResourceBudget.AsLimitExceeded(ex) is { } limit)
        {
            return limit.LimitName == nameof(PdfLimits.MaxWorkPerPage) ? PageWorkLimit : ContentLimitExceeded;
        }

        return ContentUnreadable;
    }
}

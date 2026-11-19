using System;
using System.Collections.Generic;

namespace ZeroDep.Abstractions;

/// <summary>
/// A single page's structural classification (ADR-0003): its <see cref="Class"/>, a 0–1
/// <see cref="Confidence"/> (the sole carrier of "uncertain" — low confidence means escalate), and the
/// <see cref="PageSignals"/> behind it. Additive; the document-level category is a roll-up of these.
/// </summary>
public sealed class PageClassification
{
    /// <summary>The zero-based page index.</summary>
    public int PageIndex { get; init; }

    /// <summary>The assigned structural class.</summary>
    public PageContentClass Class { get; init; }

    /// <summary>Confidence in the assigned class, 0–1. Low confidence signals "uncertain — escalate".</summary>
    public double Confidence { get; init; }

    /// <summary>The deterministic signals the class was derived from.</summary>
    public PageSignals Signals { get; init; } = new PageSignals();

    /// <summary>
    /// Whether part of the page could not be read, so its text, images or signals may be partial. The page is
    /// still reported; <see cref="IncompleteReasons"/> says what was skipped.
    /// </summary>
    public bool Incomplete { get; init; }

    /// <summary>
    /// Stable short codes for what was skipped on the page, in a fixed order, without duplicates; empty when
    /// the page is complete. Current codes: <c>PageWorkLimit</c> (the page content exceeded
    /// <see cref="PdfLimits.MaxWorkPerPage"/>), <c>ContentLimitExceeded</c> (a content stream exceeded
    /// <see cref="PdfLimits.MaxDecodedStreamBytes"/> or another limit), <c>ContentUnreadable</c> (the page
    /// content could not be decoded or interpreted), <c>FontMapLimitExceeded</c> (a font map exceeded
    /// <see cref="PdfLimits.MaxCMapEntries"/> and was dropped). New codes may be added in later versions.
    /// </summary>
    public IReadOnlyList<string> IncompleteReasons { get; init; } = Array.Empty<string>();
}

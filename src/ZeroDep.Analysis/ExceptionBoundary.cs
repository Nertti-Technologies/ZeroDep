using System;
using ZeroDep.Abstractions;
using ZeroDep.Objects;

namespace ZeroDep.Analysis;

/// <summary>
/// The single place that decides which exceptions may leave the public analysis entry points. Every
/// non-fatal failure while reading a document becomes a <see cref="PdfSyntaxException"/>, so a caller
/// only has to handle one documented type. <see cref="OutOfMemoryException"/> is never translated.
/// </summary>
internal static class ExceptionBoundary
{
    /// <summary>
    /// True for an exception that is translated at the boundary: anything except an
    /// <see cref="OutOfMemoryException"/>, which must reach the host unchanged.
    /// </summary>
    public static bool IsTranslatable(Exception exception) => exception is not OutOfMemoryException;

    /// <summary>
    /// True for an exception that a per-page or per-image catch may absorb (recording the part as incomplete):
    /// anything translatable except a breach of the document work budget, which must end the whole document.
    /// </summary>
    public static bool IsPartRecoverable(Exception exception)
        => IsTranslatable(exception) && !ResourceBudget.IsDocumentLimit(exception);

    /// <summary>
    /// Runs <paramref name="body"/> and rethrows any translatable exception other than a
    /// <see cref="PdfSyntaxException"/> as a <see cref="PdfSyntaxException"/> that wraps it.
    /// </summary>
    public static T Run<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (PdfSyntaxException)
        {
            throw;
        }
        catch (Exception ex) when (ResourceBudget.AsLimitExceeded(ex) is { } limit)
        {
            throw new PdfLimitExceededException(limit.LimitName, limit.Limit, limit.Message, ex);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw Translate(ex);
        }
    }

    /// <summary>Wraps an unexpected failure as a <see cref="PdfSyntaxException"/> naming its type.</summary>
    public static PdfSyntaxException Translate(Exception exception)
        => new PdfSyntaxException($"Malformed document: {exception.GetType().Name}: {exception.Message}", exception);
}

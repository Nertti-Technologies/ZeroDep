using System;

namespace ZeroDep.Fonts;

/// <summary>
/// The exception boundary for font loading (ADR-0010 §5.2-7): a malformed program surfaces as
/// <see cref="NotSupportedException"/>, never as an index or arithmetic fault from inside a parser.
/// </summary>
internal static class FontFault
{
    /// <summary>Runs a loader, translating any non-fatal fault into <see cref="NotSupportedException"/>.</summary>
    public static T Guard<T>(Func<T> load)
    {
        try
        {
            return load();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not NotSupportedException and not ArgumentNullException)
        {
            throw new NotSupportedException("Malformed font program.", ex);
        }
    }

    /// <summary>
    /// Returns a usable em size: the value when it is a whole number of font units in 1..65535, else 1000
    /// (the Type 1 / CFF default), so later divisions by it stay finite.
    /// </summary>
    public static int SanitizeUnitsPerEm(double unitsPerEm)
        => unitsPerEm >= 1 && unitsPerEm <= 65535 ? (int)unitsPerEm : 1000;
}

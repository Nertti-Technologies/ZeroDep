using System;
using System.Globalization;

namespace ZeroDep.Cli;

/// <summary>Parses command-line values.</summary>
internal static class CliArguments
{
    /// <summary>
    /// Whether a positional argument is the DPI threshold rather than the input path. An argument that
    /// names an existing file is always the path, even when its name is a number.
    /// </summary>
    public static bool TryParseThreshold(string arg, Func<string, bool> fileExists, out int value)
    {
        value = 0;
        return !fileExists(arg) && TryParseInt(arg, out value);
    }

    /// <summary>Parses an integer option value the same way under every culture.</summary>
    public static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}

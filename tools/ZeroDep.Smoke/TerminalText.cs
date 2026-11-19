using System.Text;

namespace ZeroDep.Smoke;

/// <summary>
/// Cleans text before it reaches the terminal. File names, rejection details and failure lines can quote document
/// content, and a PDF name can carry ESC through a <c>#1B</c> escape.
/// </summary>
/// <remarks>
/// The same rule as the CLI's <c>ConsoleText.Clean</c> (src/ZeroDep.Cli). This tool is a separate executable that
/// runs the CLI as a process and cannot reference its internals, so the rule is repeated here; keep the two alike.
/// </remarks>
internal static class TerminalText
{
    /// <summary>Replaces C0 (U+0000–U+001F), DEL (U+007F) and C1 (U+0080–U+009F) control characters with spaces.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < ' ' || (c >= '\u007f' && c <= '\u009f') ? ' ' : c);
        return sb.ToString();
    }
}

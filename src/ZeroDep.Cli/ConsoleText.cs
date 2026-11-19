using System.Text;
using ZeroDep.Abstractions;

namespace ZeroDep.Cli;

/// <summary>
/// Formats PDF-derived values for the terminal. Every string taken from a document passes through
/// <see cref="Clean"/>, so a crafted name or value cannot send terminal escape sequences.
/// </summary>
/// <remarks>
/// The release smoke gate repeats the <see cref="Clean"/> rule in tools/ZeroDep.Smoke/TerminalText.cs (it runs this
/// CLI as a process and cannot reference it); keep the two alike.
/// </remarks>
internal static class ConsoleText
{
    /// <summary>
    /// Replaces control characters with spaces: C0 (U+0000–U+001F), DEL (U+007F) and C1 (U+0080–U+009F,
    /// which includes the single-character control-sequence introducer U+009B).
    /// </summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s!.Length);
        foreach (char c in s) sb.Append(IsControl(c) ? ' ' : c);
        return sb.ToString();
    }

    private static bool IsControl(char c) => c < '\u0020' || (c >= '\u007f' && c <= '\u009f');

    /// <summary>Shortens <paramref name="s"/> to <paramref name="max"/> characters, marking the cut.</summary>
    public static string Truncate(string s, int max) => s.Length > max ? s.Substring(0, max) + "…" : s;

    /// <summary>One report line for an image.</summary>
    public static string ImageLine(ImageDpiInfo image)
    {
        string flag = image.IsBelowThreshold ? "  [LOW]" : "";
        return $"  p{image.PageIndex} {Clean(image.ResourceName),-10} {image.PixelWidth}x{image.PixelHeight}px  eff {image.EffectiveDpi:F0} DPI{flag}";
    }

    /// <summary>One report line for a form field.</summary>
    public static string FieldLine(FormFieldInfo field)
    {
        string value = field.IsChecked is bool b ? (b ? "[X]" : "[ ]") : Truncate(Clean(field.Value), 60);
        string label = string.IsNullOrEmpty(field.Label) ? "" : "   (" + Truncate(Clean(field.Label), 80) + ")";
        return $"  [{Clean(field.FieldType),-3}] {Clean(field.FullyQualifiedName)} = {value}{label}";
    }

    /// <summary>
    /// The report line for a document the analyzer rejected: a resource limit it exceeds, or a structure it
    /// cannot read. The message can quote document content, so it is cleaned like any other value.
    /// </summary>
    public static string RejectionLine(PdfSyntaxException exception)
    {
        string reason = exception is PdfLimitExceededException ? "resource limit exceeded" : "unreadable document";
        return $"rejected ({reason}): {Truncate(Clean(exception.Message), 200)}";
    }
}

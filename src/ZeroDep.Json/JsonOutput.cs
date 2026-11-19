using System.IO;
using System.Text;

namespace ZeroDep.Json;

/// <summary>
/// The one definition of how streamed JSON reaches a byte stream (ADR-0013), shared by
/// <c>PdfAnalyzer.WriteJson</c> and the batch runner's per-file output so the two cannot drift apart.
/// </summary>
internal static class JsonOutput
{
    /// <summary>UTF-8 without a byte-order mark, as JSON consumers expect (RFC 8259 §8.1).</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Characters buffered before the encoder runs: 64 Ki (128 KiB of UTF-16). <b>Estimate</b>: large enough that
    /// the per-flush cost is negligible next to the hundreds of megabytes a large book produces, and well under 1%
    /// of the about 320 MB analysis-only peak measured for such a book (ADR-0013 §1.1).
    /// </summary>
    public const int BufferChars = 64 * 1024;

    /// <summary>A writer over <paramref name="output"/> that leaves it open when disposed.</summary>
    public static StreamWriter CreateWriter(Stream output)
        => new StreamWriter(output, Utf8NoBom, BufferChars, leaveOpen: true);
}

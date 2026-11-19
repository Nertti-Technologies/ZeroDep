using System;
using System.IO;
using ZeroDep.Abstractions;

namespace ZeroDep.Filters;

/// <summary>Decodes PDF <c>/ASCIIHexDecode</c> data (ISO 32000-2 §7.4.2).</summary>
public static class AsciiHexDecode
{
    /// <summary>Decodes hex-encoded data, within the default <see cref="PdfLimits"/>, ignoring whitespace, ending at '&gt;'.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidDataException">The output exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/> (256 MiB by default).</exception>
    public static byte[] Decode(byte[] input) => Decode(input, PdfLimits.Default);

    /// <summary>Decodes hex-encoded data, within <paramref name="limits"/>, ignoring whitespace, ending at '&gt;'.</summary>
    /// <param name="input">The encoded bytes.</param>
    /// <param name="limits">The limits to apply; <see cref="PdfLimits.MaxDecodedStreamBytes"/> bounds the output.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="limits"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The output exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/>; the inner exception is a
    /// <see cref="PdfLimitExceededException"/> naming the limit.
    /// </exception>
    public static byte[] Decode(byte[] input, PdfLimits limits)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (limits is null) throw new ArgumentNullException(nameof(limits));
        return DecoderBoundary.Run(() => DecodeCore(input, limits), "ASCIIHexDecode");
    }

    private static byte[] DecodeCore(byte[] input, PdfLimits limits)
    {
        var output = new BoundedOutput(input.Length / 2, limits);
        int high = -1;
        foreach (byte b in input)
        {
            if (b == (byte)'>') break;
            int v = HexValue(b);
            if (v < 0) continue;
            if (high < 0) high = v;
            else { output.WriteByte((byte)((high << 4) | v)); high = -1; }
        }
        if (high >= 0) output.WriteByte((byte)(high << 4)); // odd trailing nibble padded with 0
        return output.ToArray();
    }

    private static int HexValue(int b)
    {
        if (b >= '0' && b <= '9') return b - '0';
        if (b >= 'A' && b <= 'F') return b - 'A' + 10;
        if (b >= 'a' && b <= 'f') return b - 'a' + 10;
        return -1;
    }
}

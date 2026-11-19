using System;
using System.Collections.Generic;
using System.IO;
using ZeroDep.Abstractions;

namespace ZeroDep.Filters;

/// <summary>
/// Decodes PDF <c>/LZWDecode</c> data — variable-width LZW (9–12 bit codes) with the PDF default
/// EarlyChange of 1 (ISO 32000-2 §7.4.4.2).
/// </summary>
public static class LzwDecode
{
    private const int ClearTable = 256;
    private const int EndOfData = 257;

    /// <summary>Decodes LZW data and applies an optional PNG/TIFF predictor, within the default <see cref="PdfLimits"/>.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="predictor">The <c>/Predictor</c> value (1 = none).</param>
    /// <param name="colors">The <c>/Colors</c> value (samples per pixel).</param>
    /// <param name="bitsPerComponent">The <c>/BitsPerComponent</c> value.</param>
    /// <param name="columns">The <c>/Columns</c> value (samples per row).</param>
    /// <param name="earlyChange">The <c>/EarlyChange</c> value (default 1).</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The predictor parameters are invalid, or the output exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/>
    /// (256 MiB by default).
    /// </exception>
    public static byte[] Decode(byte[] input, int predictor, int colors, int bitsPerComponent, int columns, int earlyChange)
        => Decode(input, PdfLimits.Default, predictor, colors, bitsPerComponent, columns, earlyChange);

    /// <summary>Decodes LZW data and applies an optional PNG/TIFF predictor, within <paramref name="limits"/>.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="limits">The limits to apply; <see cref="PdfLimits.MaxDecodedStreamBytes"/> bounds the output.</param>
    /// <param name="predictor">The <c>/Predictor</c> value (1 = none).</param>
    /// <param name="colors">The <c>/Colors</c> value (samples per pixel).</param>
    /// <param name="bitsPerComponent">The <c>/BitsPerComponent</c> value.</param>
    /// <param name="columns">The <c>/Columns</c> value (samples per row).</param>
    /// <param name="earlyChange">The <c>/EarlyChange</c> value (1 in PDF files that omit it).</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="limits"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The predictor parameters are invalid; or the output exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/>, in
    /// which case the inner exception is a <see cref="PdfLimitExceededException"/> naming the limit.
    /// </exception>
    public static byte[] Decode(byte[] input, PdfLimits limits, int predictor, int colors, int bitsPerComponent, int columns, int earlyChange)
    {
        byte[] raw = Decode(input, limits, earlyChange);
        return predictor <= 1
            ? raw
            : DecoderBoundary.Run(() => Predictor.Apply(raw, predictor, colors, bitsPerComponent, columns), "LZWDecode");
    }

    /// <summary>Decodes LZW data (no predictor), within the default <see cref="PdfLimits"/>.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="earlyChange">The <c>/EarlyChange</c> value (default 1).</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidDataException">The output exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/> (256 MiB by default).</exception>
    public static byte[] Decode(byte[] input, int earlyChange = 1) => Decode(input, PdfLimits.Default, earlyChange);

    /// <summary>Decodes LZW data (no predictor), within <paramref name="limits"/>.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="limits">The limits to apply; <see cref="PdfLimits.MaxDecodedStreamBytes"/> bounds the output.</param>
    /// <param name="earlyChange">The <c>/EarlyChange</c> value (default 1).</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="limits"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The output exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/>; the inner exception is a
    /// <see cref="PdfLimitExceededException"/> naming the limit.
    /// </exception>
    public static byte[] Decode(byte[] input, PdfLimits limits, int earlyChange = 1)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (limits is null) throw new ArgumentNullException(nameof(limits));
        return DecoderBoundary.Run(() => DecodeCore(input, earlyChange, limits), "LZWDecode");
    }

    private static byte[] DecodeCore(byte[] input, int earlyChange, PdfLimits limits)
    {
        var output = new BoundedOutput(input.Length * 3L, limits);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i });
            table.Add(Array.Empty<byte>()); // 256 ClearTable placeholder
            table.Add(Array.Empty<byte>()); // 257 EndOfData placeholder
        }
        Reset();

        int codeWidth = 9;
        int bitBuffer = 0;
        int bitCount = 0;
        int pos = 0;
        byte[]? previous = null;

        int ReadCode()
        {
            while (bitCount < codeWidth)
            {
                if (pos >= input.Length) return -1;
                bitBuffer = (bitBuffer << 8) | input[pos++];
                bitCount += 8;
            }
            bitCount -= codeWidth;
            return (bitBuffer >> bitCount) & ((1 << codeWidth) - 1);
        }

        int code;
        while ((code = ReadCode()) != -1)
        {
            if (code == ClearTable)
            {
                Reset();
                codeWidth = 9;
                previous = null;
                continue;
            }
            if (code == EndOfData) break;

            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
            }
            else if (previous is not null)
            {
                entry = new byte[previous.Length + 1];
                Array.Copy(previous, entry, previous.Length);
                entry[previous.Length] = previous[0];
            }
            else
            {
                break; // invalid stream
            }

            output.Write(entry, 0, entry.Length);

            // The code space is 12 bits (4096 entries); a full table stays as it is until ClearTable.
            if (previous is not null && table.Count < Limits.LzwMaxTableEntries)
            {
                var added = new byte[previous.Length + 1];
                Array.Copy(previous, added, previous.Length);
                added[previous.Length] = entry[0];
                table.Add(added);
            }

            previous = entry;

            if (table.Count + earlyChange >= (1 << codeWidth) && codeWidth < 12)
            {
                codeWidth++;
            }
        }

        return output.ToArray();
    }
}

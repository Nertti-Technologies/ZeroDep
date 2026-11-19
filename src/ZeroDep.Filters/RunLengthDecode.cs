using System;
using System.IO;
using ZeroDep.Abstractions;

namespace ZeroDep.Filters;

/// <summary>Decodes PDF <c>/RunLengthDecode</c> data (ISO 32000-2 §7.4.5).</summary>
public static class RunLengthDecode
{
    /// <summary>Decodes run-length data, within the default <see cref="PdfLimits"/>; 128 is the end-of-data marker.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidDataException">The output exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/> (256 MiB by default).</exception>
    public static byte[] Decode(byte[] input) => Decode(input, PdfLimits.Default);

    /// <summary>Decodes run-length data, within <paramref name="limits"/>; 128 is the end-of-data marker.</summary>
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
        return DecoderBoundary.Run(() => DecodeCore(input, limits), "RunLengthDecode");
    }

    private static byte[] DecodeCore(byte[] input, PdfLimits limits)
    {
        var output = new BoundedOutput(input.Length * 2L, limits);
        int i = 0;
        while (i < input.Length)
        {
            int length = input[i++];
            if (length == 128) break; // EOD
            if (length < 128)
            {
                int count = Math.Min(length + 1, input.Length - i);
                output.Write(input, i, count);
                i += count;
            }
            else if (i < input.Length)
            {
                byte value = input[i++];
                output.WriteRepeated(value, 257 - length);
            }
        }
        return output.ToArray();
    }
}

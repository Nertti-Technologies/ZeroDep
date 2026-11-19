using System;
using System.IO;

namespace ZeroDep.Filters;

/// <summary>Decodes PDF <c>/RunLengthDecode</c> data (ISO 32000-2 §7.4.5).</summary>
public static class RunLengthDecode
{
    /// <summary>Decodes run-length data; 128 is the end-of-data marker.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidDataException">The output exceeds 256 MiB.</exception>
    public static byte[] Decode(byte[] input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        return DecoderBoundary.Run(() => DecodeCore(input), "RunLengthDecode");
    }

    private static byte[] DecodeCore(byte[] input)
    {
        var output = new BoundedOutput(input.Length * 2);
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

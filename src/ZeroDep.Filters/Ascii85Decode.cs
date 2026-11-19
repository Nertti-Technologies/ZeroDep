using System;
using System.Collections.Generic;
using System.IO;

namespace ZeroDep.Filters;

/// <summary>Decodes PDF <c>/ASCII85Decode</c> data (ISO 32000-2 §7.4.3).</summary>
public static class Ascii85Decode
{
    /// <summary>Decodes base-85 data; honors the 'z' zero shorthand and the '~&gt;' terminator.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidDataException">A group exceeds 2^32 - 1, or 'z' appears inside a group.</exception>
    public static byte[] Decode(byte[] input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        return DecoderBoundary.Run(() => DecodeCore(input), "ASCII85Decode");
    }

    private static byte[] DecodeCore(byte[] input)
    {
        var output = new List<byte>();
        long tuple = 0;
        int count = 0;
        int start = 0;
        if (input.Length >= 2 && input[0] == (byte)'<' && input[1] == (byte)'~') start = 2; // optional <~ prefix

        for (int i = start; i < input.Length; i++)
        {
            int c = input[i];
            if (c == '~') break; // ~> end of data
            if (c == 'z')
            {
                // 'z' stands for a whole group of zeros, so it may not appear inside a group (ISO 32000-2 §7.4.3).
                if (count != 0) throw new InvalidDataException("ASCII85 'z' inside a group.");
                output.Add(0); output.Add(0); output.Add(0); output.Add(0);
                continue;
            }
            if (c < '!' || c > 'u') continue; // skip whitespace / invalid

            tuple = (tuple * 85) + (uint)(c - '!');
            count++;
            if (count == 5)
            {
                if (tuple > uint.MaxValue) throw new InvalidDataException("ASCII85 group exceeds 2^32 - 1.");
                output.Add((byte)(tuple >> 24));
                output.Add((byte)(tuple >> 16));
                output.Add((byte)(tuple >> 8));
                output.Add((byte)tuple);
                tuple = 0;
                count = 0;
            }
        }

        if (count > 0)
        {
            for (int k = count; k < 5; k++) tuple = (tuple * 85) + 84; // pad with 'u'
            if (tuple > uint.MaxValue) throw new InvalidDataException("ASCII85 group exceeds 2^32 - 1.");
            for (int k = 0; k < count - 1; k++) output.Add((byte)(tuple >> (24 - (k * 8))));
        }
        return output.ToArray();
    }
}

using System;
using System.IO;

namespace ZeroDep.Filters;

/// <summary>
/// Reverses PNG and TIFF predictors applied before Flate/LZW compression
/// (ISO 32000-2 §7.4.4.4, "Predictor functions").
/// </summary>
internal static class Predictor
{
    /// <summary>Reverses the predictor on <paramref name="data"/>; predictor &lt; 2 returns the data unchanged.</summary>
    /// <exception cref="InvalidDataException">The predictor parameters are outside ISO 32000-2 Table 8.</exception>
    public static byte[] Apply(byte[] data, int predictor, int colors, int bitsPerComponent, int columns)
    {
        if (predictor < 2) return data;
        Validate(predictor, colors, bitsPerComponent, columns);
        if (data.Length == 0) return data;

        int bytesPerPixel = Math.Max(1, (colors * bitsPerComponent + 7) / 8);
        long rowBits = (long)colors * bitsPerComponent * columns;
        if (rowBits > (long)int.MaxValue * 8) throw new InvalidDataException("Predictor row length is out of range.");
        int rowLength = (int)((rowBits + 7) / 8);

        return predictor == 2
            ? ApplyTiff(data, colors, bitsPerComponent, rowLength)
            : ApplyPng(data, bytesPerPixel, rowLength);
    }

    // ISO 32000-2 Table 8: Predictor 2 (TIFF) or 10..15 (PNG); BitsPerComponent 1, 2, 4, 8 or 16;
    // Colors and Columns at least 1.
    private static void Validate(int predictor, int colors, int bitsPerComponent, int columns)
    {
        if (predictor != 2 && (predictor < 10 || predictor > 15))
        {
            throw new InvalidDataException($"Unsupported /Predictor {predictor}.");
        }
        if (colors < 1 || colors > Limits.MaxPredictorColors)
        {
            throw new InvalidDataException($"Predictor /Colors {colors} is outside 1..{Limits.MaxPredictorColors}.");
        }
        if (bitsPerComponent != 1 && bitsPerComponent != 2 && bitsPerComponent != 4 && bitsPerComponent != 8 && bitsPerComponent != 16)
        {
            throw new InvalidDataException($"Predictor /BitsPerComponent {bitsPerComponent} is not 1, 2, 4, 8 or 16.");
        }
        if (columns < 1) throw new InvalidDataException($"Predictor /Columns {columns} is below 1.");
    }

    private static byte[] ApplyPng(byte[] data, int bytesPerPixel, int stride)
    {
        // Each PNG-predicted row is prefixed with a one-byte filter type.
        int rows = (int)(data.Length / ((long)stride + 1));
        if (rows == 0) return Array.Empty<byte>();
        var output = new byte[rows * stride];
        var previous = new byte[stride];

        int inPos = 0;
        int outPos = 0;
        for (int r = 0; r < rows; r++)
        {
            int filter = data[inPos++];
            var current = new byte[stride];
            Array.Copy(data, inPos, current, 0, stride);
            inPos += stride;

            for (int i = 0; i < stride; i++)
            {
                int a = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                int b = previous[i];
                int c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                int x = current[i];
                int value;
                switch (filter)
                {
                    case 0: value = x; break;                       // None
                    case 1: value = x + a; break;                   // Sub
                    case 2: value = x + b; break;                   // Up
                    case 3: value = x + ((a + b) >> 1); break;      // Average
                    case 4: value = x + Paeth(a, b, c); break;      // Paeth
                    default: value = x; break;
                }
                current[i] = (byte)(value & 0xFF);
            }

            Array.Copy(current, 0, output, outPos, stride);
            outPos += stride;
            previous = current;
        }
        return output;
    }

    private static byte[] ApplyTiff(byte[] data, int colors, int bitsPerComponent, int stride)
    {
        // TIFF predictor 2 is defined per-component; this implementation covers the common 8-bit case.
        if (bitsPerComponent != 8) return data;

        var output = (byte[])data.Clone();
        int rows = stride == 0 ? 0 : data.Length / stride;
        for (int r = 0; r < rows; r++)
        {
            int rowStart = r * stride;
            for (int i = colors; i < stride; i++)
            {
                output[rowStart + i] = (byte)((output[rowStart + i] + output[rowStart + i - colors]) & 0xFF);
            }
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}

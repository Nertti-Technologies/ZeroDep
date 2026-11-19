using System;
using System.Collections.Generic;
using System.IO;
using ZeroDep.Abstractions;
using ZeroDep.Filters;

namespace ZeroDep.Color;

/// <summary>
/// Normalizes decoded image samples to RGB using a resolved <see cref="PdfColorSpace"/> (ADR-0004 §2.2).
/// Honors the image's bits-per-component (1/2/4/8/16, row-byte-aligned) and the optional <c>/Decode</c>
/// array. Output is a 3-component <see cref="RasterImage"/> (RGB), or a 1-component grayscale raster for
/// DeviceGray/CalGray. Pure-BCL and deterministic.
/// </summary>
public static class ColorConverter
{
    /// <summary>
    /// Normalizes packed sample data (native bit depth, rows byte-aligned) to an RGB/Gray raster, within the
    /// default <see cref="PdfLimits"/>.
    /// </summary>
    /// <param name="data">The decoded, still-packed image samples.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="components">Components per pixel (must match the colour space, except 16-bit reductions).</param>
    /// <param name="bitsPerComponent">Bits per component: 1, 2, 4, 8, or 16.</param>
    /// <param name="colorSpace">The resolved colour space.</param>
    /// <param name="decode">The image's <c>/Decode</c> array, or null for the colour space default.</param>
    /// <exception cref="InvalidDataException">
    /// The image exceeds the fixed pixel or tint-work limits, has more than 32 components, or uses a
    /// bit depth above 16.
    /// </exception>
    public static RasterImage ToRgb(
        byte[] data, int width, int height, int components, int bitsPerComponent, PdfColorSpace colorSpace, double[]? decode = null)
        => ToRgb(data, PdfLimits.Default, width, height, components, bitsPerComponent, colorSpace, decode);

    /// <summary>
    /// Normalizes packed sample data (native bit depth, rows byte-aligned) to an RGB/Gray raster, within
    /// <paramref name="limits"/>.
    /// </summary>
    /// <param name="data">The decoded, still-packed image samples.</param>
    /// <param name="limits">The limits to apply; the image may have at most <see cref="PdfLimits.MaxImagePixels"/> pixels.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="components">Components per pixel (must match the colour space, except 16-bit reductions).</param>
    /// <param name="bitsPerComponent">Bits per component: 1, 2, 4, 8, or 16.</param>
    /// <param name="colorSpace">The resolved colour space.</param>
    /// <param name="decode">The image's <c>/Decode</c> array, or null for the colour space default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/>, <paramref name="limits"/> or <paramref name="colorSpace"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The image has more than 32 components or uses a bit depth above 16; or it exceeds the pixel limit or
    /// the fixed tint-work limit, in which case the inner exception is a <see cref="PdfLimitExceededException"/>
    /// naming the limit.
    /// </exception>
    public static RasterImage ToRgb(
        byte[] data, PdfLimits limits, int width, int height, int components, int bitsPerComponent, PdfColorSpace colorSpace, double[]? decode = null)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        try
        {
            return ToRgbCore(data, limits, width, height, components, bitsPerComponent, colorSpace, decode);
        }
        catch (PdfLimitExceededException ex)
        {
            // The documented type of this public method stays InvalidDataException; the breach rides along.
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private static RasterImage ToRgbCore(
        byte[] data, PdfLimits limits, int width, int height, int components, int bitsPerComponent, PdfColorSpace colorSpace, double[]? decode)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (colorSpace is null)
        {
            throw new ArgumentNullException(nameof(colorSpace));
        }

        if (width <= 0 || height <= 0 || components <= 0 || bitsPerComponent <= 0)
        {
            return new RasterImage { Width = Math.Max(1, width), Height = Math.Max(1, height), Components = 1, Samples = Array.Empty<byte>() };
        }

        // Bound the work and the output allocation before allocating (ADR-0010 §5.2-4).
        long pixels = (long)width * height;
        if (pixels > limits.MaxImagePixels)
        {
            throw new PdfLimitExceededException(
                nameof(PdfLimits.MaxImagePixels),
                limits.MaxImagePixels,
                $"Image of {width}x{height} pixels exceeds the limit of {limits.MaxImagePixels} pixels.");
        }

        if (components > Limits.MaxComponents || colorSpace.ComponentCount > Limits.MaxComponents)
        {
            throw new InvalidDataException($"Image has more than {Limits.MaxComponents} components.");
        }

        if (bitsPerComponent > 16)
        {
            throw new InvalidDataException($"Image bits per component {bitsPerComponent} exceeds 16.");
        }

        double[] dec = decode is { Length: > 0 } ? decode : colorSpace.DefaultDecode(bitsPerComponent);
        double maxVal = (1 << bitsPerComponent) - 1.0;
        bool gray = colorSpace.IsGrayscale && colorSpace.ComponentCount == 1 && components == 1;

        int compBufLen = Math.Max(components, colorSpace.ComponentCount);
        var comp = new double[compBufLen];
        long rowStrideBits = (((long)width * components * bitsPerComponent + 7) / 8) * 8;

        if (gray)
        {
            var outGray = new byte[pixels];
            double dlo0 = dec.Length > 0 ? dec[0] : 0.0;
            double dhi0 = dec.Length > 1 ? dec[1] : 1.0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    long bitPos = (y * rowStrideBits) + ((long)x * bitsPerComponent);
                    int raw = ReadBits(data, bitPos, bitsPerComponent);
                    double v = dlo0 + ((raw / maxVal) * (dhi0 - dlo0));
                    outGray[(y * (long)width) + x] = ToByte(v);
                }
            }

            return new RasterImage { Width = width, Height = height, Components = 1, Samples = outGray };
        }

        // Tint transforms (Separation/DeviceN) run a function per pixel: cache the result per distinct raw
        // sample tuple (packed into a long when it fits) and charge each evaluation to a work budget.
        long tintCost = colorSpace is SeparationColorSpace sep ? sep.EvaluationCost : 0;
        bool useCache = tintCost > 0 && (long)components * bitsPerComponent <= 62;
        Dictionary<long, int>? cache = useCache ? new Dictionary<long, int>() : null;
        long tintWork = 0;

        var outRgb = new byte[pixels * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                long baseBit = (y * rowStrideBits) + ((long)x * components * bitsPerComponent);
                long key = 0;
                for (int c = 0; c < components; c++)
                {
                    int raw = ReadBits(data, baseBit + ((long)c * bitsPerComponent), bitsPerComponent);
                    key = (key << bitsPerComponent) | (uint)raw;
                    double dlo = (2 * c) < dec.Length ? dec[2 * c] : 0.0;
                    double dhi = ((2 * c) + 1) < dec.Length ? dec[(2 * c) + 1] : 1.0;
                    comp[c] = dlo + ((raw / maxVal) * (dhi - dlo));
                }

                byte r, g, b;
                if (cache is not null && cache.TryGetValue(key, out int packed))
                {
                    r = (byte)(packed >> 16);
                    g = (byte)(packed >> 8);
                    b = (byte)packed;
                }
                else
                {
                    if (tintCost > 0)
                    {
                        tintWork += tintCost;
                        if (tintWork > Limits.MaxTintWork)
                        {
                            throw new PdfLimitExceededException(
                                nameof(Limits.MaxTintWork),
                                Limits.MaxTintWork,
                                $"Image tint conversion exceeds the work limit of {Limits.MaxTintWork}.");
                        }
                    }

                    colorSpace.ToRgb(comp, 0, out r, out g, out b);
                    if (cache is not null && cache.Count < Limits.MaxTintCacheEntries)
                    {
                        cache[key] = (r << 16) | (g << 8) | b;
                    }
                }

                long o = ((y * (long)width) + x) * 3;
                outRgb[o] = r;
                outRgb[o + 1] = g;
                outRgb[o + 2] = b;
            }
        }

        return new RasterImage { Width = width, Height = height, Components = 3, Samples = outRgb };
    }

    /// <summary>
    /// Normalizes an already-decoded 8-bit raster (e.g. a codec's output) through a colour space. The
    /// raster's <see cref="RasterImage.Components"/> are treated as raw colour components.
    /// </summary>
    /// <param name="decoded">The decoded 8-bit raster.</param>
    /// <param name="colorSpace">The resolved colour space.</param>
    /// <param name="decode">The image's <c>/Decode</c> array, or null for the colour space default.</param>
    /// <remarks>Uses the default <see cref="PdfLimits"/>.</remarks>
    public static RasterImage ToRgb(RasterImage decoded, PdfColorSpace colorSpace, double[]? decode = null)
        => ToRgb(decoded, PdfLimits.Default, colorSpace, decode);

    /// <summary>
    /// Normalizes an already-decoded 8-bit raster (e.g. a codec's output) through a colour space, within
    /// <paramref name="limits"/>. The raster's <see cref="RasterImage.Components"/> are treated as raw colour
    /// components.
    /// </summary>
    /// <param name="decoded">The decoded 8-bit raster.</param>
    /// <param name="limits">The limits to apply; the image may have at most <see cref="PdfLimits.MaxImagePixels"/> pixels.</param>
    /// <param name="colorSpace">The resolved colour space.</param>
    /// <param name="decode">The image's <c>/Decode</c> array, or null for the colour space default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decoded"/>, <paramref name="limits"/> or <paramref name="colorSpace"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The image exceeds a limit (the inner exception is a <see cref="PdfLimitExceededException"/> naming it), or
    /// has more than 32 components.
    /// </exception>
    public static RasterImage ToRgb(RasterImage decoded, PdfLimits limits, PdfColorSpace colorSpace, double[]? decode = null)
    {
        if (decoded is null)
        {
            throw new ArgumentNullException(nameof(decoded));
        }

        return ToRgb(decoded.Samples, limits, decoded.Width, decoded.Height, decoded.Components, 8, colorSpace, decode);
    }

    private static int ReadBits(byte[] data, long bitPos, int n)
    {
        if (n == 8)
        {
            long bp = bitPos >> 3;
            return bp >= 0 && bp < data.Length ? data[bp] : 0;
        }

        int v = 0;
        for (int i = 0; i < n; i++)
        {
            long bp = bitPos + i;
            long bytePos = bp >> 3;
            int bit = 7 - (int)(bp & 7);
            int b = bytePos >= 0 && bytePos < data.Length ? (data[bytePos] >> bit) & 1 : 0;
            v = (v << 1) | b;
        }

        return v;
    }

    private static byte ToByte(double v01)
    {
        // Clamp in floating point: NaN and huge values must not reach the int cast.
        double x = Math.Round(v01 * 255.0, MidpointRounding.AwayFromZero);
        return x >= 255.0 ? (byte)255 : (x > 0.0 ? (byte)x : (byte)0);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using ZeroDep.Abstractions;

namespace ZeroDep.Filters.Jbig2;

/// <summary>
/// Pure-BCL decoder for PDF <c>/JBIG2Decode</c> bi-level images (ITU-T T.88), embedded organization.
/// Decodes <b>generic regions</b> (arithmetic via the MQ coder, and MMR via the CCITT G4 path),
/// <b>symbol dictionaries</b>, and <b>text regions</b> (arithmetic variants), compositing them onto the
/// page. Halftone and refinement regions (absent from real PDFs) and the Huffman variants are skipped.
/// Output is a 1-component grayscale <see cref="RasterImage"/> (black = 0, white = 255).
/// </summary>
public static class Jbig2Decode
{
    private const int SymbolDictionary = 0;
    private const int IntermediateTextRegion = 4;
    private const int ImmediateTextRegion = 6;
    private const int ImmediateLosslessTextRegion = 7;
    private const int IntermediateGenericRegion = 36;
    private const int ImmediateGenericRegion = 38;
    private const int ImmediateLosslessGenericRegion = 39;
    private const int PageInfo = 48;

    /// <summary>Decodes a JBIG2 image to a raster of the given pixel dimensions (from the PDF image dict).</summary>
    /// <param name="data">The embedded JBIG2 segment stream.</param>
    /// <param name="globals">The optional decoded <c>JBIG2Globals</c> segment stream, or null.</param>
    /// <param name="width">The image width (PDF <c>/Width</c>).</param>
    /// <param name="height">The image height (PDF <c>/Height</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
    /// <exception cref="InvalidDataException">The page size exceeds the pixel limit, or the data is malformed.</exception>
    /// <remarks>Uses the default <see cref="PdfLimits"/>.</remarks>
    public static RasterImage Decode(byte[] data, byte[]? globals, int width, int height)
        => Decode(data, PdfLimits.Default, globals, width, height);

    /// <summary>Decodes a JBIG2 image to a raster of the given pixel dimensions, within <paramref name="limits"/>.</summary>
    /// <param name="data">The embedded JBIG2 segment stream.</param>
    /// <param name="limits">
    /// The limits to apply: the page may have at most <see cref="PdfLimits.MaxImagePixels"/> pixels, and all the
    /// bitmaps of one decode together at most twice that.
    /// </param>
    /// <param name="globals">The optional decoded <c>JBIG2Globals</c> segment stream, or null.</param>
    /// <param name="width">The image width (PDF <c>/Width</c>).</param>
    /// <param name="height">The image height (PDF <c>/Height</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> or <paramref name="limits"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The data is malformed; or the page size exceeds the pixel limit, in which case the inner exception is a
    /// <see cref="PdfLimitExceededException"/> naming the limit. A segment that exceeds a limit is skipped.
    /// </exception>
    public static RasterImage Decode(byte[] data, PdfLimits limits, byte[]? globals, int width, int height)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        return DecoderBoundary.Run(() => DecodeCore(data, globals, width, height, limits), "JBIG2Decode");
    }

    private static RasterImage DecodeCore(byte[] data, byte[]? globals, int width, int height, PdfLimits limits)
    {
        // Every bitmap of this decode draws on one allowance of twice the pixel limit: the page, then each
        // region, symbol and symbol placement. A segment that overdraws it is skipped like a malformed one.
        if ((long)Math.Max(0, width) * Math.Max(0, height) > limits.MaxImagePixels)
        {
            throw DecoderBoundary.PixelLimit("JBIG2 page", width, height, limits);
        }

        long allowance = limits.MaxImagePixels > long.MaxValue / 2 ? long.MaxValue : 2 * limits.MaxImagePixels;
        var budget = new PixelBudget(allowance, limits);
        budget.Charge(width, height, "JBIG2 page");
        var page = new Jbig2Bitmap(width, height);
        var symbolDicts = new Dictionary<long, List<Jbig2Bitmap>>();

        if (globals is { Length: > 0 })
        {
            ProcessSegments(globals, page, symbolDicts, budget);
        }

        ProcessSegments(data, page, symbolDicts, budget);

        return ToRaster(page);
    }

    /// <summary>
    /// Inspects which JBIG2 features a stream uses, so corpus coverage can be quantified. A stream is
    /// fully supported when it uses no Huffman coding, no refinement, and no halftone/pattern regions.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public static Jbig2Capabilities Inspect(byte[] data, byte[]? globals = null)
        => DecoderBoundary.Run(
            () =>
            {
                var caps = default(Jbig2Capabilities);
                InspectStream(globals, ref caps);
                InspectStream(data, ref caps);
                return caps;
            },
            "JBIG2Decode");

    private static void InspectStream(byte[]? d, ref Jbig2Capabilities caps)
    {
        if (d is null)
        {
            return;
        }

        int pos = 0;
        while (pos + 11 <= d.Length)
        {
            if (!TryParseHeader(d, pos, out Segment seg))
            {
                break;
            }

            int s = seg.DataStart;
            switch (seg.Type)
            {
                case SymbolDictionary when s + 2 <= d.Length:
                {
                    int f = (d[s] << 8) | d[s + 1];
                    if ((f & 1) != 0)
                    {
                        caps.UsesHuffman = true;
                    }

                    if ((f & 2) != 0)
                    {
                        caps.UsesRefinement = true; // SDREFAGG
                    }

                    break;
                }

                case IntermediateTextRegion:
                case ImmediateTextRegion:
                case ImmediateLosslessTextRegion when s + 19 <= d.Length:
                {
                    caps.HasText = true;
                    if (s + 19 <= d.Length)
                    {
                        int f = (d[s + 17] << 8) | d[s + 18];
                        if ((f & 1) != 0)
                        {
                            caps.UsesHuffman = true;
                        }

                        if ((f & 2) != 0)
                        {
                            caps.UsesRefinement = true; // SBREFINE
                        }
                    }

                    break;
                }

                case IntermediateGenericRegion:
                case ImmediateGenericRegion:
                case ImmediateLosslessGenericRegion:
                    caps.HasGeneric = true;
                    break;

                case 16:        // pattern dictionary
                case 20:        // intermediate halftone
                case 22:        // immediate halftone
                case 23:        // immediate lossless halftone
                    caps.UsesHalftone = true;
                    break;

                case 40:        // intermediate refinement region
                case 42:        // immediate refinement region
                case 43:        // immediate lossless refinement region
                    caps.UsesRefinement = true;
                    break;

                default:
                    break;
            }

            long next = seg.DataStart + seg.DataLength;
            if (seg.DataLength < 0 || next > d.Length || next <= pos)
            {
                break;
            }

            pos = (int)next;
        }
    }

    /// <summary>Returns the sequence of segment types in a JBIG2 stream (diagnostic; T.88 §7.3 codes).</summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public static IReadOnlyList<int> SegmentTypes(byte[] data)
        => DecoderBoundary.Run(() => SegmentTypesCore(data), "JBIG2Decode");

    private static IReadOnlyList<int> SegmentTypesCore(byte[] data)
    {
        var types = new List<int>();
        if (data is null)
        {
            return types;
        }

        int pos = 0;
        while (pos + 11 <= data.Length)
        {
            if (!TryParseHeader(data, pos, out Segment seg))
            {
                break;
            }

            types.Add(seg.Type);
            long next = seg.DataStart + seg.DataLength;
            if (seg.DataLength < 0 || next > data.Length || next <= pos)
            {
                break;
            }

            pos = (int)next;
        }

        return types;
    }

    private static void ProcessSegments(byte[] d, Jbig2Bitmap page, Dictionary<long, List<Jbig2Bitmap>> symbolDicts, PixelBudget budget)
    {
        int pos = 0;
        while (pos + 11 <= d.Length)
        {
            if (!TryParseHeader(d, pos, out Segment seg))
            {
                break;
            }

            long dataLength = seg.DataLength;
            long next = seg.DataStart + dataLength;
            if (dataLength < 0 || next > d.Length)
            {
                next = d.Length;
                dataLength = d.Length - seg.DataStart;
            }

            try
            {
                switch (seg.Type)
                {
                    case PageInfo:
                        ApplyPageInfo(d, seg.DataStart, page);
                        break;

                    case SymbolDictionary:
                        symbolDicts[seg.Number] = Jbig2SymbolDict.Decode(
                            d, seg.DataStart, (int)dataLength, Gather(seg.ReferredTo, symbolDicts), budget);
                        break;

                    case IntermediateTextRegion:
                    case ImmediateTextRegion:
                    case ImmediateLosslessTextRegion:
                    {
                        Jbig2Region? region = Jbig2TextRegion.Decode(
                            d, seg.DataStart, (int)dataLength, Gather(seg.ReferredTo, symbolDicts), budget);
                        if (region is { } r)
                        {
                            page.Combine(r.Bitmap, r.X, r.Y, r.CombOp);
                        }

                        break;
                    }

                    case IntermediateGenericRegion:
                    case ImmediateGenericRegion:
                    case ImmediateLosslessGenericRegion:
                        DecodeGenericRegion(d, seg.DataStart, (int)dataLength, page, budget);
                        break;

                    default:
                        break; // halftone / refinement / control — skipped
                }
            }
            catch (Exception ex) when (DecoderBoundary.IsIsolatable(ex))
            {
                // A malformed segment is isolated; continue with the rest of the page.
            }

            pos = (int)next;
        }
    }

    // Each referred dictionary counts once, and the combined symbol set is bounded, so a segment that
    // refers to one dictionary many times cannot multiply its symbols.
    private static List<Jbig2Bitmap> Gather(long[] referredTo, Dictionary<long, List<Jbig2Bitmap>> symbolDicts)
    {
        var symbols = new List<Jbig2Bitmap>();
        var seen = new HashSet<long>();
        foreach (long r in referredTo)
        {
            if (seen.Add(r) && symbolDicts.TryGetValue(r, out List<Jbig2Bitmap>? dict))
            {
                if (symbols.Count + dict.Count > Limits.MaxJbig2Symbols)
                {
                    throw DecoderBoundary.FixedLimit(
                        nameof(Limits.MaxJbig2Symbols), Limits.MaxJbig2Symbols, $"JBIG2 symbol set exceeds {Limits.MaxJbig2Symbols} symbols.");
                }

                symbols.AddRange(dict);
            }
        }

        return symbols;
    }

    private static bool TryParseHeader(byte[] d, int pos, out Segment seg)
    {
        seg = default;

        long segNum = U32(d, pos);
        int flags = d[pos + 4];
        int type = flags & 0x3F;
        int pageAssocSize = (flags & 0x40) != 0 ? 4 : 1;

        int p = pos + 5;
        if (p >= d.Length)
        {
            return false;
        }

        int rtByte = d[p];
        long refCount = (uint)rtByte >> 5;
        if (refCount == 7)
        {
            refCount = U32(d, p) & 0x1FFFFFFF;
            p += 4;
            p += (int)((refCount + 8) / 8); // retention-flag bytes
        }
        else
        {
            p += 1;
        }

        int refSize = segNum <= 256 ? 1 : (segNum <= 65536 ? 2 : 4);

        // Check the count against the bytes that remain before allocating the list.
        if (refCount < 0 || refCount > Limits.MaxJbig2ReferredSegments || p > d.Length || refCount * refSize > d.Length - p)
        {
            return false;
        }

        var referredTo = new long[refCount];
        for (int i = 0; i < refCount; i++)
        {
            if (p + refSize > d.Length)
            {
                return false;
            }

            referredTo[i] = ReadNum(d, p, refSize);
            p += refSize;
        }

        p += pageAssocSize;
        if (p + 4 > d.Length)
        {
            return false;
        }

        long dataLength = U32(d, p);
        p += 4;

        seg = new Segment
        {
            Number = segNum,
            Type = type,
            ReferredTo = referredTo,
            DataStart = p,
            DataLength = dataLength,
        };
        return true;
    }

    private static void ApplyPageInfo(byte[] d, int p, Jbig2Bitmap page)
    {
        if (p + 17 > d.Length)
        {
            return;
        }

        if ((d[p + 16] & 0x04) != 0)
        {
            for (int i = 0; i < page.Data.Length; i++)
            {
                page.Data[i] = 1;
            }
        }
    }

    private static void DecodeGenericRegion(byte[] d, int segStart, int segLen, Jbig2Bitmap page, PixelBudget budget)
    {
        int p = segStart;
        int rw = (int)U32(d, p);
        int rh = (int)U32(d, p + 4);
        int rx = (int)U32(d, p + 8);
        int ry = (int)U32(d, p + 12);
        int combOp = d[p + 16] & 7;

        // Rows below the page cannot affect it (both coders run top to bottom), so the region is decoded
        // only down to the page's last row. Columns cannot be dropped: every pixel advances the coder.
        long visibleRows = (long)page.Height - ry;
        if (visibleRows <= 0 || rw <= 0 || (long)rx >= page.Width || (long)rx + rw <= 0)
        {
            return; // entirely outside the page
        }

        if (rh > visibleRows)
        {
            rh = (int)visibleRows;
        }

        budget.Charge(rw, rh, "JBIG2 generic region");

        int gflags = d[p + 17];
        bool mmr = (gflags & 1) != 0;
        int template = (gflags >> 1) & 3;
        bool tpgdon = (gflags & 8) != 0;

        int q = p + 18;
        var at = Array.Empty<(int X, int Y)>();
        if (!mmr)
        {
            int nat = template == 0 ? 4 : 1;
            at = new (int X, int Y)[nat];
            for (int i = 0; i < nat; i++)
            {
                at[i] = ((sbyte)d[q], (sbyte)d[q + 1]);
                q += 2;
            }
        }

        Jbig2Bitmap region;
        if (mmr)
        {
            int len = (segStart + segLen) - q;
            var sub = new byte[len];
            Array.Copy(d, q, sub, 0, len);
            RasterImage raster = CcittFaxDecode.Decode(sub, new CcittParams { K = -1, Columns = rw, Rows = rh });
            region = FromRaster(raster);
        }
        else
        {
            var mq = new MqDecoder(d, q, segStart + segLen);
            var cx = new ArithContext(1 << 16);
            region = Jbig2GenericRegion.Decode(mq, cx, rw, rh, template, at, tpgdon);
        }

        page.Combine(region, rx, ry, combOp);
    }

    private static Jbig2Bitmap FromRaster(RasterImage raster)
    {
        var bmp = new Jbig2Bitmap(raster.Width, raster.Height);
        for (int i = 0; i < bmp.Data.Length && i < raster.Samples.Length; i++)
        {
            bmp.Data[i] = (byte)(raster.Samples[i] == 0 ? 1 : 0);
        }

        return bmp;
    }

    private static RasterImage ToRaster(Jbig2Bitmap page)
    {
        var samples = new byte[page.Data.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = page.Data[i] == 1 ? (byte)0 : (byte)255;
        }

        return new RasterImage
        {
            Width = page.Width,
            Height = page.Height,
            Components = 1,
            Samples = samples,
        };
    }

    private static long ReadNum(byte[] d, int o, int size)
    {
        long v = 0;
        for (int i = 0; i < size; i++)
        {
            v = (v << 8) | d[o + i];
        }

        return v;
    }

    private static long U32(byte[] d, int o)
        => o + 4 <= d.Length
            ? ((long)d[o] << 24) | ((long)d[o + 1] << 16) | ((long)d[o + 2] << 8) | d[o + 3]
            : 0;

    private struct Segment
    {
        public long Number;
        public int Type;
        public long[] ReferredTo;
        public int DataStart;
        public long DataLength;
    }
}

/// <summary>Which JBIG2 features a stream uses (from <see cref="Jbig2Decode.Inspect"/>).</summary>
public struct Jbig2Capabilities
{
    /// <summary>Uses Huffman-coded symbol dictionaries or text regions (not yet supported).</summary>
    public bool UsesHuffman { get; set; }

    /// <summary>Uses refinement (region refinement or refinement-aggregate coding) (not yet supported).</summary>
    public bool UsesRefinement { get; set; }

    /// <summary>Uses halftone / pattern-dictionary regions (not yet supported).</summary>
    public bool UsesHalftone { get; set; }

    /// <summary>Contains at least one text region.</summary>
    public bool HasText { get; set; }

    /// <summary>Contains at least one generic region.</summary>
    public bool HasGeneric { get; set; }

    /// <summary>True when the stream uses only features this decoder fully supports.</summary>
    public readonly bool Supported => !UsesHuffman && !UsesRefinement && !UsesHalftone;
}

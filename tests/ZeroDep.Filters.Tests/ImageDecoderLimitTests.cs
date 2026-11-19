using Xunit;
using ZeroDep.Filters;
using ZeroDep.Filters.Jbig2;
using ZeroDep.Filters.Jpx;

namespace ZeroDep.Filters.Tests;

/// <summary>
/// Regression tests for image decoders fed crafted headers: allocation sized from the header, loops that
/// never advance, unbounded scan and instance counts, and undocumented exception types (ADR-0010 phase 1).
/// </summary>
public sealed class ImageDecoderLimitTests
{
    private const long MiB = 1024 * 1024;

    private static void U16(MemoryStream s, int v) => s.Write(new[] { (byte)(v >> 8), (byte)v });

    private static void U32(MemoryStream s, long v) => s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    // ---- JPEG 2000 ----

    // A raw codestream: SIZ (one tile covering the image), an optional COD, a QCD, and one empty tile-part.
    private static byte[] Jpx(int size, int comps = 1, int codeBlockExp = 4)
    {
        var s = new MemoryStream();
        s.Write(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 });
        U16(s, 38 + (3 * comps));
        U16(s, 0);
        U32(s, size); U32(s, size); U32(s, 0); U32(s, 0);
        U32(s, size); U32(s, size); U32(s, 0); U32(s, 0);
        U16(s, comps);
        for (int i = 0; i < comps; i++)
        {
            s.Write(new byte[] { 7, 1, 1 });
        }

        if (codeBlockExp >= 0)
        {
            // LRCP, 1 layer, no MCT, 5 levels, 2^(exp+2) code blocks, 5/3 wavelet.
            s.Write(new byte[] { 0xFF, 0x52, 0, 12, 0, 0, 0, 1, 0, 5, (byte)codeBlockExp, (byte)codeBlockExp, 0, 1 });
        }

        s.Write(new byte[] { 0xFF, 0x5C });
        U16(s, 3 + 16);
        s.WriteByte(0x40);
        for (int i = 0; i < 16; i++)
        {
            s.WriteByte(8 << 3);
        }

        s.Write(new byte[] { 0xFF, 0x90, 0, 10, 0, 0, 0, 0, 0, 0, 0, 1, 0xFF, 0x93 });
        s.Write(new byte[64]);
        s.Write(new byte[] { 0xFF, 0xD9 });
        return s.ToArray();
    }

    [Fact]
    public void Jp2BoxWithLengthBelowItsHeaderTerminates()
    {
        // A signature box, then a box whose 64-bit length is 2^32: as an int it is 0, so the box walk never advanced.
        byte[] data =
        {
            0, 0, 0, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A,
            0, 0, 0, 1, (byte)'x', (byte)'x', (byte)'x', (byte)'x', 0, 0, 0, 1, 0, 0, 0, 0,
        };

        Bounded.Throws<NotSupportedException>(() => JpxDecode.Decode(data, 1, 1));
    }

    [Fact]
    public void JpxSizeAboveThePixelLimitIsRejectedBeforeAllocating()
    {
        byte[] data = Jpx(65_535);
        var ex = Bounded.Throws<InvalidDataException>(() => JpxDecode.Decode(data, 65_535, 65_535), maxBytes: 16 * MiB);
        Assert.Contains("pixel limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JpxSizeThatDisagreesWithThePdfSizeIsRejected()
    {
        byte[] data = Jpx(2000);
        var ex = Bounded.Throws<InvalidDataException>(() => JpxDecode.Decode(data, 100, 100), maxBytes: 16 * MiB);
        Assert.Contains("disagrees", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JpxWithTinyCodeBlocksIsRejectedByItsWorkingSet()
    {
        // No COD: 1x1 code blocks, measured at about 166 bytes per sample, so 4000x4000 would need about 2.6 GB.
        byte[] data = Jpx(4000, codeBlockExp: -1);
        var ex = Bounded.Throws<InvalidDataException>(() => JpxDecode.Decode(data, 4000, 4000), maxBytes: 16 * MiB);
        Assert.Contains("memory budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JpxWithinTheBudgetStillDecodes()
    {
        byte[] data = Jpx(2000);
        RasterImage? image = null;
        Assert.Null(Bounded.Run(() => image = JpxDecode.Decode(data, 2000, 2000)));
        Assert.Equal(2000, image!.Width);
        Assert.Equal(2000, image.Height);
    }

    [Fact]
    public void JpxTruncatedMarkerSegmentThrowsInvalidData()
    {
        byte[] data = { 0xFF, 0x4F, 0xFF, 0x52, 0x00, 0x0C, 0x00 };   // COD cut off after one byte
        Bounded.Throws<InvalidDataException>(() => JpxDecode.Decode(data, 10, 10));
    }

    // ---- JBIG2 ----

    private static void SegmentHeader(MemoryStream s, int number, int type, long dataLength)
    {
        U32(s, number);
        s.WriteByte((byte)type);
        s.WriteByte(0);      // no referred-to segments
        s.WriteByte(1);      // page 1
        U32(s, dataLength);
    }

    [Fact]
    public void Jbig2GenericRegionTallerThanThePageIsDecodedOnlyToThePageBottom()
    {
        // A 20000 x 20000 immediate generic region on a 1 x 1 page: 381 MB before the region was clipped.
        var s = new MemoryStream();
        SegmentHeader(s, 1, 38, 18 + 8 + 4);
        U32(s, 20_000); U32(s, 20_000); U32(s, 0); U32(s, 0);
        s.WriteByte(0);
        s.WriteByte(0);   // template 0, arithmetic
        s.Write(new byte[] { 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE });
        s.Write(new byte[] { 0x12, 0x34, 0x56, 0x78 });
        byte[] data = s.ToArray();

        RasterImage? image = null;
        Assert.Null(Bounded.Run(() => image = Jbig2Decode.Decode(data, null, 1, 1), maxBytes: 32 * MiB));
        Assert.Equal(1, image!.Width);
        Assert.Equal(1, image.Height);
    }

    [Fact]
    public void Jbig2PageAboveThePixelLimitIsRejected()
    {
        Bounded.Throws<InvalidDataException>(() => Jbig2Decode.Decode(Array.Empty<byte>(), null, 100_000, 100_000), maxBytes: MiB);
    }

    [Fact]
    public void Jbig2SymbolDictionaryWithHugeSymbolCountIsSkipped()
    {
        var s = new MemoryStream();
        SegmentHeader(s, 1, 0, 2 + 8 + 8 + 16);
        s.Write(new byte[] { 0, 0 });                                   // arithmetic, template 0
        s.Write(new byte[] { 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE });  // AT pixels
        U32(s, int.MaxValue);                                         // exported symbols
        U32(s, int.MaxValue);                                         // new symbols
        s.Write(Enumerable.Repeat((byte)0x5A, 16).ToArray());
        byte[] data = s.ToArray();

        RasterImage? image = null;
        Assert.Null(Bounded.Run(() => image = Jbig2Decode.Decode(data, null, 8, 8), maxBytes: 32 * MiB));
        Assert.All(image!.Samples, b => Assert.Equal(255, b));
    }

    [Fact]
    public void Jbig2TextRegionWithHugeInstanceCountIsSkipped()
    {
        var s = new MemoryStream();
        SegmentHeader(s, 1, 6, 17 + 2 + 4 + 16);
        U32(s, 10); U32(s, 10); U32(s, 0); U32(s, 0);
        s.WriteByte(0);
        s.Write(new byte[] { 0, 0 });       // arithmetic, no refinement
        U32(s, int.MaxValue);               // SBNUMINSTANCES
        s.Write(Enumerable.Repeat((byte)0xA5, 16).ToArray());
        byte[] data = s.ToArray();

        RasterImage? image = null;
        Assert.Null(Bounded.Run(() => image = Jbig2Decode.Decode(data, null, 10, 10), maxBytes: 32 * MiB));
        Assert.Equal(100, image!.Samples.Length);
    }

    [Fact]
    public void Jbig2ReferredToCountBeyondTheDataIsRejectedWithoutAllocating()
    {
        // The long form of the referred-to count claims 1,000,000 segments in a 20-byte header.
        var s = new MemoryStream();
        U32(s, 1);
        s.WriteByte(38);
        U32(s, (7L << 29) | 1_000_000);
        s.Write(new byte[11]);
        byte[] data = s.ToArray();

        IReadOnlyList<int>? types = null;
        Assert.Null(Bounded.Run(() => types = Jbig2Decode.SegmentTypes(data), maxBytes: MiB));
        Assert.Empty(types!);
    }

    // ---- CCITT ----

    [Fact]
    public void CcittRowsAreCappedByThePixelLimit()
    {
        // Every 1 bit is a complete all-white Group 4 row: 125 KB made 1 M rows of 2000 pixels (2 GB).
        byte[] data = Enumerable.Repeat((byte)0xFF, 125_000).ToArray();
        var ex = Bounded.Throws<InvalidDataException>(() => CcittFaxDecode.Decode(data, new CcittParams { K = -1, Columns = 2000 }));
        Assert.Contains("pixel limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CcittDeclaredSizeAboveThePixelLimitIsRejected()
    {
        Bounded.Throws<InvalidDataException>(
            () => CcittFaxDecode.Decode(new byte[] { 0xFF }, new CcittParams { K = -1, Columns = 100_000, Rows = 100_000 }), maxBytes: MiB);
    }

    // ---- JPEG ----

    // SOI, one-entry DC and AC Huffman tables (a 1-bit code for symbol 0), a frame header, then the scans.
    private static byte[] Jpeg(int width, int height, int components = 1, bool progressive = false, int scans = 1, bool tables = true)
    {
        var s = new MemoryStream();
        s.Write(new byte[] { 0xFF, 0xD8 });
        if (tables)
        {
            s.Write(new byte[] { 0xFF, 0xC4, 0, 2 + 17 + 1 + 17 + 1 });
            s.WriteByte(0x00); s.WriteByte(1); s.Write(new byte[15]); s.WriteByte(0);
            s.WriteByte(0x10); s.WriteByte(1); s.Write(new byte[15]); s.WriteByte(0);
        }

        s.Write(new byte[] { 0xFF, (byte)(progressive ? 0xC2 : 0xC0) });
        U16(s, 8 + (3 * components));
        s.WriteByte(8);
        U16(s, height);
        U16(s, width);
        s.WriteByte((byte)components);
        for (int c = 0; c < components; c++)
        {
            s.Write(new byte[] { (byte)(c + 1), 0x11, 0 });
        }

        for (int i = 0; i < scans; i++)
        {
            s.Write(new byte[] { 0xFF, 0xDA, 0, 8, 1, 1, 0x00, 0, (byte)(progressive ? 0 : 63), 0 });
            s.WriteByte(0x00);
        }

        s.Write(new byte[] { 0xFF, 0xD9 });
        return s.ToArray();
    }

    [Fact]
    public void JpegSizeAboveThePixelLimitIsRejectedBeforeAllocating()
    {
        byte[] data = Jpeg(65_535, 65_535);
        var ex = Bounded.Throws<InvalidDataException>(() => JpegDecoder.Decode(data), maxBytes: MiB);
        Assert.Contains("pixel limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JpegWorkingSetAboveTheBudgetIsRejected()
    {
        // 16384 x 16384 is within the pixel limit, but three planes plus RGB output need about 1.5 GiB.
        byte[] data = Jpeg(16_384, 16_384, components: 3);
        var ex = Bounded.Throws<InvalidDataException>(() => JpegDecoder.Decode(data), maxBytes: MiB);
        Assert.Contains("memory budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineJpegStopsDecodingWhenEntropyDataRunsOut()
    {
        // One byte of entropy data for a million 8x8 blocks: the rest repeat the DC level cheaply.
        byte[] data = Jpeg(8000, 8000);
        RasterImage? image = null;
        Assert.Null(Bounded.Run(() => image = JpegDecoder.Decode(data)));
        Assert.Equal(8000, image!.Width);
        Assert.Equal(128, image.Samples[image.Samples.Length - 1]);
    }

    [Fact]
    public void ProgressiveJpegScanCountIsBounded()
    {
        // 5000 DC scans over 250,000 blocks each.
        byte[] data = Jpeg(4000, 4000, progressive: true, scans: 5000);
        RasterImage? image = null;
        Assert.Null(Bounded.Run(() => image = JpegDecoder.Decode(data)));
        Assert.Equal(4000, image!.Height);
    }

    [Fact]
    public void JpegWithoutHuffmanTablesThrowsInvalidData()
    {
        byte[] data = Jpeg(16, 16, tables: false);
        Bounded.Throws<InvalidDataException>(() => JpegDecoder.Decode(data));
    }
}

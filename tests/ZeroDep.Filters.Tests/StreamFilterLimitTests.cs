using System.IO.Compression;
using System.Text;
using Xunit;
using ZeroDep.Filters;

namespace ZeroDep.Filters.Tests;

/// <summary>
/// Regression tests for stream filters fed crafted input: output caps, the LZW table size, predictor and
/// ASCII85 validation (ADR-0010 phase 1).
/// </summary>
public sealed class StreamFilterLimitTests
{
    private const long MiB = 1024 * 1024;

    private static byte[] Zlib(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }

        return ms.ToArray();
    }

    [Fact]
    public void FlateOutputAbove256MiBIsRejected()
    {
        byte[] bomb = Zlib(new byte[300 * MiB]);   // a few hundred KB that inflate to 300 MiB

        var ex = Bounded.Throws<InvalidDataException>(() => FlateDecode.Decode(bomb));
        Assert.Contains("256 MiB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlateOutputBelowTheCapStillDecodes()
    {
        byte[] raw = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BT (Hello) Tj ET\n", 10_000)));
        Assert.Equal(raw, FlateDecode.Decode(Zlib(raw)));
    }

    // Emits codes that grow the newest entry each time (KwKwK), then repeats the longest code forever
    // without a ClearTable, so the table would keep growing without the 4096-entry cap.
    private static byte[] LzwBomb(int repeats)
    {
        var codes = new List<(int Code, int Width)>();
        int tableCount = 258, width = 9;
        void Emit(int c, bool adds)
        {
            codes.Add((c, width));
            if (adds)
            {
                tableCount++;
            }

            if (tableCount + 1 >= (1 << width) && width < 12)
            {
                width++;
            }
        }

        Emit(0, false);
        while (tableCount < 4095)
        {
            Emit(tableCount, true);
        }

        for (int i = 0; i < repeats; i++)
        {
            Emit(4094, true);
        }

        var output = new List<byte>();
        long acc = 0;
        int bits = 0;
        foreach ((int c, int w) in codes)
        {
            acc = (acc << w) | (uint)c;
            bits += w;
            while (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(acc >> bits));
            }
        }

        if (bits > 0)
        {
            output.Add((byte)(acc << (8 - bits)));
        }

        return output.ToArray();
    }

    [Fact]
    public void LzwTableStopsGrowingAt4096Entries()
    {
        byte[] input = LzwBomb(20_000);   // about 82 MB of output from 30 KB
        byte[]? output = null;

        Exception? ex = Bounded.Run(() => output = LzwDecode.Decode(input));
        Assert.Null(ex);

        // A literal, then codes 258..4094 with entries of length 2..3838, then 20,000 copies of entry 4094.
        long expected = (3838L * 3839 / 2) + (20_000L * 3838);
        Assert.Equal(expected, output!.LongLength);
    }

    [Fact]
    public void LzwOutputAbove256MiBIsRejected()
    {
        byte[] input = LzwBomb(75_000);   // about 288 MB of output
        Bounded.Throws<InvalidDataException>(() => LzwDecode.Decode(input));
    }

    [Fact]
    public void RunLengthOutputAbove256MiBIsRejected()
    {
        var input = new byte[2 * 2_200_000];   // 2.2 M runs of 128 bytes each
        for (int i = 0; i < input.Length; i += 2)
        {
            input[i] = 129;
            input[i + 1] = (byte)'x';
        }

        Bounded.Throws<InvalidDataException>(() => RunLengthDecode.Decode(input));
    }

    [Theory]
    [InlineData("uuuuu~>")]          // 85^5 - 1 > 2^32 - 1
    [InlineData("s8W-\"~>")]          // just above 2^32 - 1
    [InlineData("!!z!!~>")]           // 'z' inside a group
    [InlineData("uuu~>")]             // a final partial group that overflows when padded
    public void Ascii85OverflowAndMidGroupZAreRejected(string text)
    {
        Assert.Throws<InvalidDataException>(() => Ascii85Decode.Decode(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void Ascii85MaximumGroupAndZShorthandStillDecode()
    {
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, Ascii85Decode.Decode(Encoding.ASCII.GetBytes("s8W-!~>")));
        Assert.Equal(new byte[8], Ascii85Decode.Decode(Encoding.ASCII.GetBytes("zz~>")));
    }

    [Theory]
    [InlineData(7, 1, 8, 4)]       // predictor 3..9 is not defined
    [InlineData(16, 1, 8, 4)]      // nor is 16
    [InlineData(12, 0, 8, 4)]      // /Colors 0
    [InlineData(12, 33, 8, 4)]     // /Colors above 32
    [InlineData(12, 1, 3, 4)]      // /BitsPerComponent 3
    [InlineData(12, 1, 8, 0)]      // /Columns 0
    [InlineData(2, -1, 8, 4)]
    public void InvalidPredictorParametersAreRejected(int predictor, int colors, int bitsPerComponent, int columns)
    {
        byte[] data = Zlib(new byte[] { 0, 1, 2, 3, 4 });
        Assert.Throws<InvalidDataException>(() => FlateDecode.Decode(data, predictor, colors, bitsPerComponent, columns));
    }

    [Fact]
    public void HugePredictorRowWithTinyDataDoesNotAllocateTheRow()
    {
        byte[] data = Zlib(new byte[] { 0, 1, 2, 3, 4 });
        byte[]? output = null;

        Exception? ex = Bounded.Run(() => output = FlateDecode.Decode(data, 12, 1, 8, 2_000_000_000), maxBytes: 16 * MiB);
        Assert.Null(ex);
        Assert.Empty(output!);
    }
}

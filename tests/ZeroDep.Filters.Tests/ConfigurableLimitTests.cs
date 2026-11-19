using System.IO.Compression;
using System.Text;
using Xunit;
using ZeroDep.Abstractions;
using ZeroDep.Filters;

namespace ZeroDep.Filters.Tests;

/// <summary>
/// The stream filters' <see cref="PdfLimits"/> overloads: a small injected budget is enforced, the breach keeps
/// the documented <see cref="InvalidDataException"/> and carries the <see cref="PdfLimitExceededException"/>
/// naming the limit, and the overloads without limits behave as before (ADR-0010 phase 2).
/// </summary>
public sealed class ConfigurableLimitTests
{
    private static readonly PdfLimits Small = new PdfLimits { MaxDecodedStreamBytes = 100 };

    private static byte[] Zlib(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }

        return ms.ToArray();
    }

    // Literal 9-bit LZW codes (no table growth reaches 10 bits within 250 codes), MSB first.
    private static byte[] LzwLiterals(int count)
    {
        var bits = new List<bool>();
        for (int i = 0; i < count; i++)
        {
            for (int b = 8; b >= 0; b--) bits.Add((('A' >> b) & 1) != 0);
        }

        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }

        return bytes;
    }

    private static void AssertDecodedLimit(Func<byte[]> decode, long limit = 100)
    {
        var ex = Assert.Throws<InvalidDataException>(() => decode());
        var inner = Assert.IsType<PdfLimitExceededException>(ex.InnerException);
        Assert.Equal(nameof(PdfLimits.MaxDecodedStreamBytes), inner.LimitName);
        Assert.Equal(limit, inner.Limit);
    }

    [Fact]
    public void Flate_OverTheInjectedLimit_ThrowsInvalidDataNamingTheLimit()
    {
        byte[] data = Zlib(new byte[101]);

        AssertDecodedLimit(() => FlateDecode.Decode(data, Small));
        AssertDecodedLimit(() => FlateDecode.Decode(data, Small, 1, 1, 8, 1));
        Assert.Equal(101, FlateDecode.Decode(data).Length);
        Assert.Equal(100, FlateDecode.Decode(Zlib(new byte[100]), Small).Length);
    }

    [Fact]
    public void Lzw_OverTheInjectedLimit_ThrowsInvalidDataNamingTheLimit()
    {
        byte[] data = LzwLiterals(150);

        AssertDecodedLimit(() => LzwDecode.Decode(data, Small));
        AssertDecodedLimit(() => LzwDecode.Decode(data, Small, 1, 1, 8, 1, 1));
        Assert.Equal(150, LzwDecode.Decode(data).Length);
        Assert.Equal(150, LzwDecode.Decode(data, 1, 1, 8, 1, 1).Length);
    }

    [Fact]
    public void RunLength_OverTheInjectedLimit_ThrowsInvalidDataNamingTheLimit()
    {
        byte[] data = { 129, 0x41, 129, 0x42, 128 }; // two runs of 128 bytes

        AssertDecodedLimit(() => RunLengthDecode.Decode(data, Small));
        Assert.Equal(256, RunLengthDecode.Decode(data).Length);
    }

    [Fact]
    public void Ascii85_OverTheInjectedLimit_ThrowsInvalidDataNamingTheLimit()
    {
        byte[] data = Encoding.ASCII.GetBytes(new string('z', 26) + "~>"); // 104 bytes

        AssertDecodedLimit(() => Ascii85Decode.Decode(data, Small));
        Assert.Equal(104, Ascii85Decode.Decode(data).Length);
    }

    [Fact]
    public void AsciiHex_OverTheInjectedLimit_ThrowsInvalidDataNamingTheLimit()
    {
        byte[] data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("41", 101)) + ">");

        AssertDecodedLimit(() => AsciiHexDecode.Decode(data, Small));
        Assert.Equal(101, AsciiHexDecode.Decode(data).Length);
    }

    [Fact]
    public void Ascii85_DefaultLimit_BoundsTheZShorthand()
    {
        // 'z' expands one byte to four: 70 M of them would be 280 MiB of output.
        byte[] data = new byte[70_000_000];
        Array.Fill(data, (byte)'z');

        var ex = Bounded.Throws<InvalidDataException>(() => Ascii85Decode.Decode(data), maxBytes: 600L * 1024 * 1024);
        Assert.Equal(nameof(PdfLimits.MaxDecodedStreamBytes), Assert.IsType<PdfLimitExceededException>(ex.InnerException).LimitName);
    }

    [Fact]
    public void NullLimits_AreRejected()
    {
        byte[] data = Array.Empty<byte>();

        Assert.Throws<ArgumentNullException>(() => FlateDecode.Decode(data, (PdfLimits)null!));
        Assert.Throws<ArgumentNullException>(() => LzwDecode.Decode(data, (PdfLimits)null!));
        Assert.Throws<ArgumentNullException>(() => LzwDecode.Decode(data, null!, 1, 1, 8, 1, 1));
        Assert.Throws<ArgumentNullException>(() => RunLengthDecode.Decode(data, null!));
        Assert.Throws<ArgumentNullException>(() => Ascii85Decode.Decode(data, null!));
        Assert.Throws<ArgumentNullException>(() => AsciiHexDecode.Decode(data, null!));
        Assert.Throws<ArgumentNullException>(() => FlateDecode.Decode(null!, PdfLimits.Default));
    }
}

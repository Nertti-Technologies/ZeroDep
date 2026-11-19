using System;
using Xunit;
using ZeroDep.Ocr;
using ZeroDep.Ocr.Paddle;

namespace ZeroDep.Ocr.Paddle.Tests;

/// <summary>The BMP wrapper used to hand decoded pixels to the worker.</summary>
public sealed class BmpTests
{
    [Fact]
    public void Encode_SmallImage_HasExpectedSize()
    {
        var image = new DecodedImage { Width = 2, Height = 2, Format = PixelFormat.Gray8, Pixels = new byte[4] };

        // Row width 2*3 = 6, padded to 8; data = 8*2 = 16; file = 54 + 16 = 70.
        Assert.Equal(70, Bmp.Encode(image).Length);
    }

    // Width x height x 3 overflowed int and produced a short array (or an index error) before.
    [Theory]
    [InlineData(int.MaxValue, 2)]
    [InlineData(50_000, 50_000)]
    [InlineData(-1, 1)]
    public void Encode_OversizedOrNegative_ThrowsArgumentException(int width, int height)
    {
        var image = new DecodedImage { Width = width, Height = height, Format = PixelFormat.Gray8 };

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(() => Bmp.Encode(image));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }
}

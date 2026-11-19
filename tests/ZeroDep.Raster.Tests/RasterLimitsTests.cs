using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using ZeroDep.Fonts;
using ZeroDep.Raster;
using static ZeroDep.Fonts.Tests.CraftedFonts;

namespace ZeroDep.Raster.Tests;

/// <summary>
/// ADR-0010 Phase 1 — glyph rasterization stays bounded: bitmap size, non-finite coordinates and scales,
/// supersampling, flattening and scanline work.
/// </summary>
public sealed class RasterLimitsTests
{
    private static GlyphOutline Triangle(double s, double startX = 0, double midX = 0)
    {
        var seg = new List<GlyphSegment> { GlyphSegment.Line(s, 0), GlyphSegment.Line(midX == 0 ? s : midX, s), GlyphSegment.Line(0, s) };
        return new GlyphOutline(new[] { new GlyphContour(startX, 0, seg) }, 0);
    }

    [Fact]
    public void SmallGlyph_StillRenders()
    {
        GlyphBitmap bmp = GlyphRasterizer.Render(Triangle(100), 0.5);
        Assert.Equal(50, bmp.Width);
        Assert.Equal(50, bmp.Height);
    }

    [Theory]
    [InlineData(20_000)]
    [InlineData(1e9)]
    public void HugeGlyph_IsRejectedBeforeAllocating(double size)
    {
        Exception? ex = Bounded(() => GlyphRasterizer.Render(Triangle(size), 1.0), maxBytes: 16L * 1024 * 1024);
        Assert.IsType<InvalidDataException>(ex);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteScale_DrawsNothing(double scale)
    {
        Assert.Same(GlyphBitmap.Empty, GlyphRasterizer.Render(Triangle(10), scale));
    }

    [Fact]
    public void NonFiniteCoordinate_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => GlyphRasterizer.Render(Triangle(10, midX: double.NaN), 1.0));
        Assert.Throws<InvalidDataException>(() => GlyphRasterizer.Render(Triangle(10, startX: double.PositiveInfinity), 1.0));
    }

    [Fact]
    public void HugeSubSampling_IsClamped()
    {
        GlyphBitmap? bmp = null;
        Assert.Null(Bounded(() => bmp = GlyphRasterizer.Render(Triangle(100), 1.0, int.MaxValue)));
        Assert.Equal(100, bmp!.Width);
    }

    [Fact]
    public void ManyLargeCurves_HitTheFlatteningOrWorkLimit()
    {
        // 200,000 cubic curves spanning the whole bitmap: each flattens to hundreds of points.
        var seg = new List<GlyphSegment>();
        for (int i = 0; i < 200_000; i++)
        {
            seg.Add(GlyphSegment.Cubic(8000, 0, 8000, 8000, (i & 1) == 0 ? 0 : 8000, 8000));
        }

        var outline = new GlyphOutline(new[] { new GlyphContour(0, 0, seg) }, 0);
        Exception? ex = Bounded(() => GlyphRasterizer.Render(outline, 1.0), maxBytes: 1024L * 1024 * 1024);
        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void Renderer_NonFiniteOrHugePixelSize_IsBounded()
    {
        FontProgram font = FontProgram.Load(Ttf(new[] { ZeroDep.Fonts.Tests.CraftedFonts.Triangle() }, unitsPerEm: 16));
        Assert.Same(GlyphBitmap.Empty, GlyphRenderer.Render(font, 0, double.NaN));
        Assert.Same(GlyphBitmap.Empty, GlyphRenderer.Render(font, 0, double.PositiveInfinity));

        // A huge size must be refused (bitmap limit), not overflow an int cast or allocate gigabytes.
        Exception? ex = Bounded(() => GlyphRenderer.Render(font, 0, 1e12, hinted: true), maxBytes: 16L * 1024 * 1024);
        Assert.IsType<InvalidDataException>(ex);
        ex = Bounded(() => GlyphRenderer.Render(font, 0, 1e12), maxBytes: 16L * 1024 * 1024);
        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void Renderer_ZeroUnitsPerEmFont_Renders()
    {
        FontProgram font = FontProgram.Load(Ttf(new[] { ZeroDep.Fonts.Tests.CraftedFonts.Triangle() }, unitsPerEm: 0));
        GlyphBitmap? bmp = null;
        Assert.Null(Bounded(() => bmp = GlyphRenderer.Render(font, 0, 16)));
        Assert.True(bmp!.Width > 0);
    }
}

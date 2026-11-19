using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using ZeroDep.Fonts;
using static ZeroDep.Fonts.Tests.CraftedFonts;

namespace ZeroDep.Fonts.Tests;

/// <summary>
/// ADR-0010 Phase 1 — crafted font programs terminate in bounded memory: glyph fan-out (composites,
/// subroutines, seac), hinter loops and stack, malformed tables, and the load/glyph exception contract.
/// </summary>
public sealed class FontLimitsTests
{
    // ---- sanity: well-formed crafted programs still produce outlines ----

    [Fact]
    public void CraftedPrograms_StillRender()
    {
        FontProgram ttf = FontProgram.Load(Ttf(new[] { Triangle() }));
        Assert.Single(ttf.GetGlyph(0).Contours);

        FontProgram composite = FontProgram.Load(Ttf(new[] { Composite(1, 3), Triangle() }));
        Assert.Equal(3, composite.GetGlyph(0).Contours.Count);

        // CFF: 100 0 rmoveto 50 0 rlineto 0 50 rlineto endchar
        byte[] cs = Cs(N(100), N(0), new byte[] { 21 }, N(50), N(0), new byte[] { 5 }, N(0), N(50), new byte[] { 5, 14 });
        Assert.Single(FontProgram.Load(Cff(cs)).GetGlyph(0).Contours);

        // Type 1: 0 500 hsbw 100 0 rmoveto 50 0 rlineto 0 50 rlineto closepath endchar
        byte[] t1 = Cs(N(0), N(500), new byte[] { 13 }, N(100), N(0), new byte[] { 21 }, N(50), N(0), new byte[] { 5 }, N(0), N(50), new byte[] { 5, 9, 14 });
        Assert.Single(FontProgram.Load(Type1(new[] { t1 }, Array.Empty<byte[]>(), "A")).GetGlyphByName("A").Contours);
    }

    // ---- TrueType ----

    [Fact]
    public void TrueType_CompositeIncludingItself_IsSkipped()
    {
        FontProgram font = FontProgram.Load(Ttf(new[] { Composite(0, 10) }));
        Assert.Null(Bounded(() => Assert.True(font.GetGlyph(0).IsEmpty)));
    }

    [Fact]
    public void TrueType_CompositeFanOut_HitsTheWorkLimit()
    {
        // Eight levels of 60 components each, all referencing the next level: 60^8 expansions unbounded.
        var glyphs = new List<byte[]>();
        for (int level = 0; level < 8; level++)
        {
            glyphs.Add(Composite(level + 1, 60));
        }

        glyphs.Add(Triangle());
        FontProgram font = FontProgram.Load(Ttf(glyphs.ToArray()));
        Assert.Null(Bounded(() => Assert.True(font.GetGlyph(0).IsEmpty)));
    }

    [Fact]
    public void TrueType_NonIncreasingEndPoints_GiveAnEmptyOutline()
    {
        // Two contours with end points [5, 2] but only three points.
        var gl = new List<byte>();
        gl.AddRange(BE16(2));
        gl.AddRange(new byte[8]);
        gl.AddRange(BE16(5));
        gl.AddRange(BE16(2));
        gl.AddRange(BE16(0));
        gl.AddRange(new byte[] { 0x37, 0x37, 0x37, 1, 2, 3, 1, 2, 3 });
        FontProgram font = FontProgram.Load(Ttf(new[] { gl.ToArray() }));

        Assert.True(font.GetGlyph(0).IsEmpty);
        Assert.True(font.GetHintedGlyph(0, 16).IsEmpty);
    }

    [Fact]
    public void TrueType_LoopCallOfEmptyFunctions_StaysWithinTheBudget()
    {
        // fpgm: F0 = empty; F1 = LOOPCALL F0 x32767; F2 = LOOPCALL F1 x32767. prep calls F2 in a loop.
        byte[] fpgm =
        {
            0xB0, 0, 0x2C, 0x2D,
            0xB0, 1, 0x2C, 0xB8, 0x7F, 0xFF, 0xB0, 0, 0x2A, 0x2D,
            0xB0, 2, 0x2C, 0xB8, 0x7F, 0xFF, 0xB0, 1, 0x2A, 0x2D,
        };
        byte[] prep = { 0xB8, 0x7F, 0xFF, 0xB0, 2, 0x2A };
        FontProgram font = FontProgram.Load(Ttf(new[] { Triangle() }, fpgm, prep));

        GlyphOutline? hinted = null;
        Assert.Null(Bounded(() => hinted = font.GetHintedGlyph(0, 16)));
        Assert.Single(hinted!.Contours); // prep faulted, so the glyph falls back to the scaled outline
    }

    [Fact]
    public void TrueType_HinterStack_IsBoundedByMaxp()
    {
        // prep: NPUSHB 255 bytes, then jump back to it forever: the stack would grow without bound.
        var prep = new List<byte> { 0x40, 255 };
        prep.AddRange(new byte[255]);
        int jmprAt = prep.Count + 3;
        short off = (short)-jmprAt;
        prep.AddRange(new byte[] { 0xB8, (byte)(off >> 8), (byte)off, 0x1C });
        FontProgram font = FontProgram.Load(Ttf(new[] { Triangle() }, new byte[] { 0x00 }, prep.ToArray(), maxStack: 256));

        GlyphOutline? hinted = null;
        Assert.Null(Bounded(() => hinted = font.GetHintedGlyph(0, 16), maxBytes: 64L * 1024 * 1024));
        Assert.Single(hinted!.Contours);
    }

    [Fact]
    public void TrueType_LoopCountedInstructions_AreCharged()
    {
        // Glyph program: a loop count of about 2.1 × 10^9 (32767 × 32767 / 64 × 8192 / 64), then SHPIX,
        // which iterates once per count: PUSHW 0x7FFF, DUP, MUL, PUSHW 0x2000, MUL, SLOOP, PUSHB 1, SHPIX.
        byte[] instructions = { 0xB8, 0x7F, 0xFF, 0x20, 0x63, 0xB8, 0x20, 0x00, 0x63, 0x17, 0xB0, 1, 0x38 };
        var gl = new List<byte>(Triangle());
        gl[12] = 0; // instruction length (bytes 12-13), then the instructions before the flags
        gl[13] = (byte)instructions.Length;
        gl.InsertRange(14, instructions);
        FontProgram font = FontProgram.Load(Ttf(new[] { gl.ToArray() }, new byte[] { 0x00 }, new byte[] { 0x00 }));

        Assert.Null(Bounded(() => font.GetHintedGlyph(0, 16)));
    }

    [Fact]
    public void TrueType_ZeroUnitsPerEm_FallsBackTo1000()
    {
        Assert.Equal(1000, FontProgram.Load(Ttf(new[] { Triangle() }, unitsPerEm: 0)).UnitsPerEm);
    }

    [Fact]
    public void TrueType_HugePpem_DoesNotOverflow()
    {
        FontProgram font = FontProgram.Load(Ttf(new[] { Triangle() }, unitsPerEm: 16));
        Assert.Null(Bounded(() => font.GetHintedGlyph(0, int.MaxValue)));
    }

    // ---- CFF ----

    [Fact]
    public void Cff_SubroutineFanOut_HitsTheWorkLimit()
    {
        // Global subr i calls subr i+1 ten times, ten levels deep: 10^9 calls without a budget.
        var subrs = new List<byte[]>();
        for (int lvl = 0; lvl < 10; lvl++)
        {
            var sb = new List<byte>();
            if (lvl < 9)
            {
                for (int i = 0; i < 10; i++)
                {
                    sb.Add((byte)(lvl + 1 - 107 + 139));
                    sb.Add(29); // callgsubr
                }
            }

            sb.Add(11); // return
            subrs.Add(sb.ToArray());
        }

        FontProgram font = FontProgram.Load(Cff(new byte[] { 32, 29, 14 }, subrs.ToArray()));
        Assert.Null(Bounded(() => Assert.True(font.GetGlyph(0).IsEmpty)));
    }

    [Fact]
    public void Cff_OpenTypeTableLengthBeyondTheData_IsNotSupported()
    {
        var d = new List<byte>();
        d.AddRange(Encoding.ASCII.GetBytes("OTTO"));
        d.AddRange(BE16(1));
        d.AddRange(new byte[6]);
        d.AddRange(Encoding.ASCII.GetBytes("CFF "));
        d.AddRange(new byte[4]);
        d.AddRange(BE32(28));
        d.AddRange(BE32(0xFFFFFFF0));
        d.AddRange(new byte[] { 1, 0, 4, 1 });

        Exception? ex = Bounded(() => FontProgram.Load(d.ToArray()), maxBytes: 16L * 1024 * 1024);
        Assert.IsType<NotSupportedException>(ex);
    }

    [Fact]
    public void Cff_EmptyTopDictIndex_IsNotSupported()
    {
        byte[] cff = { 1, 0, 4, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Throws<NotSupportedException>(() => CffFont.Load(cff));
        Assert.Throws<NotSupportedException>(() => FontProgram.Load(cff));
    }

    // ---- Type 1 ----

    [Fact]
    public void Type1_SeacReferringToItself_DoesNotNest()
    {
        // 'A' = seac with base 'A' and accent 'A' (StandardEncoding 65).
        byte[] seac = Cs(N(0), N(0), N(0), N(65), N(65), new byte[] { 12, 6 }, new byte[] { 14 });
        FontProgram font = FontProgram.Load(Type1(new[] { seac }, Array.Empty<byte[]>(), "A"));
        Assert.Null(Bounded(() => font.GetGlyphByName("A")));
    }

    [Fact]
    public void Type1_SubroutineFanOut_HitsTheWorkLimit()
    {
        // Subr 0 calls itself twice (depth-capped at 30 levels): 2^30 calls without a budget.
        byte[] subr = Cs(N(0), new byte[] { 10 }, N(0), new byte[] { 10 }, new byte[] { 11 });
        byte[] glyph = Cs(N(0), new byte[] { 10 }, new byte[] { 14 });
        FontProgram font = FontProgram.Load(Type1(new[] { glyph }, new[] { subr }, "A"));
        Assert.Null(Bounded(() => Assert.True(font.GetGlyphByName("A").IsEmpty)));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(2_000_000_000)]
    public void Type1_CallOtherSubrArgumentCount_IsClamped(int count)
    {
        byte[] glyph = Cs(N32(count), N(99), new byte[] { 12, 16 }, new byte[] { 14 });
        FontProgram font = FontProgram.Load(Type1(new[] { glyph }, Array.Empty<byte[]>(), "A"));
        Assert.Null(Bounded(() => font.GetGlyphByName("A"), maxBytes: 16L * 1024 * 1024));
    }

    [Fact]
    public void Type1_TruncatedNumber_GivesAnEmptyOutline()
    {
        FontProgram font = FontProgram.Load(Type1(new[] { new byte[] { 255, 1 } }, Array.Empty<byte[]>(), "A"));
        Assert.True(font.GetGlyphByName("A").IsEmpty);
        Assert.True(font.GetGlyph(0).IsEmpty);
    }

    [Fact]
    public void Type1_NegativeCharStringLength_Terminates()
    {
        byte[] program = Type1Raw("/CharStrings 1 dict dup begin\n/ -10 RD xxxxxxxxxx ND\nend\n");
        Exception? ex = Bounded(() => FontProgram.Load(program));
        Assert.IsType<NotSupportedException>(ex); // no usable CharStrings remain
    }

    [Fact]
    public void Type1_NegativeSubrLength_IsSkipped()
    {
        byte[] program = Type1Raw("/Subrs 2 array\ndup 0 -5 RD xxxxx NP\ndup 1 -2147483648 RD xx NP\n/CharStrings 1 dict dup begin\n/A 1 RD x ND\nend\n");
        FontProgram? font = null;
        Assert.Null(Bounded(() => font = FontProgram.Load(program)));
        Assert.Equal(1, font!.GlyphCount);
    }

    [Fact]
    public void Type1_HugeCharStringLength_IsSkipped()
    {
        byte[] program = Type1Raw("/CharStrings 2 dict dup begin\n/B 2147483647 RD x ND\n/A 1 RD x ND\nend\n");
        Exception? ex = Bounded(() => FontProgram.Load(program), maxBytes: 16L * 1024 * 1024);
        Assert.True(ex is null or NotSupportedException, ex?.ToString());
    }

    // ---- exception contract ----

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x00, 0xFF, 0xFF })] // TrueType header, 65,535 tables, truncated
    [InlineData(new byte[] { 0x4F, 0x54, 0x54, 0x4F, 0x00, 0x01 })] // OTTO, truncated directory
    [InlineData(new byte[] { 0x01, 0x00, 0x04, 0x04, 0x00, 0x01, 0x01, 0x01, 0x05, 0x0C })] // CFF, truncated escape
    [InlineData(new byte[] { 0x25, 0x21 })] // "%!" with no eexec
    [InlineData(new byte[0])]
    public void Load_MalformedPrograms_ThrowOnlyNotSupported(byte[] data)
    {
        Exception? ex = Bounded(() =>
        {
            FontProgram font = FontProgram.Load(data);
            for (int g = 0; g < Math.Min(font.GlyphCount, 4); g++)
            {
                font.GetGlyph(g);
                font.GetHintedGlyph(g, 12);
            }
        });
        Assert.True(ex is null or NotSupportedException, ex?.ToString());
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using ZeroDep.Abstractions;
using ZeroDep.Color;
using ZeroDep.Filters;
using ZeroDep.Objects;

namespace ZeroDep.Color.Tests;

/// <summary>
/// ADR-0010 Phase 1 — crafted colour spaces, functions and images terminate in bounded memory and fail
/// with the documented exception types (nesting, cycles, fan-out, stack and work limits).
/// </summary>
public sealed class ColorLimitsTests
{
    private static byte[] Raw(PdfStream s) => s.GetRawBytes();

    private static PdfReference Ref(int n) => new PdfReference(n, 0);

    private static PdfArray Arr(params PdfObject[] items) => new PdfArray(items);

    private static PdfArray Nums(params double[] v) => new PdfArray(v.Select(x => (PdfObject)new PdfReal(x)).ToArray());

    private static PdfDictionary Dict(Dictionary<string, PdfObject> e) => new PdfDictionary(e);

    private static Func<PdfObject, PdfObject> Resolver(Dictionary<int, PdfObject> objects)
        => o => o is PdfReference r && objects.TryGetValue(r.ObjectNumber, out PdfObject? v) ? v : o;

    private static PdfStream PsFunction(string program, int outputs = 1)
    {
        var range = new List<double>();
        for (int i = 0; i < outputs; i++)
        {
            range.Add(0);
            range.Add(1);
        }

        return new PdfStream(
            Dict(new Dictionary<string, PdfObject>
            {
                ["FunctionType"] = new PdfInteger(4),
                ["Domain"] = Nums(0, 1),
                ["Range"] = Nums(range.ToArray()),
            }),
            Encoding.ASCII.GetBytes(program));
    }

    private static PdfObject Identity(PdfObject o) => o;

    // ---- colour-space cycles and nesting ----

    [Fact]
    public void Indexed_BaseReferringToItself_IsRejected()
    {
        var objects = new Dictionary<int, PdfObject>();
        objects[6] = Arr(new PdfName("Indexed"), Ref(6), new PdfInteger(1), new PdfString(new byte[6], false));

        Exception? ex = Bounded.Run(() => PdfColorSpace.Resolve(Ref(6), Resolver(objects), Raw));
        Assert.IsType<PdfSyntaxException>(ex);
    }

    [Fact]
    public void IccAlternate_ReferringBackToTheSpace_IsRejected()
    {
        var objects = new Dictionary<int, PdfObject>();
        objects[7] = Arr(new PdfName("ICCBased"), Ref(8));
        objects[8] = new PdfStream(Dict(new Dictionary<string, PdfObject> { ["N"] = new PdfInteger(3), ["Alternate"] = Ref(7) }), Array.Empty<byte>());

        Exception? ex = Bounded.Run(() => PdfColorSpace.Resolve(Ref(7), Resolver(objects), Raw));
        Assert.IsType<PdfSyntaxException>(ex);
    }

    [Fact]
    public void SeparationAlternate_ReferringToItself_IsRejected()
    {
        var objects = new Dictionary<int, PdfObject>();
        objects[9] = Arr(new PdfName("Separation"), new PdfName("Ink"), Ref(9), PsFunction("{ }"));

        Exception? ex = Bounded.Run(() => PdfColorSpace.Resolve(Ref(9), Resolver(objects), Raw));
        Assert.IsType<PdfSyntaxException>(ex);
    }

    [Fact]
    public void NamedLookup_ResolvingToItsOwnName_IsRejected()
    {
        // /CS0 -> [/Indexed /CS0 0 <>] — no indirect references, so only the depth limit can stop it.
        PdfObject Lookup(string name)
            => Arr(new PdfName("Indexed"), new PdfName(name), new PdfInteger(0), new PdfString(new byte[3], false));

        Exception? ex = Bounded.Run(() => PdfColorSpace.Resolve(new PdfName("CS0"), Identity, Raw, Lookup));
        Assert.Equal("MaxColorSpaceDepth", Assert.IsType<PdfLimitExceededException>(ex).LimitName);
    }

    [Fact]
    public void ColorSpace_NestedWithinTheLimit_Resolves()
    {
        // Indexed -> ICCBased -> Alternate DeviceRGB: the realistic depth.
        var icc = new PdfStream(Dict(new Dictionary<string, PdfObject> { ["N"] = new PdfInteger(3), ["Alternate"] = new PdfName("DeviceRGB") }), Array.Empty<byte>());
        PdfArray cs = Arr(new PdfName("Indexed"), Arr(new PdfName("ICCBased"), icc), new PdfInteger(0), new PdfString(new byte[] { 1, 2, 3 }, false));
        Assert.Equal("Indexed", PdfColorSpace.Resolve(cs, Identity, Raw).Family);
    }

    [Fact]
    public void ColorSpace_ShortArrays_AreNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => PdfColorSpace.Resolve(Arr(new PdfName("Indexed"), new PdfName("DeviceRGB")), Identity, Raw));
        Assert.Throws<NotSupportedException>(() => PdfColorSpace.Resolve(Arr(new PdfName("Separation"), new PdfName("Ink")), Identity, Raw));
        Assert.Throws<NotSupportedException>(() => PdfColorSpace.Resolve(Arr(new PdfName("DeviceN"), Arr(new PdfName("A"))), Identity, Raw));
    }

    [Fact]
    public void Separation_TintWithTooFewOutputs_IsNotSupported()
    {
        // One output cannot feed DeviceRGB's three components.
        PdfArray cs = Arr(new PdfName("Separation"), new PdfName("Ink"), new PdfName("DeviceRGB"), PsFunction("{ }", outputs: 1));
        Assert.Throws<NotSupportedException>(() => PdfColorSpace.Resolve(cs, Identity, Raw));
    }

    [Fact]
    public void Indexed_HugeHival_IsClampedTo255()
    {
        var lookup = new PdfString(Enumerable.Range(0, 256 * 3).Select(i => (byte)i).ToArray(), false);
        PdfArray cs = Arr(new PdfName("Indexed"), new PdfName("DeviceRGB"), new PdfInteger(2_000_000_000), lookup);

        PdfColorSpace? indexed = null;
        Assert.Null(Bounded.Run(() => indexed = PdfColorSpace.Resolve(cs, Identity, Raw), maxBytes: 16L * 1024 * 1024));
        indexed!.ToRgb(new[] { 1e12 }, 0, out byte r, out _, out _);
        Assert.Equal(765 % 256, r); // palette entry 255 (lookup byte 765 mod 256)
        indexed.ToRgb(new[] { double.NaN }, 0, out byte r0, out _, out _);
        Assert.Equal(0, r0);
    }

    // ---- function cycles, nesting and fan-out ----

    [Fact]
    public void StitchingFunction_ContainingItself_IsRejected()
    {
        var objects = new Dictionary<int, PdfObject>();
        objects[6] = Dict(new Dictionary<string, PdfObject>
        {
            ["FunctionType"] = new PdfInteger(3),
            ["Domain"] = Nums(0, 1),
            ["Functions"] = Arr(Ref(6)),
            ["Bounds"] = Arr(),
            ["Encode"] = Nums(0, 1),
        });

        Exception? ex = Bounded.Run(() => PdfFunction.Parse(Ref(6), Resolver(objects), Raw));
        Assert.IsType<PdfSyntaxException>(ex);
    }

    [Fact]
    public void FunctionArray_ContainingItself_IsRejected()
    {
        var objects = new Dictionary<int, PdfObject> { [6] = Arr(Ref(6)) };
        Exception? ex = Bounded.Run(() => PdfFunction.Parse(Ref(6), Resolver(objects), Raw));
        Assert.IsType<PdfSyntaxException>(ex);
    }

    [Fact]
    public void Function_NestedDirectArrays_HitTheDepthLimit()
    {
        PdfObject f = PsFunction("{ }");
        for (int i = 0; i < 100; i++)
        {
            f = Arr(f);
        }

        Exception? ex = Bounded.Run(() => PdfFunction.Parse(f, Identity, Raw));
        Assert.Equal("MaxFunctionDepth", Assert.IsType<PdfLimitExceededException>(ex).LimitName);
    }

    [Fact]
    public void Function_SharedSubFunctionFanOut_HitsTheNodeLimit()
    {
        // Ten levels, each a stitching function whose ten sub-functions are all the next level: 10^10 nodes.
        var objects = new Dictionary<int, PdfObject>();
        for (int level = 0; level < 10; level++)
        {
            PdfObject[] subs = Enumerable.Repeat<PdfObject>(Ref(level + 1), 10).ToArray();
            objects[level] = Dict(new Dictionary<string, PdfObject>
            {
                ["FunctionType"] = new PdfInteger(3),
                ["Domain"] = Nums(0, 1),
                ["Functions"] = Arr(subs),
                ["Bounds"] = Nums(Enumerable.Range(1, 9).Select(i => i / 10.0).ToArray()),
            });
        }

        objects[10] = PsFunction("{ }");

        Exception? ex = Bounded.Run(() => PdfFunction.Parse(Ref(0), Resolver(objects), Raw));
        Assert.Equal("MaxFunctionNodes", Assert.IsType<PdfLimitExceededException>(ex).LimitName);
    }

    [Fact]
    public void StitchingFunction_ShortBounds_IsNotSupported()
    {
        var dict = Dict(new Dictionary<string, PdfObject>
        {
            ["FunctionType"] = new PdfInteger(3),
            ["Domain"] = Nums(0, 1),
            ["Functions"] = Arr(PsFunction("{ }"), PsFunction("{ }"), PsFunction("{ }")),
            ["Bounds"] = Nums(0.5),
        });
        Assert.Throws<NotSupportedException>(() => PdfFunction.Parse(dict, Identity, Raw));
    }

    // ---- sampled functions ----

    private static PdfStream Sampled(int inputs, double bitsPerSample)
    {
        var domain = new List<double>();
        var size = new List<double>();
        for (int i = 0; i < inputs; i++)
        {
            domain.Add(0);
            domain.Add(1);
            size.Add(2);
        }

        return new PdfStream(
            Dict(new Dictionary<string, PdfObject>
            {
                ["FunctionType"] = new PdfInteger(0),
                ["Domain"] = Nums(domain.ToArray()),
                ["Range"] = Nums(0, 1),
                ["Size"] = Nums(size.ToArray()),
                ["BitsPerSample"] = new PdfReal(bitsPerSample),
            }),
            new byte[] { 0 });
    }

    [Theory]
    [InlineData(2e9)]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(-8)]
    [InlineData(64)]
    public void SampledFunction_BitsPerSampleOutsideTheSpecSet_IsNotSupported(double bps)
    {
        Exception? ex = Bounded.Run(() => PdfFunction.Parse(Sampled(1, bps), Identity, Raw));
        Assert.IsType<NotSupportedException>(ex);
    }

    [Fact]
    public void SampledFunction_TooManyInputs_IsNotSupported()
    {
        Exception? ex = Bounded.Run(() => PdfFunction.Parse(Sampled(26, 8), Identity, Raw));
        Assert.IsType<NotSupportedException>(ex);
    }

    [Fact]
    public void SampledFunction_AllowedBitDepths_Evaluate()
    {
        foreach (int bps in new[] { 1, 2, 4, 8, 12, 16, 24, 32 })
        {
            PdfFunction f = PdfFunction.Parse(Sampled(1, bps), Identity, Raw);
            Assert.Single(f.Evaluate(new[] { 0.5 }));
        }
    }

    // ---- PostScript calculator ----

    [Fact]
    public void PostScript_DeepBraces_AreRejectedWithoutStackOverflow()
    {
        Exception? ex = Bounded.Run(() => PdfFunction.Parse(PsFunction(new string('{', 200_000)), Identity, Raw));
        Assert.Equal("MaxPostScriptBraceDepth", Assert.IsType<PdfLimitExceededException>(ex).LimitName);
    }

    [Fact]
    public void PostScript_BracesWithinTheLimit_Parse()
    {
        string program = new string('{', 50) + " 0.25 " + new string('}', 50);
        Assert.Equal(1, PdfFunction.Parse(PsFunction(program), Identity, Raw).OutputCount);
    }

    [Theory]
    [InlineData("{ 1000000000 copy }")]
    [InlineData("{ 2147483647 copy }")]
    [InlineData("{ 1e300 copy }")]
    [InlineData("{ 99 copy 99 copy 99 copy }")]
    [InlineData("{ 1000000000 index }")]
    [InlineData("{ 1000000000 7 roll }")]
    public void PostScript_HugeStackCounts_StayBounded(string program)
    {
        PdfFunction f = PdfFunction.Parse(PsFunction(program), Identity, Raw);
        Assert.Null(Bounded.Run(() => f.Evaluate(new[] { 0.5 }), maxBytes: 16L * 1024 * 1024));
    }

    [Fact]
    public void PostScript_PushingPastTheOperandStack_Stops()
    {
        string program = "{ " + string.Concat(Enumerable.Repeat("dup ", 5000)) + "}";
        PdfFunction f = PdfFunction.Parse(PsFunction(program), Identity, Raw);
        Assert.Null(Bounded.Run(() => f.Evaluate(new[] { 0.5 }), maxBytes: 16L * 1024 * 1024));
    }

    [Theory]
    [InlineData("{ pop -1e30 -1 idiv }")]
    [InlineData("{ pop -9223372036854775808 -1 idiv }")]
    [InlineData("{ pop -9223372036854775808 -1 mod }")]
    [InlineData("{ pop 1e300 cvi }")]
    [InlineData("{ pop 1 1e300 bitshift }")]
    public void PostScript_IntegerEdgeCases_DoNotThrow(string program)
    {
        PdfFunction f = PdfFunction.Parse(PsFunction(program), Identity, Raw);
        Assert.Single(f.Evaluate(new[] { 0.5 }));
    }

    // ---- image conversion ----

    [Fact]
    public void ColorConverter_TooManyPixels_IsInvalidData()
    {
        Exception? ex = Bounded.Run(() => ColorConverter.ToRgb(new byte[1], 100_000, 100_000, 3, 8, PdfColorSpace.Resolve(new PdfName("DeviceRGB"), Identity, Raw)));
        Assert.IsType<InvalidDataException>(ex);

        // width × height overflows int: still caught by the long arithmetic.
        ex = Bounded.Run(() => ColorConverter.ToRgb(new byte[1], 65_536, 65_536, 1, 8, PdfColorSpace.Resolve(new PdfName("DeviceGray"), Identity, Raw)));
        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void ColorConverter_HugeBitDepthOrComponents_IsInvalidData()
    {
        PdfColorSpace rgb = PdfColorSpace.Resolve(new PdfName("DeviceRGB"), Identity, Raw);
        Assert.IsType<InvalidDataException>(Bounded.Run(() => ColorConverter.ToRgb(new byte[1], 1, 1, 3, 1_000_000_000, rgb)));
        Assert.IsType<InvalidDataException>(Bounded.Run(() => ColorConverter.ToRgb(new byte[1], 1, 1, 1_000_000_000, 8, rgb)));
    }

    [Fact]
    public void ColorConverter_LargeSeparationImage_CachesTheTintTransform()
    {
        // 4096 x 4096 pixels of the same tint through a 400-operator function: one evaluation, not 16 M.
        string program = "{ " + string.Concat(Enumerable.Repeat("dup mul ", 200)) + "}";
        PdfArray csArr = Arr(new PdfName("Separation"), new PdfName("Spot"), new PdfName("DeviceGray"), PsFunction(program));
        PdfColorSpace cs = PdfColorSpace.Resolve(csArr, Identity, Raw);

        RasterImage? img = null;
        Assert.Null(Bounded.Run(() => img = ColorConverter.ToRgb(new byte[] { 0x80 }, 4096, 4096, 1, 8, cs)));
        Assert.Equal(4096 * 4096 * 3, img!.Samples.Length);
    }

    [Fact]
    public void ColorConverter_ExpensiveUncachedTints_HitTheWorkLimit()
    {
        // DeviceN with 16 colourants (128 bits per pixel, too wide to cache) through a 16-input sampled
        // function: each distinct pixel costs 2^16 × 18 units, so the budget runs out after ~1,700 pixels.
        PdfObject[] names = Enumerable.Range(0, 16).Select(i => (PdfObject)new PdfName("N" + i)).ToArray();
        PdfArray csArr = Arr(new PdfName("DeviceN"), Arr(names), new PdfName("DeviceGray"), Sampled(16, 8));
        PdfColorSpace cs = PdfColorSpace.Resolve(csArr, Identity, Raw);

        var data = new byte[4096 * 16];
        Exception? ex = Bounded.Run(() => ColorConverter.ToRgb(data, 4096, 1, 16, 8, cs), seconds: 30);
        Assert.IsType<InvalidDataException>(ex);
        Assert.Equal("MaxTintWork", Assert.IsType<PdfLimitExceededException>(ex!.InnerException).LimitName);
    }

    // ---- injected PdfLimits (ADR-0010 phase 2) ----

    [Fact]
    public void ColorConverter_OverTheInjectedPixelLimit_IsInvalidDataNamingTheLimit()
    {
        PdfColorSpace rgb = PdfColorSpace.Resolve(new PdfName("DeviceRGB"), Identity, Raw);
        var limits = new PdfLimits { MaxImagePixels = 15 };

        var ex = Assert.IsType<InvalidDataException>(Bounded.Run(() => ColorConverter.ToRgb(new byte[48], limits, 4, 4, 3, 8, rgb)));
        var inner = Assert.IsType<PdfLimitExceededException>(ex.InnerException);
        Assert.Equal(nameof(PdfLimits.MaxImagePixels), inner.LimitName);
        Assert.Equal(15, inner.Limit);

        var raster = new RasterImage { Width = 4, Height = 4, Components = 3, Samples = new byte[48] };
        ex = Assert.IsType<InvalidDataException>(Bounded.Run(() => ColorConverter.ToRgb(raster, limits, rgb)));
        Assert.Equal(nameof(PdfLimits.MaxImagePixels), Assert.IsType<PdfLimitExceededException>(ex.InnerException).LimitName);

        // The same image within the default limits converts as before.
        Assert.Equal(48, ColorConverter.ToRgb(new byte[48], 4, 4, 3, 8, rgb).Samples.Length);
        Assert.Equal(48, ColorConverter.ToRgb(raster, rgb).Samples.Length);
    }

    [Fact]
    public void ColorConverter_NullLimits_AreRejected()
    {
        PdfColorSpace rgb = PdfColorSpace.Resolve(new PdfName("DeviceRGB"), Identity, Raw);
        var raster = new RasterImage { Width = 1, Height = 1, Components = 3, Samples = new byte[3] };

        Assert.Throws<ArgumentNullException>(() => ColorConverter.ToRgb(new byte[3], null!, 1, 1, 3, 8, rgb));
        Assert.Throws<ArgumentNullException>(() => ColorConverter.ToRgb(raster, (PdfLimits)null!, rgb));
        Assert.Throws<ArgumentNullException>(() => ColorConverter.ToRgb(null!, PdfLimits.Default, 1, 1, 3, 8, rgb));
    }
}

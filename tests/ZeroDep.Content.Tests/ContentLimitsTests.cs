using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using ZeroDep.Abstractions;
using ZeroDep.Content;
using ZeroDep.Objects;

namespace ZeroDep.Content.Tests;

/// <summary>
/// ADR-0010 Phase 1 — crafted content streams and font maps terminate in bounded memory: operand nesting,
/// Form XObject fan-out and self-reference, the q-stack, numeric casts, and ToUnicode / CID /W sizes.
/// </summary>
public sealed class ContentLimitsTests
{
    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var map = new Dictionary<string, PdfObject>();
        foreach (var (key, value) in entries) map[key] = value;
        return new PdfDictionary(map);
    }

    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    private static PdfReference Ref(int n) => new PdfReference(n, 0);

    private static ContentInterpreter Interpreter(Dictionary<int, PdfObject>? objects = null, Func<PdfStream, byte[]>? decode = null)
        => new ContentInterpreter(
            o => objects is not null && o is PdfReference r && objects.TryGetValue(r.ObjectNumber, out PdfObject? v) ? v : o,
            decode ?? (s => s.GetRawBytes()));

    private static PdfStream Form(string content, PdfDictionary resources)
        => new PdfStream(Dict(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Form")), ("Resources", resources)), B(content));

    [Theory]
    [InlineData("[", " Tj")]
    [InlineData("<< /A ", " >>")]
    public void DeeplyNestedOperands_AreRejectedWithoutStackOverflow(string open, string tail)
    {
        string content = string.Concat(Enumerable.Repeat(open, 100_000)) + tail;
        Exception? ex = Bounded.Run(() => Interpreter().RunAll(B(content), Dict(), Matrix.Identity));
        Assert.IsType<PdfSyntaxException>(ex);
    }

    [Fact]
    public void DeeplyNestedInlineImageValue_IsRejectedWithoutStackOverflow()
    {
        string content = "BI /D " + new string('[', 100_000) + " ID x EI";
        Exception? ex = Bounded.Run(() => Interpreter().RunAll(B(content), Dict(), Matrix.Identity));
        Assert.IsType<PdfSyntaxException>(ex);
    }

    [Fact]
    public void OperandsNestedWithinTheLimit_AreAccepted()
    {
        string content = new string('[', 50) + new string(']', 50) + " pop";
        Assert.Null(Bounded.Run(() => Interpreter().RunAll(B(content), Dict(), Matrix.Identity)));
    }

    [Fact]
    public void FormXObjectFanOut_HitsTheWorkLimit()
    {
        // Twelve levels; each form invokes the next 20 times: 20^12 runs without a budget.
        var objects = new Dictionary<int, PdfObject>();
        const int Levels = 12;
        string body = string.Concat(Enumerable.Repeat("/F Do ", 20));
        for (int i = 0; i < Levels; i++)
        {
            bool last = i == Levels - 1;
            PdfDictionary res = last ? Dict() : Dict(("XObject", Dict(("F", Ref(100 + i + 1)))));
            objects[100 + i] = Form(last ? "1 0 0 1 0 0 cm" : body, res);
        }

        int decodes = 0;
        ContentInterpreter interp = Interpreter(objects, s => { decodes++; return s.GetRawBytes(); });
        PdfDictionary pageRes = Dict(("XObject", Dict(("F", Ref(100)))));

        // This case bounds CPU, not memory: the 5 × 10^7 tokens it runs each allocate short-lived objects,
        // so the allocation total (all garbage) is bounded by the budget rather than by a small constant.
        Exception? ex = Bounded.Run(() => interp.RunAll(B("/F Do"), pageRes, Matrix.Identity), maxBytes: 8L * 1024 * 1024 * 1024);
        Assert.IsType<PdfSyntaxException>(ex);
        Assert.True(decodes <= Levels, $"each form is decoded once per page, saw {decodes} decodes");
    }

    [Fact]
    public void FormInvokingItself_IsSkipped()
    {
        var objects = new Dictionary<int, PdfObject>();
        var image = new PdfStream(Dict(("Subtype", new PdfName("Image"))), Array.Empty<byte>());
        objects[5] = Form("/F Do /F Do /Im Do", Dict(("XObject", Dict(("F", Ref(5)), ("Im", image)))));
        PdfDictionary pageRes = Dict(("XObject", Dict(("F", Ref(5)))));

        ContentResult? result = null;
        Assert.Null(Bounded.Run(() => result = Interpreter(objects).RunAll(B("/F Do"), pageRes, Matrix.Identity)));
        Assert.Single(result!.Images); // the form ran once; its self-invocations were skipped
    }

    [Fact]
    public void SameFormInvokedRepeatedly_IsDecodedOnce()
    {
        var objects = new Dictionary<int, PdfObject>();
        var image = new PdfStream(Dict(("Subtype", new PdfName("Image"))), Array.Empty<byte>());
        objects[5] = Form("/Im Do", Dict(("XObject", Dict(("Im", image)))));
        PdfDictionary pageRes = Dict(("XObject", Dict(("F", Ref(5)))));

        int decodes = 0;
        ContentResult result = Interpreter(objects, s => { decodes++; return s.GetRawBytes(); })
            .RunAll(B(string.Concat(Enumerable.Repeat("/F Do ", 1000))), pageRes, Matrix.Identity);
        Assert.Equal(1000, result.Images.Count);
        Assert.Equal(1, decodes);
    }

    [Fact]
    public void ManySaves_AreBoundedAndStillRestore()
    {
        var image = new PdfStream(Dict(("Subtype", new PdfName("Image"))), Array.Empty<byte>());
        PdfDictionary res = Dict(("XObject", Dict(("Im", image))));

        // One million unmatched q operators: the stack stays bounded.
        Assert.Null(Bounded.Run(() => Interpreter().RunAll(B(string.Concat(Enumerable.Repeat("q ", 1_000_000))), res, Matrix.Identity), maxBytes: 200L * 1024 * 1024));

        // Within the limit, q/Q still restore: 2x scale inside, identity after.
        string content = string.Concat(Enumerable.Repeat("q ", 100)) + "2 0 0 2 0 0 cm /Im Do " + string.Concat(Enumerable.Repeat("Q ", 100)) + "/Im Do";
        ContentResult result = Interpreter().RunAll(B(content), res, Matrix.Identity);
        Assert.Equal(2, result.Images[0].Transform.A, 3);
        Assert.Equal(1, result.Images[1].Transform.A, 3);
    }

    [Theory]
    [InlineData("1e300 Tr")]
    [InlineData("-1e300 Tr")]
    [InlineData("99999999999 Tr")]
    public void HugeRenderMode_DoesNotWrap(string op)
    {
        PdfDictionary font = Dict(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type1")));
        PdfDictionary res = Dict(("Font", Dict(("F1", font))));
        ContentResult result = Interpreter().RunAll(B("BT /F1 12 Tf " + op + " (A) Tj ET"), res, Matrix.Identity);
        Assert.Equal(0, Assert.Single(result.TextRuns).RenderMode);
    }

    [Fact]
    public void HugeFirstChar_DoesNotThrow()
    {
        PdfDictionary font = Dict(
            ("Type", new PdfName("Font")),
            ("Subtype", new PdfName("Type1")),
            ("FirstChar", new PdfReal(1e300)),
            ("Widths", new PdfArray(new PdfObject[] { new PdfInteger(600) })));
        PdfDictionary res = Dict(("Font", Dict(("F1", font))));
        ContentResult result = Interpreter().RunAll(B("BT /F1 12 Tf (A) Tj ET"), res, Matrix.Identity);
        Assert.Equal("A", Assert.Single(result.TextRuns).Text);
    }

    [Fact]
    public void ToUnicodeRangeBomb_IsRejected()
    {
        // 200 ranges of 65,536 codes each: 13 M entries from a few KB.
        var sb = new StringBuilder("begincmap\n200 beginbfrange\n");
        for (int i = 0; i < 200; i++) sb.Append($"<{i * 65536:X8}><{(i * 65536) + 0xFFFF:X8}><0041>\n");
        sb.Append("endbfrange\nendcmap\n");

        Exception? ex = Bounded.Run(() => ToUnicodeCMap.Parse(B(sb.ToString())));
        Assert.IsType<PdfSyntaxException>(ex);
    }

    [Fact]
    public void ToUnicodeRangeBomb_InAFont_FallsBackWithoutAMap()
    {
        var sb = new StringBuilder("begincmap\n200 beginbfrange\n");
        for (int i = 0; i < 200; i++) sb.Append($"<{i * 65536:X8}><{(i * 65536) + 0xFFFF:X8}><0041>\n");
        sb.Append("endbfrange\nendcmap\n");
        PdfDictionary font = Dict(
            ("Type", new PdfName("Font")),
            ("Subtype", new PdfName("Type1")),
            ("ToUnicode", new PdfStream(Dict(), B(sb.ToString()))));
        PdfDictionary res = Dict(("Font", Dict(("F1", font))));

        ContentResult? result = null;
        Assert.Null(Bounded.Run(() => result = Interpreter().RunAll(B("BT /F1 12 Tf (A) Tj ET"), res, Matrix.Identity)));
        Assert.Equal("A", Assert.Single(result!.TextRuns).Text); // WinAnsi fallback
    }

    private static PdfStream AToUnicode() => new PdfStream(Dict(), B("1 beginbfchar <0041> <0041> endbfchar"));

    [Fact]
    public void CidWidthsBomb_FallsBackToDefaultWidth()
    {
        // 1,000 triples of 70,000 CIDs each, with repeated ranges, would write 70 M entries.
        var items = new List<PdfObject>();
        for (int i = 0; i < 1000; i++)
        {
            items.Add(new PdfInteger(0));
            items.Add(new PdfInteger(69_999));
            items.Add(new PdfInteger(500));
        }

        PdfDictionary cidFont = Dict(("Type", new PdfName("Font")), ("Subtype", new PdfName("CIDFontType2")), ("W", new PdfArray(items.ToArray())));
        PdfDictionary font = Dict(
            ("Type", new PdfName("Font")),
            ("Subtype", new PdfName("Type0")),
            ("DescendantFonts", new PdfArray(new PdfObject[] { cidFont })),
            ("ToUnicode", AToUnicode()));
        PdfDictionary res = Dict(("Font", Dict(("F1", font))));

        ContentResult? result = null;
        Assert.Null(Bounded.Run(() => result = Interpreter().RunAll(B("BT /F1 10 Tf <0041> Tj ET"), res, Matrix.Identity)));
        Assert.Equal(10.0, Assert.Single(result!.TextRuns).Width, 3); // /DW 1000 → 10 at size 10
    }

    [Fact]
    public void CidWidths_HugeStartCid_DoesNotOverflow()
    {
        var items = new PdfObject[]
        {
            new PdfInteger(int.MaxValue),
            new PdfArray(new PdfObject[] { new PdfInteger(500), new PdfInteger(500) }),
            new PdfInteger(65),
            new PdfArray(new PdfObject[] { new PdfInteger(700) }),
        };
        PdfDictionary cidFont = Dict(("Subtype", new PdfName("CIDFontType2")), ("W", new PdfArray(items)));
        PdfDictionary font = Dict(("Subtype", new PdfName("Type0")), ("DescendantFonts", new PdfArray(new PdfObject[] { cidFont })), ("ToUnicode", AToUnicode()));
        PdfDictionary res = Dict(("Font", Dict(("F1", font))));

        ContentResult result = Interpreter().RunAll(B("BT /F1 10 Tf <0041> Tj ET"), res, Matrix.Identity);
        Assert.Equal(7.0, Assert.Single(result.TextRuns).Width, 3); // CID 65 → 700/1000 × 10
    }
}

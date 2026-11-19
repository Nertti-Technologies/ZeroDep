using System.Text;
using Xunit;
using ZeroDep.Abstractions;
using static ZeroDep.Objects.Tests.CraftedPdf;

namespace ZeroDep.Objects.Tests;

/// <summary>
/// Regression tests for crafted files that used to overflow the stack, hang, exhaust memory or raise
/// undocumented exception types in the object layer (ADR-0010 phase 1).
/// </summary>
public sealed class ResourceLimitTests
{
    private static PdfFile Open(byte[] pdf) => PdfFile.Open(new MemoryStream(pdf));

    // ---- nesting ----

    [Fact]
    public void DeeplyNestedArraysAreRejectedInsteadOfOverflowingTheStack()
    {
        const int depth = 100_000;
        var objects = new SortedDictionary<int, byte[]>
        {
            [1] = Concat(A("<< /Type /Catalog /Pages 2 0 R /X "), A(new string('[', depth)), A(new string(']', depth)), A(" >>")),
            [2] = A("<< /Type /Pages /Kids [] /Count 0 >>"),
        };
        byte[] pdf = Classic(objects);

        var ex = Bounded.Throws<PdfSyntaxException>(() =>
        {
            using PdfFile file = Open(pdf);
            file.GetObject(1);
        });
        Assert.Contains("nesting", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeeplyNestedDictionariesAreRejectedInsteadOfOverflowingTheStack()
    {
        const int depth = 100_000;
        var objects = new SortedDictionary<int, byte[]>
        {
            [1] = Concat(A(string.Concat(Enumerable.Repeat("<</A ", depth))), A(string.Concat(Enumerable.Repeat(">>", depth)))),
        };
        byte[] pdf = Classic(objects);

        Bounded.Throws<PdfSyntaxException>(() =>
        {
            using PdfFile file = Open(pdf);
            file.GetObject(1);
        });
    }

    [Fact]
    public void NestingUpToTheLimitIsAcceptedAndOneMoreIsRejected()
    {
        static PdfObject Parse(int depth)
        {
            byte[] text = A(new string('[', depth) + new string(']', depth));
            return new PdfObjectParser(text, 0, text.Length).ParseValue();
        }

        Assert.IsType<PdfArray>(Parse(Limits.MaxNestingDepth));
        Assert.Throws<PdfSyntaxException>(() => Parse(Limits.MaxNestingDepth + 1));
    }

    // ---- object streams ----

    [Fact]
    public void ObjectStreamThatContainsItselfResolvesToNull()
    {
        // Object 1 is listed as compressed inside object stream 1, i.e. inside itself.
        byte[] pdf = WithXrefStream(ms =>
        {
            long xoff = ms.Position;
            return new byte[] { 0, 0, 0, /*1*/ 2, 1, 0, /*2*/ 1, (byte)xoff, 0 };
        }, xrefNumber: 2, size: 3);

        Exception? ex = Bounded.Run(() =>
        {
            using PdfFile file = Open(pdf);
            Assert.IsType<PdfNull>(file.GetObject(1));
        });
        Assert.Null(ex);
    }

    [Fact]
    public void ObjectStreamsThatContainEachOtherResolveToNull()
    {
        // Object 1 lives in object stream 3, and object 3 in object stream 1.
        byte[] pdf = WithXrefStream(ms =>
        {
            long xoff = ms.Position;
            return new byte[] { 0, 0, 0, /*1*/ 2, 3, 0, /*2*/ 1, (byte)xoff, 0, /*3*/ 2, 1, 0 };
        }, xrefNumber: 2, size: 4);

        Exception? ex = Bounded.Run(() =>
        {
            using PdfFile file = Open(pdf);
            Assert.IsType<PdfNull>(file.GetObject(1));
            Assert.IsType<PdfNull>(file.GetObject(3));
        });
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("/N -1 /First 0")]
    [InlineData("/N 4294967296 /First 0")]
    [InlineData("/N 2147483647 /First 0")]
    [InlineData("/N 1 /First -5")]
    [InlineData("/N 1 /First 999999")]
    public void ObjectStreamWithOutOfRangeHeaderThrowsSyntaxException(string header)
    {
        byte[] pdf = WithXrefStream(ms =>
        {
            long o3 = ms.Position;
            ms.Write(A($"3 0 obj\n<< /Type /ObjStm {header} /Length 4 >>\nstream\n1 0 \nendstream\nendobj\n"));
            long xoff = ms.Position;
            return new byte[] { 0, 0, 0, /*1*/ 2, 3, 0, /*2*/ 1, (byte)xoff, 0, /*3*/ 1, (byte)o3, 0 };
        }, xrefNumber: 2, size: 4);

        Bounded.Throws<PdfSyntaxException>(() =>
        {
            using PdfFile file = Open(pdf);
            file.GetObject(1);
        });
    }

    // ---- cross-reference ----

    [Theory]
    [InlineData("[-5 10 1]")]
    [InlineData("[1 9 1]")]
    [InlineData("[1 4294967297 1]")]
    public void CrossReferenceStreamWithOutOfRangeWidthsThrowsSyntaxException(string widths)
    {
        var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        long xoff = ms.Position;
        byte[] data = new byte[64];
        ms.Write(A($"2 0 obj\n<< /Type /XRef /W {widths} /Size 3 /Root 1 0 R /Length {data.Length} >>\nstream\n"));
        ms.Write(data);
        ms.Write(A($"\nendstream\nendobj\nstartxref\n{xoff}\n%%EOF\n"));
        byte[] pdf = ms.ToArray();

        Bounded.Throws<PdfSyntaxException>(() => Open(pdf).Dispose());
    }

    [Fact]
    public void XRefStmOutsideTheFileIsIgnored()
    {
        byte[] pdf = Classic(BasicPage(A("q Q")), "/XRefStm 999999999");

        Exception? ex = Bounded.Run(() =>
        {
            using PdfFile file = Open(pdf);
            Assert.IsType<PdfDictionary>(file.GetObject(1));
        });
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("99999999999")]
    public void StartXrefOutsideTheFileThrowsSyntaxException(string offset)
    {
        byte[] pdf = A($"%PDF-1.7\n1 0 obj << >> endobj\nstartxref\n{offset}\n%%EOF\n");
        Bounded.Throws<PdfSyntaxException>(() => Open(pdf).Dispose());
    }

    [Fact]
    public void ObjectOffsetOutsideTheFileThrowsSyntaxException()
    {
        byte[] pdf = WithXrefStream(ms =>
        {
            long xoff = ms.Position;
            // Object 1 is "in use" at offset 0xFF..FF (8-byte field would be needed; use the 1-byte max beyond EOF).
            return new byte[] { 0, 0, 0, /*1*/ 1, 0xFF, 0, /*2*/ 1, (byte)xoff, 0 };
        }, xrefNumber: 2, size: 3);

        Bounded.Throws<PdfSyntaxException>(() =>
        {
            using PdfFile file = Open(pdf);
            file.GetObject(1);
        });
    }

    [Theory]
    [InlineData("<< /A 4294967297 0 R >>")]
    [InlineData("<< /A 1 4294967296 R >>")]
    [InlineData("<< /A -1 0 R >>")]
    public void ReferenceNumbersOutsideTheIntRangeAreRejected(string body)
    {
        byte[] text = A(body);
        Assert.Throws<PdfSyntaxException>(() => new PdfObjectParser(text, 0, text.Length).ParseValue());
    }

    [Fact]
    public void IndirectObjectNumberOutsideTheIntRangeIsRejected()
    {
        var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        long o1 = ms.Position;
        ms.Write(A("4294967297 0 obj\n<< /Type /Catalog >>\nendobj\n"));
        long xref = ms.Position;
        ms.Write(A($"xref\n0 2\n0000000000 65535 f \n{o1:D10} 00000 n \ntrailer\n<< /Size 2 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        byte[] pdf = ms.ToArray();

        Bounded.Throws<PdfSyntaxException>(() =>
        {
            using PdfFile file = Open(pdf);
            file.GetObject(1);
        });
    }

    // ---- object loading ----

    [Fact]
    public void ManyObjectsWithoutEndobjLoadInBoundedMemory()
    {
        // 2000 page objects without 'endobj', followed by 1 MiB of padding: reading each one up to EOF
        // used to allocate about 2000 x 1 MiB.
        const int n = 2000;
        const int pad = 1024 * 1024;
        var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        long cat = ms.Position;
        ms.Write(A("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"));
        long pages = ms.Position;
        var kids = new StringBuilder();
        for (int i = 0; i < n; i++) kids.Append($"{i + 3} 0 R ");
        ms.Write(A($"2 0 obj << /Type /Pages /Kids [{kids}] /Count {n} >> endobj\n"));
        var offsets = new long[n];
        for (int i = 0; i < n; i++)
        {
            offsets[i] = ms.Position;
            ms.Write(A($"{i + 3} 0 obj << /Type /Page /Parent 2 0 R >>\n"));
        }

        ms.Write(A(new string(' ', pad)));
        long xref = ms.Position;
        var sb = new StringBuilder($"xref\n0 {n + 3}\n0000000000 65535 f \n{cat:D10} 00000 n \n{pages:D10} 00000 n \n");
        foreach (long o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {n + 3} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(A(sb.ToString()));
        byte[] pdf = ms.ToArray();

        Exception? ex = Bounded.Run(() =>
        {
            using PdfFile file = Open(pdf);
            for (int i = 0; i < n; i++)
            {
                var page = Assert.IsType<PdfDictionary>(file.GetObject(i + 3));
                Assert.Equal("Page", ((PdfName)page["Type"]!).Value);
            }
        });
        Assert.Null(ex);
    }

    [Fact]
    public void StreamWithoutEndobjLongerThanTheFirstWindowIsReadWhole()
    {
        // The window must grow past 64 KiB until 'endstream' is inside it.
        byte[] data = new byte[200_000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)'a';
        var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        long o1 = ms.Position;
        ms.Write(A($"1 0 obj\n<< /Length 7 >>\nstream\n"));
        ms.Write(data);
        ms.Write(A("\nendstream\n"));
        long xref = ms.Position;
        ms.Write(A($"xref\n0 2\n0000000000 65535 f \n{o1:D10} 00000 n \ntrailer\n<< /Size 2 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));

        using PdfFile file = Open(ms.ToArray());
        var stream = Assert.IsType<PdfStream>(file.GetObject(1));
        Assert.Equal(data.Length, stream.GetRawBytes().Length);
    }
}

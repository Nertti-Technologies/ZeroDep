using System.IO.Compression;
using Xunit;
using ZeroDep.Abstractions;
using static ZeroDep.Objects.Tests.CraftedPdf;

namespace ZeroDep.Objects.Tests;

/// <summary>
/// The per-document <see cref="ResourceBudget"/> (ADR-0010 option A3): object reads charge the document work
/// budget, the object window follows <see cref="PdfLimits.MaxDecodedStreamBytes"/>, and the structural stream
/// decoder reports a decoder's limit breach as <see cref="PdfLimitExceededException"/>.
/// </summary>
public sealed class DocumentBudgetTests
{
    private static byte[] Zlib(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }

        return ms.ToArray();
    }

    private static byte[] OnePage() => Classic(BasicPage(A("BT ET")));

    [Fact]
    public void Open_UsesTheGivenLimitsAndOwnsANewBudget()
    {
        var limits = new PdfLimits { MaxWorkPerDocument = 1_000, WorkPerSourceByte = 0 };

        using PdfFile file = PdfFile.Open(new MemoryStream(OnePage()), null, limits);
        using PdfFile other = PdfFile.Open(new MemoryStream(OnePage()), null, limits);

        Assert.Same(limits, file.Budget.Limits);
        Assert.NotSame(file.Budget, other.Budget);
        Assert.Same(PdfLimits.Default, PdfFile.Open(new MemoryStream(OnePage())).Budget.Limits);
    }

    [Fact]
    public void Open_SizesTheBudgetFromTheSourceLength()
    {
        byte[] pdf = OnePage();
        var limits = new PdfLimits { MaxWorkPerDocument = 1_000, WorkPerSourceByte = 3 };

        using PdfFile file = PdfFile.Open(new MemoryStream(pdf), null, limits);

        Assert.Equal(pdf.Length, file.Budget.SourceLength);
        Assert.Equal(1_000 + (3L * pdf.Length), file.Budget.MaxWork);
    }

    [Fact]
    public void Charge_OverTheAllowance_NamesHowTheBudgetWasDerived()
    {
        var budget = new ResourceBudget(new PdfLimits { MaxWorkPerDocument = 10, WorkPerSourceByte = 2 }, 5);

        var ex = Assert.Throws<PdfLimitExceededException>(() => budget.Charge(21));

        Assert.Equal(nameof(PdfLimits.MaxWorkPerDocument), ex.LimitName);
        Assert.Equal(20, ex.Limit);
        Assert.Equal("Document exceeds the work limit of 20 (10 base + 2 per byte for 5 bytes).", ex.Message);
    }

    [Fact]
    public void Charge_WithoutAnAllowance_KeepsThePlainMessage()
    {
        var budget = new ResourceBudget(new PdfLimits { MaxWorkPerDocument = 10 });

        Assert.Equal("Document exceeds the work limit of 10.", Assert.Throws<PdfLimitExceededException>(() => budget.Charge(11)).Message);
    }

    [Fact]
    public void ObjectReads_ChargeTheDocumentBudget()
    {
        using PdfFile file = PdfFile.Open(new MemoryStream(OnePage()), null, new PdfLimits { MaxWorkPerDocument = 1_000, WorkPerSourceByte = 0 });

        Assert.Equal(0, file.Budget.Work);
        file.GetObject(1);
        long afterOne = file.Budget.Work;
        Assert.True(afterOne >= 1, "an object read costs at least one unit");

        file.GetObject(1); // cached: no further read
        Assert.Equal(afterOne, file.Budget.Work);
    }

    [Fact]
    public void ObjectReads_OverTheDocumentBudget_AreRejected()
    {
        using PdfFile file = PdfFile.Open(new MemoryStream(OnePage()), null, new PdfLimits { MaxWorkPerDocument = 1, WorkPerSourceByte = 0 });

        file.GetObject(1);
        var ex = Assert.Throws<PdfLimitExceededException>(() => file.GetObject(2));
        Assert.Equal(nameof(PdfLimits.MaxWorkPerDocument), ex.LimitName);
        Assert.Equal(1, ex.Limit);

        // The breach is sticky: every later read fails the same way.
        Assert.Equal(nameof(PdfLimits.MaxWorkPerDocument), Assert.Throws<PdfLimitExceededException>(() => file.GetObject(3)).LimitName);
    }

    [Fact]
    public void ManyObjectsWithoutEndobj_AreBoundedByTheDocumentBudget()
    {
        // 200 stream objects without 'endstream'/'endobj', each 2 KiB apart: every one of them reads windows up
        // to the end of the file. The document budget, not the per-object window, bounds the total.
        var objects = new SortedDictionary<int, byte[]>
        {
            [1] = A("<< /Type /Catalog /Pages 2 0 R >>"),
            [2] = A("<< /Type /Pages /Kids [] /Count 0 >>"),
        };
        var body = new MemoryStream();
        var offsets = new List<long>();
        body.Write(A("%PDF-1.7\n"));
        foreach (var kv in objects)
        {
            offsets.Add(body.Position);
            body.Write(A($"{kv.Key} 0 obj\n"));
            body.Write(kv.Value);
            body.Write(A("\nendobj\n"));
        }

        for (int i = 0; i < 200; i++)
        {
            offsets.Add(body.Position);
            body.Write(A($"{i + 3} 0 obj\n<< /Length 99999999 >>\nstream\n"));
            body.Write(new byte[2048]);
        }

        long xref = body.Position;
        body.Write(A($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n"));
        foreach (long o in offsets) body.Write(A($"{o:D10} 00000 n \n"));
        body.Write(A($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        byte[] pdf = body.ToArray();

        var limits = new PdfLimits { MaxWorkPerDocument = 20_000, WorkPerSourceByte = 0 };
        Exception? ex = Bounded.Run(() =>
        {
            using PdfFile file = PdfFile.Open(new MemoryStream(pdf), null, limits);
            for (int n = 3; n < 203; n++)
            {
                try
                {
                    file.GetObject(n);
                }
                catch (PdfSyntaxException e) when (e is not PdfLimitExceededException)
                {
                    // a malformed object on its own is fine; only the budget ends the walk
                }
            }
        });

        Assert.Equal(nameof(PdfLimits.MaxWorkPerDocument), Assert.IsType<PdfLimitExceededException>(ex).LimitName);
    }

    [Fact]
    public void ObjectWindow_FollowsTheDecodedStreamLimit()
    {
        // A 3 KB array object with no 'endobj', last before the cross-reference table: the default 64 KiB window
        // reaches the end of the file and parses it, but a 1,000-byte window can never hold the whole array.
        byte[] head = A("%PDF-1.7\n");
        byte[] o1 = A("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        byte[] o2 = A("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        byte[] o3 = A("3 0 obj\n[" + string.Concat(Enumerable.Repeat("1 ", 1500)) + "]\n");
        long off1 = head.Length, off2 = off1 + o1.Length, off3 = off2 + o2.Length, xref = off3 + o3.Length;
        byte[] file = Concat(
            head, o1, o2, o3,
            A($"xref\n0 4\n0000000000 65535 f \n{off1:D10} 00000 n \n{off2:D10} 00000 n \n{off3:D10} 00000 n \n"),
            A($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));

        using PdfFile small = PdfFile.Open(new MemoryStream(file), null, new PdfLimits { MaxDecodedStreamBytes = 1_000 });
        var ex = Assert.Throws<PdfLimitExceededException>(() => small.GetObject(3));
        Assert.Equal(nameof(PdfLimits.MaxDecodedStreamBytes), ex.LimitName);
        Assert.Equal(1_000, ex.Limit);

        using PdfFile normal = PdfFile.Open(new MemoryStream(file));
        Assert.IsType<PdfArray>(normal.GetObject(3));
    }

    [Fact]
    public void StreamDecoder_ReportsADecoderBreachAsLimitExceeded()
    {
        var stream = new PdfStream(
            new PdfDictionary(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }),
            Zlib(new byte[500]));

        var ex = Assert.Throws<PdfLimitExceededException>(() => StreamDecoder.Decode(stream, new PdfLimits { MaxDecodedStreamBytes = 100 }));
        Assert.Equal(nameof(PdfLimits.MaxDecodedStreamBytes), ex.LimitName);
        Assert.Equal(100, ex.Limit);
        Assert.IsType<InvalidDataException>(ex.InnerException);

        Assert.Equal(500, StreamDecoder.Decode(stream).Length);
    }

    [Fact]
    public void ResourceBudget_NamesOnlyTheDocumentBudgetAsDocumentFatal()
    {
        var page = new PdfLimitExceededException(nameof(PdfLimits.MaxWorkPerPage), 1, "page");
        var document = new PdfLimitExceededException(nameof(PdfLimits.MaxWorkPerDocument), 1, "document");

        Assert.False(ResourceBudget.IsDocumentLimit(page));
        Assert.True(ResourceBudget.IsDocumentLimit(document));
        Assert.True(ResourceBudget.IsDocumentLimit(new InvalidDataException("wrapped", document)));
        Assert.False(ResourceBudget.IsDocumentLimit(new InvalidOperationException("other", document)));
        Assert.Same(page, ResourceBudget.AsLimitExceeded(new InvalidDataException("wrapped", page)));
        Assert.Null(ResourceBudget.AsLimitExceeded(new InvalidDataException("plain")));
        Assert.Throws<ArgumentNullException>(() => new ResourceBudget(null!));
    }
}

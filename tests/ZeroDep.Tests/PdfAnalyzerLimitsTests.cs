using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;
using ZeroDep.Abstractions;

namespace ZeroDep.Tests;

/// <summary>
/// The resource limits through the public facade (ADR-0010 phase 2): each budget, set low through
/// <see cref="AnalyzerOptions.Limits"/>, gives its documented outcome — a <see cref="RejectionReason.ResourceLimitExceeded"/>
/// rejection, a <see cref="PdfLimitExceededException"/> naming the limit, or an incomplete page with a reason
/// code — and the overloads without options behave as before.
/// </summary>
public sealed class PdfAnalyzerLimitsTests
{
    private static AnalyzerOptions With(PdfLimits limits) => new AnalyzerOptions { Limits = limits };

    private static MemoryStream S(byte[] pdf) => new MemoryStream(pdf);

    // ---- document work budget ----

    [Fact]
    public void Analyze_DocumentOverItsWorkBudget_IsRejected()
    {
        // Three pages of 1,000 operators each: every page fits the page budget, but the analysis passes over
        // all of them exceed the document budget.
        byte[] pdf = Pages(Ops(1_000), Ops(1_000), Ops(1_000));
        var limits = new PdfLimits { MaxWorkPerPage = 2_000, MaxWorkPerDocument = 4_000, WorkPerSourceByte = 0 };

        DocumentAnalysis analysis = PdfAnalyzer.Analyze(S(pdf), With(limits));

        Assert.Equal(DocumentStatus.Rejected, analysis.Status);
        Assert.Equal(RejectionReason.ResourceLimitExceeded, analysis.Rejection!.Reason);
        Assert.Contains("4000", analysis.Rejection.Detail, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Processed, PdfAnalyzer.Analyze(S(pdf)).Status);
    }

    [Fact]
    public void Analyze_ContentHeavyFile_IsAdmittedByThePerByteAllowance()
    {
        // The ADR-0011 case in miniature: content-heavy pages need more than the base budget, and the
        // allowance for the file's own size covers them.
        byte[] pdf = Pages(Ops(1_000), Ops(1_000), Ops(1_000));
        var noAllowance = new PdfLimits { MaxWorkPerPage = 2_000, MaxWorkPerDocument = 4_000, WorkPerSourceByte = 0 };
        var allowance = new PdfLimits { MaxWorkPerPage = 2_000, MaxWorkPerDocument = 4_000, WorkPerSourceByte = 16 };

        Assert.Equal(DocumentStatus.Rejected, PdfAnalyzer.Analyze(S(pdf), With(noAllowance)).Status);
        Assert.Equal(DocumentStatus.Processed, PdfAnalyzer.Analyze(S(pdf), With(allowance)).Status);
    }

    [Fact]
    public void Extraction_DocumentOverItsWorkBudget_ThrowsLimitExceeded()
    {
        byte[] pdf = Pages(Ops(10));
        AnalyzerOptions options = With(new PdfLimits { MaxWorkPerDocument = 1, WorkPerSourceByte = 0 });

        foreach (Action<Stream, AnalyzerOptions> extract in Extractors())
        {
            var ex = Assert.Throws<PdfLimitExceededException>(() => extract(S(pdf), options));
            Assert.Equal(nameof(PdfLimits.MaxWorkPerDocument), ex.LimitName);
            Assert.Equal(1, ex.Limit);
        }
    }

    [Fact]
    public void Extraction_DocumentWorkAcrossPages_IsBounded()
    {
        byte[] pdf = Pages(Ops(1_000), Ops(1_000), Ops(1_000));
        AnalyzerOptions options = With(new PdfLimits { MaxWorkPerPage = 2_000, MaxWorkPerDocument = 2_500, WorkPerSourceByte = 0 });

        var ex = Assert.Throws<PdfLimitExceededException>(() => PdfAnalyzer.ExtractText(S(pdf), options));
        Assert.Equal(nameof(PdfLimits.MaxWorkPerDocument), ex.LimitName);
        Assert.Empty(PdfAnalyzer.ExtractText(S(pdf)));
    }

    // ---- page work budget ----

    [Fact]
    public void Analyze_PageOverItsWorkBudget_IsKeptAndMarkedIncomplete()
    {
        byte[] pdf = Pages(Ops(500) + " BT /F1 12 Tf (lost) Tj ET", "BT /F1 12 Tf (kept) Tj ET");

        DocumentAnalysis analysis = PdfAnalyzer.Analyze(S(pdf), With(new PdfLimits { MaxWorkPerPage = 100 }));

        Assert.Equal(DocumentStatus.Processed, analysis.Status);
        Assert.Equal(2, analysis.Pages.Count);
        Assert.True(analysis.Pages[0].Incomplete);
        Assert.Equal(new[] { "PageWorkLimit" }, analysis.Pages[0].IncompleteReasons);
        Assert.False(analysis.Pages[1].Incomplete);
        Assert.Empty(analysis.Pages[1].IncompleteReasons);
        Assert.Equal("kept", Assert.Single(analysis.TextRuns).Text);

        DocumentAnalysis normal = PdfAnalyzer.Analyze(S(pdf));
        Assert.All(normal.Pages, p => Assert.False(p.Incomplete));
        Assert.Equal(2, normal.TextRuns.Count);
    }

    [Fact]
    public void ExtractText_PageOverItsWorkBudget_IsSkipped()
    {
        byte[] pdf = Pages(Ops(500) + " BT /F1 12 Tf (lost) Tj ET", "BT /F1 12 Tf (kept) Tj ET");

        IReadOnlyList<TextRunInfo> runs = PdfAnalyzer.ExtractText(S(pdf), With(new PdfLimits { MaxWorkPerPage = 100 }));

        Assert.Equal("kept", Assert.Single(runs).Text);
        Assert.Contains("kept", PdfAnalyzer.ExtractPlainText(S(pdf), With(new PdfLimits { MaxWorkPerPage = 100 })), StringComparison.Ordinal);
    }

    // ---- decoded stream size ----

    [Fact]
    public void Analyze_ContentStreamOverTheDecodedLimit_MarksThePageIncomplete()
    {
        // 5,000 bytes of content, Flate-compressed to a few dozen: the decoded size breaks a 4,096-byte limit.
        string content = "BT /F1 12 Tf (big) Tj ET" + new string(' ', 5_000);
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("/Filter /FlateDecode", Zlib(Encoding.ASCII.GetBytes(content))),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        DocumentAnalysis analysis = PdfAnalyzer.Analyze(S(pdf), With(new PdfLimits { MaxDecodedStreamBytes = 4_096 }));

        Assert.Equal(DocumentStatus.Processed, analysis.Status);
        Assert.Equal(new[] { "ContentLimitExceeded" }, Assert.Single(analysis.Pages).IncompleteReasons);
        Assert.Empty(analysis.TextRuns);
        Assert.Equal("big", Assert.Single(PdfAnalyzer.Analyze(S(pdf)).TextRuns).Text);
    }

    [Fact]
    public void Analyze_ObjectLargerThanTheDecodedLimit_IsRejected()
    {
        // The page dictionary has no 'endobj' nearby and does not fit a 1,000-byte object window.
        string pad = string.Concat(Enumerable.Repeat("/K 1 ", 400));
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " + pad + ">>");

        DocumentAnalysis analysis = PdfAnalyzer.Analyze(S(pdf), With(new PdfLimits { MaxDecodedStreamBytes = 1_000 }));

        Assert.Equal(RejectionReason.ResourceLimitExceeded, analysis.Rejection!.Reason);
        var ex = Assert.Throws<PdfLimitExceededException>(() => PdfAnalyzer.ExtractText(S(pdf), With(new PdfLimits { MaxDecodedStreamBytes = 1_000 })));
        Assert.Equal(nameof(PdfLimits.MaxDecodedStreamBytes), ex.LimitName);
        Assert.Equal(DocumentStatus.Processed, PdfAnalyzer.Analyze(S(pdf)).Status);
    }

    // ---- font maps ----

    [Fact]
    public void Analyze_FontMapOverTheEntryLimit_MarksThePageIncomplete()
    {
        byte[] cmap = Encoding.ASCII.GetBytes("begincmap\n3 beginbfchar\n<41><0058>\n<42><0059>\n<43><005A>\nendbfchar\nendcmap\n");
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream(string.Empty, Encoding.ASCII.GetBytes("BT /F1 12 Tf (AB) Tj ET")),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>",
            Stream(string.Empty, cmap));

        DocumentAnalysis analysis = PdfAnalyzer.Analyze(S(pdf), With(new PdfLimits { MaxCMapEntries = 2 }));

        PageClassification page = Assert.Single(analysis.Pages);
        Assert.True(page.Incomplete);
        Assert.Equal(new[] { "FontMapLimitExceeded" }, page.IncompleteReasons);
        Assert.Equal("AB", Assert.Single(analysis.TextRuns).Text);

        DocumentAnalysis normal = PdfAnalyzer.Analyze(S(pdf));
        Assert.False(Assert.Single(normal.Pages).Incomplete);
        Assert.Equal("XY", Assert.Single(normal.TextRuns).Text);
    }

    // ---- form fields ----

    [Fact]
    public void FormOverTheFieldLimit_IsRejectedOrThrows()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /T (a) /FT /Tx /V (1) >>",
            "<< /T (b) /FT /Tx /V (2) >>",
            "<< /T (c) /FT /Tx /V (3) >>");
        AnalyzerOptions options = With(new PdfLimits { MaxFormFields = 2 });

        DocumentAnalysis analysis = PdfAnalyzer.Analyze(S(pdf), options);
        Assert.Equal(RejectionReason.ResourceLimitExceeded, analysis.Rejection!.Reason);

        var ex = Assert.Throws<PdfLimitExceededException>(() => PdfAnalyzer.ExtractForm(S(pdf), options));
        Assert.Equal(nameof(PdfLimits.MaxFormFields), ex.LimitName);
        Assert.Equal(2, ex.Limit);

        Assert.Equal(3, PdfAnalyzer.ExtractForm(S(pdf)).Fields.Count);
        Assert.Equal(3, PdfAnalyzer.ExtractForm(S(pdf), With(new PdfLimits { MaxFormFields = 3 })).Fields.Count);
    }

    // ---- image pixels ----

    [Fact]
    public void ExtractColorImages_ImageOverThePixelLimit_IsSkipped()
    {
        byte[] pdf = ImagePdf(10, 10);

        Assert.Empty(PdfAnalyzer.ExtractColorImages(S(pdf), With(new PdfLimits { MaxImagePixels = 50 })));
        Assert.Single(PdfAnalyzer.ExtractColorImages(S(pdf), With(new PdfLimits { MaxImagePixels = 100 })));
        Assert.Single(PdfAnalyzer.ExtractColorImages(S(pdf)));
    }

    // ---- fixed structural limits ----

    [Fact]
    public void NestingBeyondTheFixedDepth_IsReportedAsALimit()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /X " + new string('[', 1_000) + new string(']', 1_000) + " >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        DocumentAnalysis analysis = PdfAnalyzer.Analyze(S(pdf));
        Assert.Equal(RejectionReason.ResourceLimitExceeded, analysis.Rejection!.Reason);

        var ex = Assert.Throws<PdfLimitExceededException>(() => PdfAnalyzer.ExtractText(S(pdf)));
        Assert.Equal("MaxNestingDepth", ex.LimitName);
        Assert.Equal(256, ex.Limit);
    }

    // ---- JSON ----

    [Fact]
    public void ToJson_SerializesTheNewReasonAndTheIncompleteSignal()
    {
        byte[] heavy = Pages(Ops(1_000), Ops(1_000), Ops(1_000));
        string rejected = PdfAnalyzer.ToJson(S(heavy), With(new PdfLimits { MaxWorkPerPage = 2_000, MaxWorkPerDocument = 4_000, WorkPerSourceByte = 0 }));
        Assert.Contains("\"schemaVersion\":\"1.3\"", rejected, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"ResourceLimitExceeded\"", rejected, StringComparison.Ordinal);

        byte[] pdf = Pages(Ops(500), "BT ET");
        string json = PdfAnalyzer.ToJson(S(pdf), With(new PdfLimits { MaxWorkPerPage = 100 }), indent: false);
        Assert.Contains("\"incomplete\":true,\"incompleteReasons\":[\"PageWorkLimit\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"incomplete\":false,\"incompleteReasons\":[]", json, StringComparison.Ordinal);
    }

    // ---- the options overloads and the existing overloads ----

    [Fact]
    public void OptionsOverloads_RejectNullArguments()
    {
        var options = new AnalyzerOptions();
        byte[] pdf = Pages("BT ET");

        foreach (Action<Stream, AnalyzerOptions> call in Extractors().Append((s, o) => PdfAnalyzer.Analyze(s, o)).Append((s, o) => PdfAnalyzer.ToJson(s, o)))
        {
            Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() => call(null!, options)).ParamName);
            Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => call(S(pdf), null!)).ParamName);
        }
    }

    [Fact]
    public void OptionsOverloads_UseTheThresholdAndThePassword()
    {
        byte[] pdf = ImagePdf(10, 10);

        Assert.Equal(300, Assert.Single(PdfAnalyzer.AnalyzeImageDpi(S(pdf), new AnalyzerOptions { DpiThreshold = 300 })).Threshold);
        Assert.Equal(300, Assert.Single(PdfAnalyzer.Analyze(S(pdf), new AnalyzerOptions { DpiThreshold = 300 }).Images).Threshold);
        Assert.Equal(DocumentStatus.Processed, PdfAnalyzer.Analyze(S(pdf), new AnalyzerOptions { Password = "unused" }).Status);
    }

    [Fact]
    public void ExistingOverloads_MatchTheOptionsOverloadsWithDefaults()
    {
        byte[] pdf = Pages("BT /F1 12 Tf (one) Tj ET", "BT /F1 12 Tf (two) Tj ET");
        byte[] image = ImagePdf(10, 10);
        var defaults = new AnalyzerOptions();

        Assert.Equal(PdfAnalyzer.ToJson(S(pdf)), PdfAnalyzer.ToJson(S(pdf), defaults));
        Assert.Equal(PdfAnalyzer.ToJson(S(pdf), indent: true), PdfAnalyzer.ToJson(S(pdf), defaults, indent: true));
        Assert.Equal(
            PdfAnalyzer.ToJson(S(image), dpiThreshold: 200),
            PdfAnalyzer.ToJson(S(image), new AnalyzerOptions { DpiThreshold = 200 }));
        Assert.Equal(PdfAnalyzer.ExtractPlainText(S(pdf)), PdfAnalyzer.ExtractPlainText(S(pdf), defaults));
        Assert.Equal(
            PdfAnalyzer.ExtractText(S(pdf)).Select(r => r.Text),
            PdfAnalyzer.ExtractText(S(pdf), defaults).Select(r => r.Text));
        Assert.Equal(PdfAnalyzer.AnalyzeImageDpi(S(image)).Count, PdfAnalyzer.AnalyzeImageDpi(S(image), defaults).Count);
        Assert.Equal(PdfAnalyzer.ExtractImages(S(image)).Count, PdfAnalyzer.ExtractImages(S(image), defaults).Count);
        Assert.Equal(PdfAnalyzer.ExtractColorImages(S(image)).Count, PdfAnalyzer.ExtractColorImages(S(image), defaults).Count);
        Assert.Equal(PdfAnalyzer.ExtractForm(S(pdf)).HasAcroForm, PdfAnalyzer.ExtractForm(S(pdf), defaults).HasAcroForm);
    }

    // ---- helpers ----

    private static IEnumerable<Action<Stream, AnalyzerOptions>> Extractors()
    {
        yield return (s, o) => PdfAnalyzer.ExtractForm(s, o);
        yield return (s, o) => PdfAnalyzer.AnalyzeImageDpi(s, o);
        yield return (s, o) => PdfAnalyzer.ExtractText(s, o);
        yield return (s, o) => PdfAnalyzer.ExtractPlainText(s, o);
        yield return (s, o) => PdfAnalyzer.ExtractImages(s, o);
        yield return (s, o) => PdfAnalyzer.ExtractColorImages(s, o);
    }

    // 'n' (end path) operators: one work unit each.
    private static string Ops(int count) => string.Concat(Enumerable.Repeat("n ", count));

    // One page per content string, all sharing a Helvetica font /F1.
    internal static byte[] Pages(params string[] contents)
    {
        var objects = new List<object>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", contents.Select((_, i) => $"{4 + (2 * i)} 0 R"))}] /Count {contents.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (int i = 0; i < contents.Length; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {5 + (2 * i)} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            objects.Add(Stream(string.Empty, Encoding.ASCII.GetBytes(contents[i])));
        }

        return Build(objects.ToArray());
    }

    // A page that draws one width x height DeviceGray image (unfiltered) over a 1 x 1 inch area.
    private static byte[] ImagePdf(int width, int height)
        => Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream(string.Empty, Encoding.ASCII.GetBytes("q 72 0 0 72 0 0 cm /Im1 Do Q")),
            Stream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[width * height]));

    private static byte[] Stream(string dict, byte[] data)
    {
        var ms = new MemoryStream();
        byte[] head = Encoding.ASCII.GetBytes($"<< {dict} /Length {data.Length} >>\nstream\n");
        ms.Write(head, 0, head.Length);
        ms.Write(data, 0, data.Length);
        byte[] tail = Encoding.ASCII.GetBytes("\nendstream");
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private static byte[] Zlib(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }

        return ms.ToArray();
    }

    // Objects are numbered from 1 in order; each is a string or the raw bytes of a stream object.
    private static byte[] Build(params object[] objects)
    {
        var ms = new MemoryStream();
        void W(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        W("%PDF-1.7\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = ms.Position;
            W($"{i + 1} 0 obj\n");
            if (objects[i] is byte[] bytes) ms.Write(bytes, 0, bytes.Length);
            else W((string)objects[i]);
            W("\nendobj\n");
        }

        long xref = ms.Position;
        W($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets)
        {
            W($"{o:D10} 00000 n \n");
        }

        W($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;
using ZeroDep.Abstractions;

namespace ZeroDep.Tests;

/// <summary>
/// The public facade's exception contract: <see cref="PdfAnalyzer.Analyze"/> and
/// <see cref="PdfAnalyzer.ToJson"/> report an unreadable file as a rejection, and every extraction
/// method throws only <see cref="PdfSyntaxException"/> for one.
/// </summary>
public sealed class PdfAnalyzerExceptionContractTests
{
    public static IEnumerable<object[]> Extractors()
    {
        yield return new object[] { "ExtractForm", (Action<Stream>)(s => PdfAnalyzer.ExtractForm(s)) };
        yield return new object[] { "AnalyzeImageDpi", (Action<Stream>)(s => PdfAnalyzer.AnalyzeImageDpi(s)) };
        yield return new object[] { "ExtractText", (Action<Stream>)(s => PdfAnalyzer.ExtractText(s)) };
        yield return new object[] { "ExtractPlainText", (Action<Stream>)(s => PdfAnalyzer.ExtractPlainText(s)) };
        yield return new object[] { "ExtractImages", (Action<Stream>)(s => PdfAnalyzer.ExtractImages(s)) };
        yield return new object[] { "ExtractColorImages", (Action<Stream>)(s => PdfAnalyzer.ExtractColorImages(s)) };
    }

    [Theory]
    [MemberData(nameof(Extractors))]
    public void Extractor_UnexpectedEngineFailure_ThrowsOnlySyntaxException(string name, Action<Stream> extract)
    {
        Exception ex = Record.Exception(() => extract(new MemoryStream(NegativeObjectStreamCount())));

        Assert.NotNull(ex);
        Assert.True(ex is PdfSyntaxException, $"{name} threw {ex!.GetType().Name}");
    }

    [Theory]
    [MemberData(nameof(Extractors))]
    public void Extractor_NullStream_ThrowsArgumentNull(string name, Action<Stream> extract)
    {
        Assert.NotNull(name);
        Assert.Throws<ArgumentNullException>(() => extract(null!));
    }

    [Fact]
    public void Analyze_UnexpectedEngineFailure_IsRejected()
    {
        DocumentAnalysis analysis = PdfAnalyzer.Analyze(new MemoryStream(NegativeObjectStreamCount()));

        Assert.Equal(DocumentStatus.Rejected, analysis.Status);
        Assert.NotNull(analysis.Rejection);
    }

    [Fact]
    public void ToJson_UnexpectedEngineFailure_SerializesRejection()
    {
        string json = PdfAnalyzer.ToJson(new MemoryStream(NegativeObjectStreamCount()));

        Assert.Contains("\"status\"", json, StringComparison.Ordinal);
        Assert.Contains("ejected", json, StringComparison.Ordinal);
    }

    // A cross-reference stream whose only object stream declares /N -1: the object layer used to fail
    // with an undocumented exception type.
    private static byte[] NegativeObjectStreamCount()
    {
        var ms = new MemoryStream();
        void W(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        W("%PDF-1.7\n");
        long objStm = ms.Position;
        W("3 0 obj\n<< /Type /ObjStm /N -1 /First 0 /Length 0 >>\nstream\n\nendstream\nendobj\n");
        long xref = ms.Position;
        byte[] rows = { 0, 0, 0, 2, 3, 0, 1, (byte)xref, 0, 1, (byte)objStm, 0 };
        W($"2 0 obj\n<< /Type /XRef /W [1 1 1] /Size 4 /Root 1 0 R /Length {rows.Length} >>\nstream\n");
        ms.Write(rows, 0, rows.Length);
        W($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}

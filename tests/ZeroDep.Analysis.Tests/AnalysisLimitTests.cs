using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using ZeroDep.Abstractions;
using ZeroDep.Analysis;

namespace ZeroDep.Analysis.Tests;

/// <summary>
/// Termination and exception-contract regressions for the analysis layer: crafted field trees must
/// finish quickly in bounded memory, and unexpected engine failures must surface only as a rejection
/// or a <see cref="PdfSyntaxException"/>.
/// </summary>
public sealed class AnalysisLimitTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const long AllocationCeiling = 300L * 1024 * 1024;

    // A field whose /Kids lists itself many times used to fan out exponentially (fan^depth calls).
    [Fact]
    public void AcroForm_KidsSelfReference_Terminates()
    {
        string kids = string.Join(" ", Enumerable.Repeat("4 0 R", 64));
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            $"<< /T (a) /FT /Tx /Kids [{kids}] >>");

        AcroFormReport report = RunBounded(() => AcroFormAnalyzer.Analyze(new MemoryStream(pdf)));

        Assert.True(report.HasAcroForm);
        Assert.Empty(report.Fields);   // the only node is its own (skipped) child: no leaf is reached
    }

    // Two fields that list each other as kids form a cycle; each is visited once.
    [Fact]
    public void AcroForm_TwoNodeCycle_Terminates()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /T (a) /Kids [5 0 R 5 0 R] >>",
            "<< /T (b) /FT /Tx /Kids [4 0 R 4 0 R] >>");

        AcroFormReport report = RunBounded(() => AcroFormAnalyzer.Analyze(new MemoryStream(pdf)));

        Assert.Empty(report.Fields);
    }

    // A legitimate tree is unaffected by the visited set.
    [Fact]
    public void AcroForm_OrdinaryTree_StillReportsEveryLeaf()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /T (p) /Kids [5 0 R 6 0 R] >>",
            "<< /T (x) /FT /Tx /V (1) /Parent 4 0 R >>",
            "<< /T (y) /FT /Tx /V (2) /Parent 4 0 R >>");

        AcroFormReport report = AcroFormAnalyzer.Analyze(new MemoryStream(pdf));

        Assert.Equal(new[] { "p.x", "p.y" }, report.Fields.Select(f => f.FullyQualifiedName).ToArray());
    }

    // More field nodes than the budget: the walk stops with a PdfSyntaxException naming the limit, and the
    // unified analysis reports a rejection instead of throwing.
    [Fact]
    public void AcroForm_OverFieldBudget_IsRejected()
    {
        var kids = new StringBuilder();
        for (int i = 0; i <= Limits.MaxFormFields; i++)
        {
            kids.Append("<</T(f)>>");
        }

        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            $"<< /T (root) /FT /Tx /Kids [{kids}] >>");

        PdfSyntaxException ex = RunBounded(() =>
            Assert.Throws<PdfSyntaxException>(() => AcroFormAnalyzer.Analyze(new MemoryStream(pdf))));
        Assert.Contains("field limit", ex.Message, StringComparison.Ordinal);

        DocumentAnalysis analysis = RunBounded(() => DocumentAnalyzer.Analyze(new MemoryStream(pdf), 150));
        Assert.Equal(DocumentStatus.Rejected, analysis.Status);
        Assert.Equal(RejectionReason.MalformedObject, analysis.Rejection!.Reason);
    }

    // An object stream with /N -1 made the engine throw a non-syntax exception out of Analyze.
    [Fact]
    public void Analyze_UnexpectedEngineFailure_IsRejectedNotThrown()
    {
        DocumentAnalysis analysis = RunBounded(() => DocumentAnalyzer.Analyze(new MemoryStream(NegativeObjectStreamCount()), 150));

        Assert.Equal(DocumentStatus.Rejected, analysis.Status);
        Assert.NotNull(analysis.Rejection);
    }

    [Fact]
    public void Analyze_NullStream_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => DocumentAnalyzer.Analyze(null!, 150));

    [Fact]
    public void Boundary_WrapsUnexpectedExceptionAsSyntaxException()
    {
        var inner = new IndexOutOfRangeException("boom");

        PdfSyntaxException ex = Assert.Throws<PdfSyntaxException>(() => ExceptionBoundary.Run<int>(() => throw inner));

        Assert.Same(inner, ex.InnerException);
        Assert.Contains(nameof(IndexOutOfRangeException), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boundary_PassesSyntaxExceptionThroughUnchanged()
    {
        var original = new PdfSyntaxException("bad xref");

        PdfSyntaxException ex = Assert.Throws<PdfSyntaxException>(() => ExceptionBoundary.Run<int>(() => throw original));

        Assert.Same(original, ex);
    }

    [Fact]
    public void Boundary_NeverTranslatesOutOfMemory()
    {
        Assert.Throws<OutOfMemoryException>(() => ExceptionBoundary.Run<int>(() => throw new OutOfMemoryException()));
        Assert.False(ExceptionBoundary.IsTranslatable(new OutOfMemoryException()));
        Assert.True(ExceptionBoundary.IsTranslatable(new InvalidOperationException()));
    }

    // Runs on a worker thread so a regression shows up as a timeout rather than a stuck test run, and
    // measures that thread's managed allocation.
    private static T RunBounded<T>(Func<T> body)
    {
        long allocated = 0;
        Task<T> task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            T result = body();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return result;
        });

        Assert.True(task.Wait(Deadline), "Analysis did not finish within the deadline.");
        Assert.True(allocated < AllocationCeiling, $"Allocated {allocated:N0} bytes.");
        return task.Result;
    }

    // A cross-reference stream whose only object stream declares /N -1.
    internal static byte[] NegativeObjectStreamCount()
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

    private static byte[] Build(params string[] objects)
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
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
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

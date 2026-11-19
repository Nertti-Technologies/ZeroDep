using System;
using System.IO;
using System.Text;
using Xunit;
using ZeroDep.Abstractions;

namespace ZeroDep.Tests;

/// <summary>
/// <see cref="PdfAnalyzer.WriteJson(Stream, Stream, AnalyzerOptions, bool)"/> streams the same JSON that
/// <see cref="PdfAnalyzer.ToJson(Stream, AnalyzerOptions, bool)"/> builds in memory (ADR-0013).
/// </summary>
public sealed class PdfAnalyzerWriteJsonTests
{
    // Text with every escape class the writer handles: quotes, a backslash, a control character, a JavaScript line
    // separator and a non-ASCII letter.
    private static readonly byte[] Pdf = PdfAnalyzerLimitsTests.Pages(
        "BT /F1 12 Tf 72 700 Td (Quote \\\" back\\\\slash \\001 tab\\t) Tj ET",
        "BT /F1 12 Tf 72 700 Td (Second page) Tj ET");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Streamed_IsByteIdenticalToToJson(bool indent)
    {
        var options = new AnalyzerOptions();
        byte[] expected = Encoding.UTF8.GetBytes(PdfAnalyzer.ToJson(new MemoryStream(Pdf), options, indent));

        var output = new MemoryStream();
        PdfAnalyzer.WriteJson(new MemoryStream(Pdf), output, options, indent);

        Assert.Equal(expected, output.ToArray());
    }

    [Fact]
    public void Streamed_HasNoByteOrderMark()
    {
        var output = new MemoryStream();
        PdfAnalyzer.WriteJson(new MemoryStream(Pdf), output);

        Assert.Equal((byte)'{', output.ToArray()[0]);
    }

    [Fact]
    public void Output_IsFlushedAndLeftOpen()
    {
        var output = new MemoryStream();
        PdfAnalyzer.WriteJson(new MemoryStream(Pdf), output);

        Assert.True(output.CanWrite);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void UnreadableDocument_StreamsOneCompleteRejection()
    {
        var output = new MemoryStream();
        PdfAnalyzer.WriteJson(new MemoryStream(Encoding.ASCII.GetBytes("not a pdf")), output);

        string json = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(PdfAnalyzer.ToJson(new MemoryStream(Encoding.ASCII.GetBytes("not a pdf"))), json);
        Assert.Contains("\"status\":\"Rejected\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputFailure_IsThrownNotHidden()
    {
        Assert.Throws<IOException>(() => PdfAnalyzer.WriteJson(new MemoryStream(Pdf), new FailingStream()));
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => PdfAnalyzer.WriteJson(null!, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => PdfAnalyzer.WriteJson(new MemoryStream(Pdf), null!));
        Assert.Throws<ArgumentNullException>(() => PdfAnalyzer.WriteJson(new MemoryStream(Pdf), new MemoryStream(), null!));
        Assert.Throws<ArgumentException>(() => PdfAnalyzer.WriteJson(new MemoryStream(Pdf), new MemoryStream(new byte[1], writable: false)));
    }

    // A writable stream whose writes fail, as a closed pipe does.
    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("pipe closed");

        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("pipe closed");
    }
}

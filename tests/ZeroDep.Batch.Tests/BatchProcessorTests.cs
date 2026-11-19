using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ZeroDep.Batch.Tests;

public sealed class BatchProcessorTests : IDisposable
{
    // A one-page AES-128 document with the user password "u" (the same fixture the analysis tests use).
    private const string PasswordProtectedBase64 = "JVBERi0xLjcKJb/3ov4KMSAwIG9iago8PCAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0NvdW50IDEgL0tpZHMgWyAzIDAgUiBdIC9UeXBlIC9QYWdlcyA+PgplbmRvYmoKMyAwIG9iago8PCAvQ29udGVudHMgNCAwIFIgL01lZGlhQm94IDUgMCBSIC9QYXJlbnQgMiAwIFIgL1Jlc291cmNlcyA8PCAvRm9udCA8PCAvRjEgNiAwIFIgPj4gPj4gL1R5cGUgL1BhZ2UgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA4MCAvRmlsdGVyIC9GbGF0ZURlY29kZSA+PgpzdHJlYW0KPxjGEzmVCM7fliiIDw6ZF5CliQtNcWaGkMLcSBj6ilXBAe7qQgj24TytgzJEV4kZqtoV98718sCI++q9b8FtiMSeh0gPmxTd77Q3TxsAJCcKZW5kc3RyZWFtCmVuZG9iago1IDAgb2JqClsgMCAwIDIwMCAyMDAgXQplbmRvYmoKNiAwIG9iago8PCAvQmFzZUZvbnQgL0hlbHZldGljYSAvU3VidHlwZSAvVHlwZTEgL1R5cGUgL0ZvbnQgPj4KZW5kb2JqCjcgMCBvYmoKPDwgL0NGIDw8IC9TdGRDRiA8PCAvQXV0aEV2ZW50IC9Eb2NPcGVuIC9DRk0gL0FFU1YyIC9MZW5ndGggMTYgPj4gPj4gL0ZpbHRlciAvU3RhbmRhcmQgL0xlbmd0aCAxMjggL08gPDJhMmYwYTE5OTAxOTJjNjAxMTQ3MzBiZGNkMzlmMzc4MjhhNTNjODlhMzQwZGQ0NzNjODUyOTlkYzUyNThlMWM+IC9PRSA8PiAvUCAtMTAyOCAvUiA0IC9TdG1GIC9TdGRDRiAvU3RyRiAvU3RkQ0YgL1UgPDdmNDBmZjQ3ZDA5ZDQ1NTI4ZjQyOWZkNjRiMTg4OGUzMDAyMTQ0Njk5MGI5ZTQxMTQwNzFhNGQ5MTA0OTg0YzE+IC9VRSA8PiAvViA0ID4+CmVuZG9iagp4cmVmCjAgOAowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMTUgMDAwMDAgbiAKMDAwMDAwMDA2NCAwMDAwMCBuIAowMDAwMDAwMTIzIDAwMDAwIG4gCjAwMDAwMDAyNDEgMDAwMDAgbiAKMDAwMDAwMDM5MiAwMDAwMCBuIAowMDAwMDAwNDIzIDAwMDAwIG4gCjAwMDAwMDA0OTMgMDAwMDAgbiAKdHJhaWxlciA8PCAvUm9vdCAxIDAgUiAvU2l6ZSA4IC9JRCBbPDVmNzcwNDY5NGVmYzgxYzI2ODUzZGQwNzQxYzcyMTUwPjw1Zjc3MDQ2OTRlZmM4MWMyNjg1M2RkMDc0MWM3MjE1MD5dIC9FbmNyeXB0IDcgMCBSID4+CnN0YXJ0eHJlZgo4MDkKJSVFT0YK";

    private readonly string _root;
    private readonly string _input;
    private readonly string _output;

    public BatchProcessorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zerodep-batch-" + Guid.NewGuid().ToString("N"));
        _input = Path.Combine(_root, "in");
        _output = Path.Combine(_root, "out");
        Directory.CreateDirectory(_input);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task RunProducesLedgerAggregateAndPerFileOutputs()
    {
        File.WriteAllBytes(Path.Combine(_input, "doc1.pdf"), TextPdf("BT /F1 12 Tf 100 700 Td (HelloZeroDep) Tj ET"));
        File.WriteAllBytes(Path.Combine(_input, "doc2.pdf"), TextPdf("BT /F1 12 Tf 100 700 Td (SecondDoc) Tj ET"));
        File.WriteAllBytes(Path.Combine(_input, "bad.pdf"), Encoding.ASCII.GetBytes("not a pdf at all, no header here"));

        var options = new BatchOptions { InputDirectory = _input, OutputDirectory = _output, MaxConcurrency = 2 };
        BatchSummary summary = await new BatchProcessor().RunAsync(options);

        Assert.Equal(3, summary.Total);
        Assert.Equal(3, summary.ProcessedThisRun);
        Assert.Equal(0, summary.Skipped);
        Assert.True(summary.Rejected >= 1);

        Assert.True(File.Exists(Path.Combine(_output, "ledger.tsv")));
        Assert.True(File.Exists(summary.AggregatePath));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_output, "perfile"), "*.json").Length);
    }

    [Fact]
    public async Task AggregateOutputContainsNoFileNamesOrContent()
    {
        File.WriteAllBytes(Path.Combine(_input, "secret-invoice.pdf"), TextPdf("BT /F1 12 Tf 100 700 Td (ConfidentialText) Tj ET"));

        var options = new BatchOptions { InputDirectory = _input, OutputDirectory = _output };
        BatchSummary summary = await new BatchProcessor().RunAsync(options);

        string aggregate = File.ReadAllText(summary.AggregatePath);
        Assert.DoesNotContain("secret-invoice", aggregate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConfidentialText", aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf", aggregate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecondRunResumesAndSkipsCompletedFiles()
    {
        File.WriteAllBytes(Path.Combine(_input, "doc1.pdf"), TextPdf("BT /F1 12 Tf 100 700 Td (Doc) Tj ET"));

        var options = new BatchOptions { InputDirectory = _input, OutputDirectory = _output };
        BatchSummary first = await new BatchProcessor().RunAsync(options);
        Assert.Equal(1, first.ProcessedThisRun);

        BatchSummary second = await new BatchProcessor().RunAsync(options);
        Assert.Equal(0, second.ProcessedThisRun);
        Assert.Equal(1, second.Skipped);
        // aggregate still reflects the full corpus after a resumed run
        Assert.Equal(1, second.Statistics.Corpus.Total);
    }

    [Fact]
    public async Task UnreadableFileIsRejectedWithoutAbortingTheRun()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only; Windows has no mode-000 equivalent
        }

        for (int i = 0; i < 3; i++)
        {
            File.WriteAllBytes(Path.Combine(_input, $"doc{i}.pdf"), TextPdf("BT /F1 12 Tf 100 700 Td (Doc) Tj ET"));
        }

        string locked = Path.Combine(_input, "doc1.pdf");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (CanOpen(locked))
            {
                return; // running with privileges that ignore file modes (e.g. root)
            }

            var options = new BatchOptions { InputDirectory = _input, OutputDirectory = _output, MaxConcurrency = 2 };
            BatchSummary summary = await new BatchProcessor().RunAsync(options);

            Assert.Equal(3, summary.Total);
            Assert.Equal(3, summary.ProcessedThisRun);
            Assert.Equal(1, summary.Rejected);
            Assert.Contains(summary.Statistics.Rejections, r => r.Reason == "MalformedObject" && r.Count == 1);
            Assert.True(File.Exists(summary.AggregatePath));
            Assert.Equal(3, File.ReadAllLines(Path.Combine(_output, "ledger.tsv")).Length);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task PerFileJsonWriteFailureIsRejectedWithoutAbortingTheRun()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only
        }

        File.WriteAllBytes(Path.Combine(_input, "doc1.pdf"), TextPdf("BT /F1 12 Tf 100 700 Td (Doc) Tj ET"));
        string perFile = Path.Combine(_output, "perfile");
        Directory.CreateDirectory(perFile);
        File.SetUnixFileMode(perFile, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (CanCreateIn(perFile))
            {
                return; // running with privileges that ignore file modes (e.g. root)
            }

            var options = new BatchOptions { InputDirectory = _input, OutputDirectory = _output };
            BatchSummary summary = await new BatchProcessor().RunAsync(options);

            Assert.Equal(1, summary.Total);
            Assert.Equal(1, summary.Rejected);
            Assert.True(File.Exists(summary.AggregatePath));
        }
        finally
        {
            File.SetUnixFileMode(perFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task PasswordProtectedFileWithoutPasswordIsEncryptedUnreadable()
    {
        File.WriteAllBytes(Path.Combine(_input, "locked.pdf"), Convert.FromBase64String(PasswordProtectedBase64));
        File.WriteAllBytes(Path.Combine(_input, "plain.pdf"), TextPdf("BT /F1 12 Tf 100 700 Td (Doc) Tj ET"));

        var options = new BatchOptions { InputDirectory = _input, OutputDirectory = _output };
        BatchSummary summary = await new BatchProcessor().RunAsync(options);

        Assert.Equal(1, summary.EncryptedUnreadable);
        Assert.Equal(1, summary.Rejected);
        Assert.Equal(1, summary.Statistics.Corpus.EncryptedUnreadable);
        Assert.Contains(summary.Statistics.Categories, c => c.Category == "EncryptedUnreadable" && c.Count == 1);
        Assert.Contains(summary.Statistics.Rejections, r => r.Reason == "EncryptedPasswordRequired" && r.Count == 1);
        Assert.Equal(50.0, summary.Statistics.Encryption.EncryptedPercent);
    }

    [Fact]
    public async Task ResumeReprocessesWhenTheDpiThresholdChanges()
    {
        File.WriteAllBytes(Path.Combine(_input, "doc1.pdf"), TextPdf("BT /F1 12 Tf 100 700 Td (Doc) Tj ET"));

        await new BatchProcessor().RunAsync(new BatchOptions { InputDirectory = _input, OutputDirectory = _output, DpiThreshold = 150 });
        BatchSummary second = await new BatchProcessor().RunAsync(
            new BatchOptions { InputDirectory = _input, OutputDirectory = _output, DpiThreshold = 300 });

        Assert.Equal(1, second.ProcessedThisRun);
        Assert.Equal(0, second.Skipped);
        Assert.Equal(300, second.Statistics.Dpi.Threshold);
    }

    [Fact]
    public async Task ResumeReprocessesWhenAPasswordIsSupplied()
    {
        File.WriteAllBytes(Path.Combine(_input, "locked.pdf"), Convert.FromBase64String(PasswordProtectedBase64));

        BatchSummary first = await new BatchProcessor().RunAsync(new BatchOptions { InputDirectory = _input, OutputDirectory = _output });
        Assert.Equal(1, first.EncryptedUnreadable);

        BatchSummary second = await new BatchProcessor().RunAsync(
            new BatchOptions { InputDirectory = _input, OutputDirectory = _output, Password = "u" });

        Assert.Equal(1, second.ProcessedThisRun);
        Assert.Equal(0, second.EncryptedUnreadable);
        Assert.Equal(0, second.Rejected);
        Assert.DoesNotContain("\tu\t", File.ReadAllText(Path.Combine(_output, "ledger.tsv")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeTreatsALedgerLineWithoutRunDetailsAsAMiss()
    {
        string pdf = Path.Combine(_input, "doc1.pdf");
        File.WriteAllBytes(pdf, TextPdf("BT /F1 12 Tf 100 700 Td (Doc) Tj ET"));

        // A line in the earlier 14-field format: right id and content hash, but no password flag or engine version.
        string oldLine = string.Join("\t", new[]
        {
            BatchId.ForPath(Path.GetFullPath(pdf)), BatchId.ForContent(pdf), "Processed", "DigitalText",
            "1", "0", "0", "None", "0", "0", "None", "150", "0", string.Empty,
        });
        Directory.CreateDirectory(_output);
        File.WriteAllText(Path.Combine(_output, "ledger.tsv"), oldLine + "\n");

        BatchSummary summary = await new BatchProcessor().RunAsync(new BatchOptions { InputDirectory = _input, OutputDirectory = _output });

        Assert.Equal(1, summary.ProcessedThisRun);
        Assert.Equal(0, summary.Skipped);
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanCreateIn(string directory)
    {
        string probe = Path.Combine(directory, "probe");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] TextPdf(string content)
    {
        var ms = new MemoryStream();
        void W(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        W("%PDF-1.4\n");
        long o1 = ms.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = ms.Position; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");
        long o3 = ms.Position; W("3 0 obj\n<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        long o4 = ms.Position; W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        long o5 = ms.Position; W("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        long xref = ms.Position;
        W("xref\n0 6\n0000000000 65535 f \n");
        foreach (long o in new[] { o1, o2, o3, o4, o5 })
        {
            W($"{o:D10} 00000 n \n");
        }

        W("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        W($"startxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}

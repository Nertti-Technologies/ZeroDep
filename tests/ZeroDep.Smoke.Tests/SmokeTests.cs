using System.Text.Json;
using Xunit;

namespace ZeroDep.Smoke.Tests;

public sealed class SmokeOptionsTests
{
    [Fact]
    public void Defaults_AreTheAdrValues()
    {
        SmokeOptions o = SmokeOptions.Parse(["corpus"], 7, out string? error)!;

        Assert.Null(error);
        Assert.Equal("corpus", o.Corpus);
        Assert.Equal(5, o.Sample);
        Assert.Equal(7, o.Seed);
        Assert.Equal(TimeSpan.FromSeconds(120), o.Timeout);
        Assert.Equal(1024L * 1024 * 1024, o.MaxMemoryBytes);
        Assert.True(o.Build);
        Assert.False(o.All);
        Assert.False(o.Attest);
    }

    [Fact]
    public void Flags_AreRead()
    {
        SmokeOptions o = SmokeOptions.Parse(["c", "--sample=2", "--seed=42", "--timeout=30", "--max-memory-mb=512", "--no-build", "--attest", "--accept-rejections"], 0, out _)!;

        Assert.Equal(2, o.Sample);
        Assert.Equal(42, o.Seed);
        Assert.Equal(TimeSpan.FromSeconds(30), o.Timeout);
        Assert.Equal(512L * 1024 * 1024, o.MaxMemoryBytes);
        Assert.False(o.Build);
        Assert.True(o.Attest);
        Assert.True(o.AcceptRejections);
    }

    public static TheoryData<string[]> InvalidArgs => new()
    {
        Array.Empty<string>(),
        new[] { "c", "--sample=0" },
        new[] { "c", "--sample=-1" },
        new[] { "c", "--timeout=x" },
        new[] { "c", "--bogus" },
        new[] { "c", "d" },
        new[] { "c", "--accept-rejections" },
    };

    [Theory]
    [MemberData(nameof(InvalidArgs))]
    public void InvalidArguments_AreRefused(string[] args)
    {
        Assert.Null(SmokeOptions.Parse(args, 0, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}

public sealed class CorpusScannerTests
{
    private const string Home = "/Users/someone";

    [Theory]
    [InlineData("/Volumes/NAS/Books")]
    [InlineData("/Volumes")]
    [InlineData("/Network/Servers/x")]
    [InlineData("/net/host/share")]
    [InlineData("/Users/someone/SynologyDrive/Books")]
    [InlineData("/Users/someone/Library/CloudStorage/OneDrive/a.pdf")]
    [InlineData("/Users/someone/Library/Mobile Documents/com~apple~CloudDocs/a.pdf")]
    [InlineData("/volumes/nas")] // case-insensitive, as on the default macOS file system
    public void ExternalAndSyncedLocations_AreForbidden(string path)
    {
        Assert.True(CorpusScanner.IsForbidden(path, Home));
    }

    [Theory]
    [InlineData("/Users/someone/Workspace/Books")]
    [InlineData("/Users/someone/Workspace/Volumes")]
    [InlineData("/VolumesX")]
    [InlineData("/Users/someone/Library/CloudStorageX")]
    public void LocalLocations_AreAllowed(string path)
    {
        Assert.False(CorpusScanner.IsForbidden(path, Home));
    }

    [Fact]
    public void AForbiddenCorpus_IsRefusedWithoutTouchingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CorpusScanner.Scan("/Volumes/never-touched", Home));
        Assert.Contains("refusing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_FollowsLocalLinksOnceAndFindsOnlyPdfs()
    {
        string root = Path.Combine(Path.GetTempPath(), "zerodep-scan-" + Guid.NewGuid().ToString("N"));
        string corpus = Path.Combine(root, "corpus");
        string elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(corpus, "sub"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            File.WriteAllText(Path.Combine(corpus, "a.pdf"), "");
            File.WriteAllText(Path.Combine(corpus, "sub", "B.PDF"), "");
            File.WriteAllText(Path.Combine(corpus, "notes.txt"), "");
            File.WriteAllText(Path.Combine(elsewhere, "c.pdf"), "");
            Directory.CreateSymbolicLink(Path.Combine(corpus, "linked"), elsewhere);
            Directory.CreateSymbolicLink(Path.Combine(corpus, "sub", "loop"), corpus); // a cycle back to the root

            IReadOnlyList<string> files = CorpusScanner.Scan(corpus, Home);

            Assert.Equal(3, files.Count);
            Assert.Contains(files, f => f.EndsWith("a.pdf", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("B.PDF", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith(Path.Combine("elsewhere", "c.pdf"), StringComparison.Ordinal));
            Assert.Equal(files.OrderBy(f => f, StringComparer.Ordinal), files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_RefusesALinkIntoForbiddenStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "zerodep-scan-" + Guid.NewGuid().ToString("N"));
        string corpus = Path.Combine(root, "corpus");
        string forbidden = Path.Combine(root, "pretend-nas");
        Directory.CreateDirectory(corpus);
        Directory.CreateDirectory(forbidden);
        try
        {
            File.WriteAllText(Path.Combine(forbidden, "a.pdf"), "");
            Directory.CreateSymbolicLink(Path.Combine(corpus, "nas"), forbidden);
            string[] roots = [CorpusScanner.RealPath(forbidden, Home)];

            var ex = Assert.Throws<InvalidOperationException>(() => CorpusScanner.Scan(corpus, Home, roots));
            Assert.Contains("refusing", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_RefusesAFileLinkIntoForbiddenStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "zerodep-scan-" + Guid.NewGuid().ToString("N"));
        string corpus = Path.Combine(root, "corpus");
        string forbidden = Path.Combine(root, "pretend-cloud");
        Directory.CreateDirectory(corpus);
        Directory.CreateDirectory(forbidden);
        try
        {
            File.WriteAllText(Path.Combine(forbidden, "b.pdf"), "");
            File.CreateSymbolicLink(Path.Combine(corpus, "b.pdf"), Path.Combine(forbidden, "b.pdf"));
            string[] roots = [CorpusScanner.RealPath(forbidden, Home)];

            Assert.Throws<InvalidOperationException>(() => CorpusScanner.Scan(corpus, Home, roots));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class SamplerTests
{
    private static readonly string[] Files = Enumerable.Range(0, 50).Select(i => $"/c/{i:D2}.pdf").ToArray();

    [Fact]
    public void SameSeed_GivesTheSameSample()
    {
        Assert.Equal(Sampler.Pick(Files, 5, 123), Sampler.Pick(Files, 5, 123));
    }

    [Fact]
    public void Sample_HasDistinctFilesFromTheCorpus()
    {
        IReadOnlyList<string> pick = Sampler.Pick(Files, 5, 9);

        Assert.Equal(5, pick.Distinct().Count());
        Assert.All(pick, f => Assert.Contains(f, Files));
    }

    [Fact]
    public void ASampleLargerThanTheCorpus_IsTheWholeCorpus()
    {
        Assert.Equal(Files, Sampler.Pick(Files, 500, 1));
    }
}

public sealed class DocumentChecksTests
{
    private const string Processed = """{"status":"Processed","pageCount":3}""";
    private const string Rejected = """{"status":"Rejected","pageCount":0,"rejection":{"reason":"ResourceLimitExceeded","detail":"too much"}}""";

    private static CliRun Run(int exit, string stdout = "", string stderr = "", string? killed = null)
        => new(exit, stdout, stderr, TimeSpan.FromSeconds(1), 100, killed);

    [Fact]
    public void ProcessedDocument_Passes()
    {
        DocumentVerdict v = DocumentChecks.Evaluate("/c/a.pdf", "ab", Run(0), Run(0, Processed));

        Assert.True(v.Passed);
        Assert.False(v.Rejected);
        Assert.Equal(3, v.PageCount);
    }

    [Fact]
    public void RejectedDocument_PassesAndCarriesTheReason()
    {
        DocumentVerdict v = DocumentChecks.Evaluate("/c/a.pdf", "ab", Run(3), Run(0, Rejected));

        Assert.True(v.Passed);
        Assert.True(v.Rejected);
        Assert.Equal("ResourceLimitExceeded: too much", v.Rejection);
    }

    [Fact]
    public void TheCrashThisGateExistsFor_Fails()
    {
        // The 2026-11-20 defect: text mode aborted with an unhandled exception (exit 134).
        DocumentVerdict v = DocumentChecks.Evaluate("/c/a.pdf", "ab", Run(134, stderr: "Unhandled exception. ZeroDep.Abstractions.PdfLimitExceededException: x\n   at ..."), Run(0, Rejected));

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("unhandled exception", StringComparison.Ordinal));
        Assert.Contains(v.Failures, f => f.Contains("exit 134", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, Rejected)]
    [InlineData(3, Processed)]
    public void TextAndJsonDisagreeing_Fails(int textExit, string json)
    {
        DocumentVerdict v = DocumentChecks.Evaluate("/c/a.pdf", "ab", Run(textExit), Run(0, json));

        Assert.Contains(v.Failures, f => f.Contains("disagrees", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidJson_Fails()
    {
        Assert.Contains(DocumentChecks.Evaluate("/c/a.pdf", "ab", Run(0), Run(0, "{\"status\":")).Failures, f => f.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownStatus_Fails()
    {
        Assert.Contains(DocumentChecks.Evaluate("/c/a.pdf", "ab", Run(0), Run(0, """{"status":"Maybe"}""")).Failures, f => f.Contains("unknown status", StringComparison.Ordinal));
    }

    [Fact]
    public void AKilledRun_Fails()
    {
        DocumentVerdict v = DocumentChecks.Evaluate("/c/a.pdf", "ab", Run(137, killed: "timed out after 120 s"), Run(0, Processed));

        Assert.Equal(["text: timed out after 120 s"], v.Failures);
    }

    [Theory]
    [InlineData(0, 2, 2, true)]
    [InlineData(0, 2, 1, false)]
    [InlineData(1, 2, 2, false)]
    public void Batch_NeedsExitZeroAndOneResultPerDocument(int exit, int expected, int results, bool passes)
    {
        Assert.Equal(passes, DocumentChecks.EvaluateBatch(Run(exit), expected, results).Count == 0);
    }
}

public sealed class AttestationTests
{
    [Fact]
    public void Attestation_HoldsHashesAndCountsButNoNames()
    {
        var verdicts = new[]
        {
            new DocumentVerdict("/Users/someone/Books/Secret Title.pdf", "bb", "Processed", 3, null, TimeSpan.Zero, 0, []),
            new DocumentVerdict("/Users/someone/Books/Other.pdf", "aa", "Rejected", 0, "R: d", TimeSpan.Zero, 0, []),
        };

        string json = Attestation.Write("tree123", 42, verdicts, rejectionsAccepted: true, new DateTimeOffset(2026, 11, 20, 1, 2, 3, TimeSpan.Zero));

        Assert.DoesNotContain("Secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Books", json, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("tree123", root.GetProperty("srcTree").GetString());
        Assert.Equal("2026-11-20T01:02:03Z", root.GetProperty("createdUtc").GetString());
        Assert.Equal(2, root.GetProperty("sample").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        Assert.Equal(1, root.GetProperty("rejected").GetInt32());
        Assert.True(root.GetProperty("rejectionsAccepted").GetBoolean());
        Assert.Equal(["aa", "bb"], root.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("sha256").GetString()));
    }
}

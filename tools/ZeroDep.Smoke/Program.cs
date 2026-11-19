using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ZeroDep.Smoke;

// Release smoke gate (ADR-0012). Exit 0: pass. Exit 1: a document or the batch run failed. Exit 2: bad usage or setup.

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

SmokeOptions? options = SmokeOptions.Parse(args, Random.Shared.Next(), out string? usageError);
if (options is null)
{
    Console.Error.WriteLine(usageError);
    Console.Error.WriteLine(SmokeOptions.Usage);
    return 2;
}

string? repo = FindRepoRoot(Directory.GetCurrentDirectory());
if (repo is null)
{
    Console.Error.WriteLine("run this from inside the ZeroDep repository (no ZeroDep.slnx found above the current folder)");
    return 2;
}

string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
IReadOnlyList<string> corpus;
try
{
    corpus = CorpusScanner.Scan(options.Corpus, home);
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (corpus.Count == 0)
{
    Console.Error.WriteLine($"no PDFs found under {options.Corpus}");
    return 2;
}

if (options.Build && !await BuildCliAsync(repo, cts.Token).ConfigureAwait(false)) return 2;

string cliDll = Path.Combine(repo, "src", "ZeroDep.Cli", "bin", "Release", "net10.0", "ZeroDep.Cli.dll");
if (!File.Exists(cliDll))
{
    Console.Error.WriteLine($"CLI not built: {cliDll}. Run without --no-build, or build it in Release first.");
    return 2;
}

IReadOnlyList<string> sample = options.All ? corpus : Sampler.Pick(corpus, options.Sample, options.Seed);
Console.WriteLine($"Smoke: seed {options.Seed}, {sample.Count} of {corpus.Count} documents, one at a time " +
    $"(timeout {options.Timeout.TotalSeconds:F0} s, memory ceiling {options.MaxMemoryBytes / (1024 * 1024)} MB).");

var cli = new CliProcess(cliDll, options.Timeout, options.MaxMemoryBytes);
var verdicts = new List<DocumentVerdict>();
var clock = Stopwatch.StartNew();
foreach (string file in sample)
{
    string sha = await Sha256Async(file, cts.Token).ConfigureAwait(false);
    CliRun text = await cli.RunAsync([file], cts.Token).ConfigureAwait(false);
    CliRun json = await cli.RunAsync([file, "--json"], cts.Token).ConfigureAwait(false);
    DocumentVerdict verdict = DocumentChecks.Evaluate(file, sha, text, json);
    verdicts.Add(verdict);

    string result = verdict.Passed ? (verdict.Rejected ? "REJECTED" : "pass") : "FAIL";
    Console.WriteLine($"[{verdicts.Count}/{sample.Count}] {result,-8} {verdict.Elapsed.TotalSeconds,6:F1} s  {verdict.PeakBytes / (1024 * 1024),5} MB  {Path.GetFileName(file)}");
    foreach (string failure in verdict.Failures) Console.WriteLine($"         {failure}");
    if (verdict.Passed && verdict.Rejection is not null) Console.WriteLine($"         {verdict.Rejection}");

    if (verdicts.Count == 1 && sample.Count > 1)
    {
        TimeSpan estimate = clock.Elapsed * sample.Count;
        string size = estimate.TotalMinutes >= 1 ? $"{estimate.TotalMinutes:F0} min" : $"{estimate.TotalSeconds:F0} s";
        Console.WriteLine($"         estimate for the whole sample: about {size} (first document × {sample.Count})");
    }
}

IReadOnlyList<string> batchFailures = await RunBatchAsync(cli, sample, verdicts, cts.Token).ConfigureAwait(false);
Console.WriteLine($"Batch: {(batchFailures.Count == 0 ? "pass" : string.Join("; ", batchFailures))}");

string reportDir = Path.Combine(repo, "artifacts", "smoke");
Directory.CreateDirectory(reportDir);
string reportPath = Path.Combine(reportDir, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md");
await File.WriteAllTextAsync(reportPath, SmokeReport.Render(options.Seed, corpus.Count, verdicts, batchFailures), cts.Token).ConfigureAwait(false);

int failed = verdicts.Count(v => !v.Passed);
int rejected = verdicts.Count(v => v.Rejected);
bool pass = failed == 0 && batchFailures.Count == 0;
Console.WriteLine($"\n{(pass ? "PASS" : "FAIL")}: {verdicts.Count} documents, {failed} failed, {rejected} rejected, {clock.Elapsed.TotalMinutes:F1} min. Report: {reportPath}");

if (options.Attest)
{
    string? refusal = !pass ? "the run failed"
        : rejected > 0 && !options.AcceptRejections ? $"{rejected} document(s) were rejected; pass --accept-rejections only with the owner's OK"
        : await Git.SrcIsDirtyAsync(repo, cts.Token).ConfigureAwait(false) ? "src/ has uncommitted changes, so the tested code is not the committed code"
        : null;
    if (refusal is not null)
    {
        Console.Error.WriteLine($"No attestation written: {refusal}.");
        return 1;
    }

    string tree = await Git.SrcTreeAsync(repo, cts.Token).ConfigureAwait(false);
    string attestation = Attestation.Write(tree, options.Seed, verdicts, options.AcceptRejections, DateTimeOffset.UtcNow);
    await File.WriteAllTextAsync(Path.Combine(repo, Attestation.RelativePath), attestation, cts.Token).ConfigureAwait(false);
    Console.WriteLine($"Attestation written: {Attestation.RelativePath} (src tree {tree[..12]}). Commit it with the release.");
}

return pass ? 0 : 1;

static string? FindRepoRoot(string start)
{
    for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ZeroDep.slnx"))) return dir.FullName;
    }

    return null;
}

static async Task<bool> BuildCliAsync(string repo, CancellationToken cancellationToken)
{
    Console.WriteLine("Building the CLI (Release)...");
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardOutput = true };
    foreach (string a in new[] { "build", "src/ZeroDep.Cli", "-c", "Release", "-v", "q", "-nologo" }) start.ArgumentList.Add(a);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start dotnet");
    string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    if (process.ExitCode == 0) return true;

    Console.Error.WriteLine(output);
    Console.Error.WriteLine("CLI build failed.");
    return false;
}

static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
{
    await using FileStream stream = File.OpenRead(path);
    byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    return Convert.ToHexStringLower(hash);
}

// The batch command takes a folder, so the sample is copied into a temporary one under hash names (identical
// files collapse to one), run with one worker, and the per-file results counted.
static async Task<IReadOnlyList<string>> RunBatchAsync(CliProcess cli, IReadOnlyList<string> sample, IReadOnlyList<DocumentVerdict> verdicts, CancellationToken cancellationToken)
{
    string work = Path.Combine(Path.GetTempPath(), "zerodep-smoke-" + Guid.NewGuid().ToString("N"));
    string input = Path.Combine(work, "in");
    string output = Path.Combine(work, "out");
    Directory.CreateDirectory(input);
    try
    {
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sample.Count; i++)
        {
            if (distinct.Add(verdicts[i].Sha256)) File.Copy(sample[i], Path.Combine(input, verdicts[i].Sha256 + ".pdf"));
        }

        CliRun batch = await cli.RunAsync(["batch", input, "--output=" + output, "--concurrency=1", "--no-resume"], cancellationToken).ConfigureAwait(false);
        // Hidden coupling: "perfile" is BatchProcessor.PerFileDirectoryName (src/ZeroDep.Batch). The tool runs
        // the CLI as a process and cannot reference the constant; if the folder is renamed there, this count
        // reads 0 and the gate fails loudly rather than passing.
        string perFile = Path.Combine(output, "perfile");
        int results = Directory.Exists(perFile) ? Directory.GetFiles(perFile, "*.json").Length : 0;
        return DocumentChecks.EvaluateBatch(batch, distinct.Count, results);
    }
    finally
    {
        Directory.Delete(work, recursive: true);
    }
}

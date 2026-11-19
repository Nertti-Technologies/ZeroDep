using System.Text.Json;

namespace ZeroDep.Smoke;

/// <summary>The gate's verdict on one document.</summary>
internal sealed record DocumentVerdict(
    string Path,
    string Sha256,
    string? Status,
    int? PageCount,
    string? Rejection,
    TimeSpan Elapsed,
    long PeakBytes,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;

    public bool Rejected => Status == "Rejected";
}

/// <summary>
/// The pass rules of ADR-0012 §4 for one document, given its text-mode and JSON-mode runs. Pure: no I/O.
/// </summary>
internal static class DocumentChecks
{
    /// <summary>The CLI's exit code for a document it rejected (see the CLI's usage text).</summary>
    public const int RejectedExitCode = 3;

    public static DocumentVerdict Evaluate(string path, string sha256, CliRun text, CliRun json)
    {
        var failures = new List<string>();
        CheckProcess("text", text, failures);
        CheckProcess("json", json, failures);

        if (text.Killed is null && text.ExitCode != 0 && text.ExitCode != RejectedExitCode)
        {
            failures.Add($"text: exit {text.ExitCode} (expected 0 or {RejectedExitCode})");
        }

        if (json.Killed is null && json.ExitCode != 0) failures.Add($"json: exit {json.ExitCode} (expected 0)");

        string? status = null;
        int? pages = null;
        string? rejection = null;
        if (json.Killed is null && json.ExitCode == 0)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json.StdOut);
                JsonElement root = doc.RootElement;
                status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
                if (root.TryGetProperty("pageCount", out JsonElement p) && p.TryGetInt32(out int n)) pages = n;
                if (root.TryGetProperty("rejection", out JsonElement r) && r.ValueKind == JsonValueKind.Object)
                {
                    rejection = $"{Text(r, "reason")}: {Text(r, "detail")}";
                }

                if (status is not ("Processed" or "Rejected")) failures.Add($"json: unknown status '{status}'");
            }
            catch (JsonException ex)
            {
                failures.Add($"json: output is not valid JSON ({ex.Message})");
            }
        }

        // One direction only. JSON mode runs a preflight (for example a missing %%EOF) and one work budget across
        // all passes, so it may reject a document that text mode, which recovers and budgets each extractor
        // separately, still reads. The reverse must hold: a document text mode cannot read, JSON must reject.
        if (status == "Processed" && text.Killed is null && text.ExitCode == RejectedExitCode)
        {
            failures.Add($"text mode rejected the document (exit {RejectedExitCode}) but JSON processed it");
        }

        return new DocumentVerdict(
            path,
            sha256,
            status,
            pages,
            rejection,
            text.Elapsed + json.Elapsed,
            Math.Max(text.PeakBytes, json.PeakBytes),
            failures);
    }

    /// <summary>The batch rules: exit 0, and one per-file result for each document in the sample.</summary>
    public static IReadOnlyList<string> EvaluateBatch(CliRun batch, int expected, int perFileResults)
    {
        var failures = new List<string>();
        CheckProcess("batch", batch, failures);
        if (batch.Killed is null && batch.ExitCode != 0) failures.Add($"batch: exit {batch.ExitCode} (expected 0)");
        if (batch.Killed is null && perFileResults != expected)
        {
            failures.Add($"batch: {perFileResults} per-file results for {expected} documents");
        }

        return failures;
    }

    private static void CheckProcess(string mode, CliRun run, List<string> failures)
    {
        if (run.Killed is not null) failures.Add($"{mode}: {run.Killed}");
        if (run.StdErr.Contains("Unhandled exception", StringComparison.Ordinal))
        {
            failures.Add($"{mode}: unhandled exception: {FirstLine(run.StdErr)}");
        }
    }

    private static string Text(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";

    private static string FirstLine(string s)
    {
        int at = s.IndexOf("Unhandled exception", StringComparison.Ordinal);
        string tail = s.Substring(at);
        int end = tail.IndexOf('\n', StringComparison.Ordinal);
        return end < 0 ? tail : tail.Substring(0, end).TrimEnd();
    }
}

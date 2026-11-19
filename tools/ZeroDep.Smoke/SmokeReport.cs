using System.Globalization;
using System.Text;

namespace ZeroDep.Smoke;

/// <summary>
/// The local Markdown report. It is written under the gitignored <c>artifacts/smoke/</c> and never leaves the
/// machine, so unlike the attestation it names the files, which is what makes a failure findable.
/// </summary>
internal static class SmokeReport
{
    public static string Render(int seed, int corpusSize, IReadOnlyList<DocumentVerdict> verdicts, IReadOnlyList<string> batchFailures)
    {
        var sb = new StringBuilder();
        CultureInfo c = CultureInfo.InvariantCulture;
        int failed = verdicts.Count(v => !v.Passed);
        int rejected = verdicts.Count(v => v.Rejected);
        bool pass = failed == 0 && batchFailures.Count == 0;

        sb.AppendLine(c, $"# ZeroDep smoke run: {(pass ? "PASS" : "FAIL")}");
        sb.AppendLine();
        sb.AppendLine(c, $"- Seed {seed}; {verdicts.Count} of {corpusSize} documents.");
        sb.AppendLine(c, $"- Failed: {failed}. Rejected (calibration input, ADR-0011): {rejected}.");
        sb.AppendLine(c, $"- Batch: {(batchFailures.Count == 0 ? "pass" : string.Join("; ", batchFailures))}.");
        sb.AppendLine();
        sb.AppendLine("| Result | Status | Pages | Time (s) | Peak MB | SHA-256 (first 12) | File | Detail |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (DocumentVerdict v in verdicts)
        {
            string detail = v.Passed ? v.Rejection ?? "" : string.Join("; ", v.Failures);
            sb.AppendLine(c, $"| {(v.Passed ? "pass" : "FAIL")} | {v.Status ?? "-"} | {v.PageCount?.ToString(c) ?? "-"} | {v.Elapsed.TotalSeconds:F1} | {v.PeakBytes / (1024 * 1024)} | {v.Sha256[..12]} | {Cell(Path.GetFileName(v.Path))} | {Cell(detail)} |");
        }

        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

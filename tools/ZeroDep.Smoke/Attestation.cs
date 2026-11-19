using System.Text.Json;

namespace ZeroDep.Smoke;

/// <summary>
/// The committed proof that the gate passed for a given source tree (ADR-0012 §5, E2). It is public in the
/// repository, so it holds hashes and counts only: no file names and no document content.
/// </summary>
internal static class Attestation
{
    /// <summary>2: <c>buildInputs</c> (a hash of every committed build input) replaced <c>srcTree</c>.</summary>
    public const int SchemaVersion = 2;

    public const string RelativePath = "build/smoke-attestation.json";

    /// <summary>
    /// The fewest documents an attestation may cover: 20, the sample the owner chose for releases (2026-11-20).
    /// Hidden coupling: release.yml checks <c>.sample &gt;= 20</c>; change both together.
    /// </summary>
    public const int MinimumSample = 20;

    public static string Write(string buildInputs, int seed, IReadOnlyList<DocumentVerdict> verdicts, bool rejectionsAccepted, DateTimeOffset now)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SchemaVersion);
            w.WriteString("buildInputs", buildInputs);
            w.WriteString("createdUtc", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteNumber("seed", seed);
            w.WriteNumber("sample", verdicts.Count);
            w.WriteNumber("failed", verdicts.Count(v => !v.Passed));
            w.WriteNumber("rejected", verdicts.Count(v => v.Rejected));
            w.WriteBoolean("rejectionsAccepted", rejectionsAccepted);
            w.WriteStartArray("documents");
            foreach (DocumentVerdict v in verdicts.OrderBy(v => v.Sha256, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("sha256", v.Sha256);
                w.WriteString("status", v.Status);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }
}

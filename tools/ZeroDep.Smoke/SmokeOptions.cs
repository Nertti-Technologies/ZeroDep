using System.Globalization;

namespace ZeroDep.Smoke;

/// <summary>The command line of the smoke tool.</summary>
internal sealed class SmokeOptions
{
    public const int DefaultSample = 5;

    /// <summary><b>Estimate</b>: about 6× the slowest measured book (19.4 s, ADR-0011 §1.1).</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary><b>Estimate</b>: about 5× the measured peak of 204 MB (ADR-0012 §4).</summary>
    public const long DefaultMaxMemoryMb = 1024;

    public string Corpus { get; private init; } = string.Empty;

    public int Sample { get; private init; } = DefaultSample;

    public bool All { get; private init; }

    public int Seed { get; private init; }

    public TimeSpan Timeout { get; private init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    public long MaxMemoryBytes { get; private init; } = DefaultMaxMemoryMb * 1024 * 1024;

    public bool Build { get; private init; } = true;

    public bool Attest { get; private init; }

    public bool AcceptRejections { get; private init; }

    public const string Usage =
        "usage: dotnet run --project tools/ZeroDep.Smoke -- <corpusDir> [--sample=N | --all] [--seed=S]\n" +
        "       [--timeout=SECONDS] [--max-memory-mb=MB] [--no-build] [--attest [--accept-rejections]]";

    /// <summary>Parses <paramref name="args"/>; returns null and an error message when they are invalid.</summary>
    public static SmokeOptions? Parse(IReadOnlyList<string> args, int defaultSeed, out string? error)
    {
        error = null;
        string? corpus = null;
        int sample = DefaultSample;
        bool all = false;
        int seed = defaultSeed;
        int timeout = DefaultTimeoutSeconds;
        long memory = DefaultMaxMemoryMb;
        bool build = true;
        bool attest = false;
        bool accept = false;

        foreach (string a in args)
        {
            if (a == "--all") all = true;
            else if (a == "--no-build") build = false;
            else if (a == "--attest") attest = true;
            else if (a == "--accept-rejections") accept = true;
            else if (TryValue(a, "--sample=", out int n) && n > 0) sample = n;
            else if (TryValue(a, "--seed=", out int s)) seed = s;
            else if (TryValue(a, "--timeout=", out int t) && t > 0) timeout = t;
            else if (TryValue(a, "--max-memory-mb=", out int m) && m > 0) memory = m;
            else if (!a.StartsWith("--", StringComparison.Ordinal) && corpus is null) corpus = a;
            else
            {
                error = $"unrecognised or invalid argument: {a}";
                return null;
            }
        }

        if (corpus is null)
        {
            error = "a corpus directory is required";
            return null;
        }

        if (accept && !attest)
        {
            error = "--accept-rejections only applies with --attest";
            return null;
        }

        return new SmokeOptions
        {
            Corpus = corpus,
            Sample = sample,
            All = all,
            Seed = seed,
            Timeout = TimeSpan.FromSeconds(timeout),
            MaxMemoryBytes = memory * 1024 * 1024,
            Build = build,
            Attest = attest,
            AcceptRejections = accept,
        };
    }

    private static bool TryValue(string arg, string prefix, out int value)
    {
        value = 0;
        return arg.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(arg.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

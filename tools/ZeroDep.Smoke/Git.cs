using System.Security.Cryptography;
using System.Text;

namespace ZeroDep.Smoke;

/// <summary>The git facts the attestation needs: a hash of the committed build inputs, and whether they are clean.</summary>
internal static class Git
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Everything committed that decides what the packages contain: the source, plus the shared build settings,
    /// the SDK pin and the polyfills compiled into every project.
    /// </summary>
    /// <remarks>
    /// Hidden coupling: <c>.github/workflows/release.yml</c> ("Verify the smoke attestation matches this release")
    /// hashes the same list in the same order. Change both together, or every release fails the check.
    /// </remarks>
    public static readonly IReadOnlyList<string> BuildInputs =
    [
        "src",
        "Directory.Build.props",
        "global.json",
        "build/IsExternalInit.cs",
        "build/OverloadResolutionPriorityAttribute.cs",
    ];

    /// <summary>
    /// SHA-256 (lower-case hex) of <c>git ls-tree -r HEAD -- &lt;BuildInputs&gt;</c>, byte for byte the value that
    /// <c>release.yml</c> computes with <c>sha256sum</c>.
    /// </summary>
    public static async Task<string> BuildInputsHashAsync(string repo, CancellationToken cancellationToken)
    {
        string listing = await RunAsync(repo, cancellationToken, ["ls-tree", "-r", "HEAD", "--", .. BuildInputs]).ConfigureAwait(false);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(listing)));
    }

    /// <summary>
    /// Whether any build input has uncommitted changes. An attestation for a dirty tree would vouch for code that
    /// was not the code tested.
    /// </summary>
    public static async Task<bool> BuildInputsDirtyAsync(string repo, CancellationToken cancellationToken)
        => (await RunAsync(repo, cancellationToken, ["status", "--porcelain", "--", .. BuildInputs]).ConfigureAwait(false)).Length > 0;

    /// <summary>Runs git and returns its raw standard output (not trimmed: the hash depends on every byte).</summary>
    private static async Task<string> RunAsync(string repo, CancellationToken cancellationToken, IReadOnlyList<string> args)
    {
        CliRun run = await ChildProcess.RunAsync("git", args, repo, Timeout, long.MaxValue, cancellationToken).ConfigureAwait(false);
        if (run.Killed is not null || run.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {run.Killed ?? run.StdErr.Trim()}");
        }

        return run.StdOut;
    }
}

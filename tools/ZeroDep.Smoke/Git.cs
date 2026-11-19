using System.Diagnostics;

namespace ZeroDep.Smoke;

/// <summary>The two git facts the attestation needs: the committed tree hash of <c>src/</c>, and whether it is clean.</summary>
internal static class Git
{
    /// <summary>The tree hash of <c>src/</c> at HEAD, the value <c>release.yml</c> compares with the tag.</summary>
    public static Task<string> SrcTreeAsync(string repo, CancellationToken cancellationToken)
        => RunAsync(repo, cancellationToken, "rev-parse", "HEAD:src");

    /// <summary>
    /// Whether <c>src/</c> has uncommitted changes. An attestation for a dirty tree would vouch for code that
    /// was not the code tested.
    /// </summary>
    public static async Task<bool> SrcIsDirtyAsync(string repo, CancellationToken cancellationToken)
        => (await RunAsync(repo, cancellationToken, "status", "--porcelain", "--", "src").ConfigureAwait(false)).Length > 0;

    private static async Task<string> RunAsync(string repo, CancellationToken cancellationToken, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Trim()}");
        return output.Trim();
    }
}

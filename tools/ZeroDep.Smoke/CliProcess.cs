using System.Diagnostics;

namespace ZeroDep.Smoke;

/// <summary>What one child process did.</summary>
internal sealed record CliRun(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed, long PeakBytes, string? Killed);

/// <summary>
/// The one place the tool starts a child process (the CLI under test, <c>dotnet build</c>, <c>git</c>). No shell is
/// involved: arguments are passed as a list. Output and error are drained concurrently. The process tree is killed
/// when it passes the timeout or the memory ceiling, and also on cancellation, so Ctrl+C never leaves a child running.
/// </summary>
internal static class ChildProcess
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<CliRun> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        TimeSpan timeout,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (string a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {fileName}");
        try
        {
            // Not tied to the token: the drains must finish after a kill so the pipes close cleanly.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var clock = Stopwatch.StartNew();
            long peak = 0;
            string? killed = null;

            // PeakWorkingSet64 is not reported on macOS, so the peak is sampled from the current working set.
            Task exited = process.WaitForExitAsync(CancellationToken.None);
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(PollInterval, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (exited.IsCompleted) break;

                peak = Math.Max(peak, WorkingSet(process));
                if (clock.Elapsed > timeout) killed = $"timed out after {timeout.TotalSeconds:F0} s";
                else if (peak > maxBytes) killed = $"exceeded {maxBytes / (1024 * 1024)} MB";
                if (killed is null) continue;

                Kill(process);
                break;
            }

            await exited.ConfigureAwait(false);
            clock.Stop();
            return new CliRun(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), clock.Elapsed, peak, killed);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // exited between the check and the kill
        }
    }

    private static long WorkingSet(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return 0; // exited between the check and the read
        }
    }
}

/// <summary>Runs the built CLI under test (<c>dotnet ZeroDep.Cli.dll …</c>) with the gate's timeout and memory ceiling.</summary>
internal sealed class CliProcess
{
    private readonly string _cliDll;

    public CliProcess(string cliDll, TimeSpan timeout, long maxBytes)
    {
        _cliDll = cliDll;
        Timeout = timeout;
        MaxBytes = maxBytes;
    }

    public TimeSpan Timeout { get; }

    public long MaxBytes { get; }

    public Task<CliRun> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        => RunAsync(args, Timeout, cancellationToken);

    /// <summary>Runs with a different timeout; the batch run covers every sampled document in one process.</summary>
    public Task<CliRun> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
        => ChildProcess.RunAsync("dotnet", [_cliDll, .. args], null, timeout, MaxBytes, cancellationToken);
}

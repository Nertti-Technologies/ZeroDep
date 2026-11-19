using System.Diagnostics;

namespace ZeroDep.Smoke;

/// <summary>What one run of the CLI did.</summary>
internal sealed record CliRun(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed, long PeakBytes, string? Killed);

/// <summary>
/// Runs the built CLI as a child process, so the gate tests what ships. The run is killed (with its process
/// tree) when it passes the timeout or the memory ceiling.
/// </summary>
internal sealed class CliProcess
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly string _cliDll;
    private readonly TimeSpan _timeout;
    private readonly long _maxBytes;

    public CliProcess(string cliDll, TimeSpan timeout, long maxBytes)
    {
        _cliDll = cliDll;
        _timeout = timeout;
        _maxBytes = maxBytes;
    }

    public async Task<CliRun> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(_cliDll);
        foreach (string a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start dotnet");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var clock = Stopwatch.StartNew();
        long peak = 0;
        string? killed = null;

        // PeakWorkingSet64 is not reported on macOS, so the peak is sampled from the current working set.
        Task exited = process.WaitForExitAsync(cancellationToken);
        while (!exited.IsCompleted)
        {
            await Task.WhenAny(exited, Task.Delay(PollInterval, cancellationToken)).ConfigureAwait(false);
            if (exited.IsCompleted) break;

            peak = Math.Max(peak, WorkingSet(process));
            if (clock.Elapsed > _timeout) killed = $"timed out after {_timeout.TotalSeconds:F0} s";
            else if (peak > _maxBytes) killed = $"exceeded {_maxBytes / (1024 * 1024)} MB";
            if (killed is null) continue;

            process.Kill(entireProcessTree: true);
            break;
        }

        await exited.ConfigureAwait(false);
        clock.Stop();
        return new CliRun(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), clock.Elapsed, peak, killed);
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

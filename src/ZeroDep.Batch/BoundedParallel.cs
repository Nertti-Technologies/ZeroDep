using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroDep.Batch;

/// <summary>
/// A bounded parallel loop for targets without <c>Parallel.ForEachAsync</c> (netstandard2.0). It is
/// compiled for every target so it can be tested on any of them.
/// </summary>
internal static class BoundedParallel
{
    /// <summary>
    /// Runs <paramref name="body"/> for each item, at most <paramref name="degree"/> at a time. It returns
    /// only after every started item has finished, even when cancellation or a failure ends the loop early,
    /// so nothing the items use (the gate, or the caller's ledger writer) is disposed while one still runs.
    /// </summary>
    public static async Task ForEachAsync(IEnumerable<string> items, int degree, Action<string> body, CancellationToken cancellationToken)
    {
        using (var gate = new SemaphoreSlim(degree))
        {
            var tasks = new List<Task>();
            try
            {
                foreach (string item in items)
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    tasks.Add(Task.Run(
                        () =>
                        {
                            try
                            {
                                body(item);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        },
                        cancellationToken));
                }
            }
            finally
            {
                await WaitQuietlyAsync(tasks).ConfigureAwait(false);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    // Waits for every task; failures are surfaced by the caller's own WhenAll, or are superseded by the
    // exception that ended the loop.
    private static async Task WaitQuietlyAsync(List<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // intentionally ignored here; see the comment above this method
        }
    }
}

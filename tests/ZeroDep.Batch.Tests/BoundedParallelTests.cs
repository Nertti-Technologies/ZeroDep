using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ZeroDep.Batch.Tests;

public sealed class BoundedParallelTests
{
    [Fact]
    public async Task RunsEveryItem()
    {
        int count = 0;
        await BoundedParallel.ForEachAsync(new[] { "a", "b", "c", "d" }, 2, _ => Interlocked.Increment(ref count), CancellationToken.None);
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task CancellationWaitsForStartedWorkBeforeCompleting()
    {
        using var cts = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int finished = 0;

        Task run = BoundedParallel.ForEachAsync(
            new[] { "a", "b" },
            1,
            item =>
            {
                if (item == "a")
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    Volatile.Write(ref finished, 1);
                }
            },
            cts.Token);

        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        cts.Cancel();
        await Task.Delay(200);

        // The loop must not finish (and release what the work uses) while "a" is still running.
        Assert.False(run.IsCompleted);

        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, Volatile.Read(ref finished));
    }
}

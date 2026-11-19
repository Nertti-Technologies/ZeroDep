using Xunit;

namespace ZeroDep.Objects.Tests;

/// <summary>
/// Runs crafted input with a time limit and measures what it allocates on its own thread, so a regression
/// shows up as a failed assertion instead of a hung or exhausted test run.
/// </summary>
internal static class Bounded
{
    public const long DefaultMaxBytes = 300L * 1024 * 1024;

    /// <summary>Runs <paramref name="action"/>; returns the exception it threw (or null).</summary>
    public static Exception? Run(Action action, long maxBytes = DefaultMaxBytes, int seconds = 10)
    {
        Exception? caught = null;
        long allocated = 0;
        Task task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), $"Did not finish within {seconds} s.");
        Assert.True(allocated < maxBytes, $"Allocated {allocated / (1024 * 1024)} MiB (limit {maxBytes / (1024 * 1024)} MiB).");
        return caught;
    }

    /// <summary>Runs <paramref name="action"/> and asserts it threw <typeparamref name="T"/> (exactly).</summary>
    public static T Throws<T>(Action action, long maxBytes = DefaultMaxBytes)
        where T : Exception
    {
        Exception? ex = Run(action, maxBytes);
        Assert.NotNull(ex);
        return Assert.IsType<T>(ex);
    }
}

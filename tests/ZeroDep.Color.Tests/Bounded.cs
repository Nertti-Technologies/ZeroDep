using System;
using System.Threading.Tasks;
using Xunit;

namespace ZeroDep.Color.Tests;

/// <summary>
/// Runs a crafted-input case with a time limit and measures its managed allocation on the thread that ran
/// it. Returns the exception the case threw, or null.
/// </summary>
internal static class Bounded
{
    public const long MaxAllocatedBytes = 300L * 1024 * 1024;

    public static Exception? Run(Action action, long maxBytes = MaxAllocatedBytes, int seconds = 10)
    {
        Exception? thrown = null;
        long allocated = 0;
        var task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), $"did not finish within {seconds} s");
        Assert.True(allocated < maxBytes, $"allocated {allocated:N0} bytes");
        return thrown;
    }
}

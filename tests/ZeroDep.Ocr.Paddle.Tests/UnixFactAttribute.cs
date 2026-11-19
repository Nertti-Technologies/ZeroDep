using System;
using Xunit;

namespace ZeroDep.Ocr.Paddle.Tests;

/// <summary>A fact that needs a POSIX shell to run a stand-in executable; skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Uses a POSIX shell script as a stand-in executable.";
        }
    }
}

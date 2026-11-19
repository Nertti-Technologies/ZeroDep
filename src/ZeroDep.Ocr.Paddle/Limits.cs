using System;

namespace ZeroDep.Ocr.Paddle;

/// <summary>
/// Fixed limits for the PaddleOCR adapter, one place per value so a later change can make them
/// configurable without touching the enforcement points.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// How long the worker may take to load its models and report ready: 10 min. <b>Estimate</b>: a warm
    /// start loads in seconds, but the first run downloads the models (on the order of 100 MB), which
    /// can take minutes on a slow link.
    /// </summary>
    internal static readonly TimeSpan InitTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long one image may take to recognize before the worker is killed: 120 s. <b>Estimate</b>: a
    /// full A4 page at 300 dpi recognizes in seconds to tens of seconds on a CPU, so this leaves headroom
    /// for slow machines while still ending a worker that has hung.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long <see cref="PaddleOcrEngine.Dispose"/> lets the worker exit on its own after its input is
    /// closed before killing it: 3 s. <b>Estimate</b>: the worker exits as soon as it reads end of input.
    /// </summary>
    internal static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long to wait for a killed worker to be reaped: 5 s. <b>Estimate</b>: process teardown after a
    /// kill signal normally takes milliseconds.
    /// </summary>
    internal static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);
}

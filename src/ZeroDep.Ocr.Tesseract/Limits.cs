using System;

namespace ZeroDep.Ocr.Tesseract;

/// <summary>
/// Fixed limits for the Tesseract adapter, one place per value so a later change can make them
/// configurable without touching the enforcement points.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// How long one <c>tesseract</c> run may take before the process tree is killed: 120 s per image.
    /// <b>Estimate</b>: a full A4 page at 300 dpi typically recognizes in a few seconds on one core, so
    /// this allows more than an order of magnitude for slow machines and dense pages while still ending
    /// a run that has hung.
    /// </summary>
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long to wait for a killed process to be reaped: 5 s. <b>Estimate</b>: process teardown after
    /// a kill signal normally takes milliseconds.
    /// </summary>
    internal static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);
}

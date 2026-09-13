using System;

namespace Optimum.Render.Vulkan.Present;

/// <summary>
/// The rendered-frame cap the latency backend is given once frame generation can
/// be active (ROADMAP, "The paced present: the design", present modes). Pure, so
/// every row of the policy is a unit test (PacerTests).
/// </summary>
internal static class PacedFrameCap
{
    /// <summary>Assumed when the display does not report a refresh rate; the refresh source logs that once.</summary>
    public const double FallbackRefreshHz = 60.0;

    /// <summary>
    /// The minimum interval between rendered-frame starts, in microseconds (0 = uncapped).
    /// <list type="bullet">
    /// <item>Frame generation off: <paramref name="userCapUs" /> unchanged - exactly the value
    /// <c>SetLatencyFrameCap</c> computes today, so "off is vanilla".</item>
    /// <item>Vsync off (IMMEDIATE): <paramref name="userCapUs" /> unchanged; the pacer owns the spacing.</item>
    /// <item>Frame generation and vsync on (FIFO): at least two refresh intervals, so each
    /// rendered frame's two presents fit two refreshes. Rendering faster only queues pairs
    /// behind FIFO, which the pacer then has to release back to back. A user cap slower
    /// than that still wins: the player never gets more rendered frames than they asked for.</item>
    /// </list>
    /// </summary>
    /// <param name="refreshHz">The display refresh rate; not positive or not finite means unknown (60 Hz).</param>
    public static ulong MinimumIntervalUs(bool frameGenerationActive, bool vsync, double refreshHz, ulong userCapUs)
    {
        if (!frameGenerationActive || !vsync) return userCapUs;

        double hz = refreshHz > 0 && double.IsFinite(refreshHz) ? refreshHz : FallbackRefreshHz;
        ulong pairIntervalUs = (ulong)Math.Round(2_000_000.0 / hz);
        return Math.Max(pairIntervalUs, userCapUs);
    }
}

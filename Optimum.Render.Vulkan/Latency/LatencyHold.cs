using System.Threading;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The one wait-until-a-timestamp primitive. Moved out of
/// <see cref="NativeLatencyBackend" /> so the present thread can hold a pair's
/// real frame until the pacer's target with exactly the sleep-then-spin the frame
/// cap already measured, instead of a second implementation with its own jitter.
/// </summary>
internal static class LatencyHold
{
    /// <summary>
    /// Holds until <paramref name="targetUs" />: coarse sleeping down to
    /// <see cref="NativeLatencyBackend.SpinTailUs" /> of the target, then the bounded spin tail. A
    /// target further away than <see cref="NativeLatencyBackend.MaxHoldUs" /> is treated as a stale
    /// anchor and not waited for at all.
    /// </summary>
    public static void Until(long targetUs)
    {
        long now = LatencyClock.NowUs();
        if (targetUs <= now) return;
        if (targetUs - now > NativeLatencyBackend.MaxHoldUs) return;

        while (targetUs - now > NativeLatencyBackend.SpinTailUs)
        {
            int ms = (int)((targetUs - now - NativeLatencyBackend.SpinTailUs) / 1000);
            if (ms <= 0) break;
            Thread.Sleep(ms);
            now = LatencyClock.NowUs();
        }

        // The tail: at most SpinTailUs of the hold, and only ever the tail.
        while (LatencyClock.NowUs() < targetUs) Thread.SpinWait(64);
    }
}

using System;
using System.Diagnostics;
using System.Threading;

namespace Optimum.Render.Vulkan.Present;

/// <summary>Spaces the real present halfway between successive generated presents.</summary>
internal sealed class GeneratedFramePacer
{
    private readonly Func<long> now;
    private readonly Action<int> sleep;
    private long lastGenerated;
    private long realTarget;
    private double renderIntervalTicks = Stopwatch.Frequency / 60.0;

    internal GeneratedFramePacer(Func<long>? now = null, Action<int>? sleep = null)
    {
        this.now = now ?? Stopwatch.GetTimestamp;
        this.sleep = sleep ?? Thread.Sleep;
    }

    internal void NoteGeneratedPresent()
    {
        long timestamp = now();
        if (lastGenerated != 0)
        {
            long elapsed = timestamp - lastGenerated;
            if (elapsed > 0 && elapsed < Stopwatch.Frequency / 10)
                renderIntervalTicks = Math.Clamp(renderIntervalTicks * 0.875 + elapsed * 0.125,
                    Stopwatch.Frequency / 1000.0, Stopwatch.Frequency / 10.0);
        }
        lastGenerated = timestamp;
        realTarget = timestamp + (long)(renderIntervalTicks * 0.5);
    }

    internal long PendingRealTargetTicks => realTarget;

    internal void WaitForRealPresent()
    {
        long target = realTarget;
        realTarget = 0;
        if (target == 0) return;
        while (true)
        {
            long remaining = target - now();
            if (remaining <= 0) return;
            int ms = (int)(remaining * 1000 / Stopwatch.Frequency);
            if (ms > 1) sleep(ms - 1);
            else Thread.SpinWait(32);
        }
    }

    internal void Reset()
    {
        lastGenerated = 0;
        realTarget = 0;
        renderIntervalTicks = Stopwatch.Frequency / 60.0;
    }
}

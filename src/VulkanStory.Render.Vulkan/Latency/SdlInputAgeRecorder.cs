using System;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Sampled SDL dispatch-age statistics in milliseconds and the event count used to calculate them.</summary>
internal readonly record struct SdlInputAgeSample(int Events, double MeanMs, double P99Ms, double MaxMs);

/// <summary>Bounded ages of SDL input events at dispatch, measured on SDL's own clock.</summary>
internal sealed class SdlInputAgeRecorder
{
    private readonly ulong[] agesNanoseconds;
    private readonly object sync = new();
    private int next;
    private int count;

    /// <summary>Allocates a bounded SDL event-age ring with at least one entry.</summary>
    public SdlInputAgeRecorder(int capacity = 2048) =>
        agesNanoseconds = new ulong[Math.Max(1, capacity)];

    /// <summary>Records dispatch minus event time on the same nanosecond clock.</summary>
    /// <remarks>Recording and sample extraction synchronize on the same private lock.</remarks>
    /// <param name="eventTimestampNanoseconds">SDL event timestamp; zero samples are ignored.</param>
    /// <param name="dispatchTimestampNanoseconds">Dispatch timestamp; values earlier than the event are ignored.</param>
    public void Record(ulong eventTimestampNanoseconds, ulong dispatchTimestampNanoseconds)
    {
        if (eventTimestampNanoseconds == 0 || dispatchTimestampNanoseconds < eventTimestampNanoseconds)
            return;
        ulong age = dispatchTimestampNanoseconds - eventTimestampNanoseconds;
        lock (sync)
        {
            agesNanoseconds[next] = age;
            next = next + 1 == agesNanoseconds.Length ? 0 : next + 1;
            if (count < agesNanoseconds.Length) count++;
        }
    }

    /// <summary>Atomically consumes the current samples and calculates mean, nearest-rank P99 and maximum age.</summary>
    /// <returns>Milliseconds and sample count, or the default zero sample when the ring is empty.</returns>
    public SdlInputAgeSample Take()
    {
        ulong[] samples;
        lock (sync)
        {
            if (count == 0) return default;
            samples = new ulong[count];
            Array.Copy(agesNanoseconds, samples, count);
            count = 0;
            next = 0;
        }
        Array.Sort(samples);
        double sum = 0;
        foreach (ulong age in samples) sum += age / 1_000_000.0;
        int p99Index = Math.Max(0, (int)Math.Ceiling(samples.Length * 0.99) - 1);
        return new SdlInputAgeSample(samples.Length, sum / samples.Length,
            samples[p99Index] / 1_000_000.0, samples[^1] / 1_000_000.0);
    }
}

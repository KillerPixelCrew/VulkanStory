using System;
using System.Diagnostics;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Tests;

public class FrameWaitTests
{
    private readonly ITestOutputHelper output;

    public FrameWaitTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void HighResolutionTimerWaitsUntilFrameDeadlineWhenAvailable()
    {
        output.WriteLine($"High-resolution timer available: {OptimumFrameWait.IsSupported}");
        if (!OperatingSystem.IsWindows() || !OptimumFrameWait.IsSupported) return;

        var frame = Stopwatch.StartNew();
        long targetTicks = Stopwatch.Frequency / 50; // 20 ms
        Assert.True(OptimumFrameWait.TryWait(frame, targetTicks));
        Assert.True(frame.ElapsedTicks >= targetTicks);
        Assert.InRange(frame.ElapsedMilliseconds, 20, 1000);
    }
}

using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class IdleThreadWaitTests
{
    [Theory]
    [InlineData(0, 0, 20, 5, 25, 21)]
    [InlineData(15, 0, 20, 5, 25, 6)]
    [InlineData(19, 0, 20, 5, 25, 5)]
    [InlineData(100, 0, 20, 2, 25, 2)]
    [InlineData(0, 0, 1, 5, 25, 5)]
    [InlineData(0, 0, 1000, 2, 25, 25)]
    public void IdleDelayHonorsNextTickMinimumAndPollCap(long elapsed, long last,
        int interval, int minimum, int maximum, int expected)
    {
        Assert.Equal(expected, OptimumConfig.IdleThreadDelayMs(elapsed, last,
            interval, minimum, maximum));
    }
}

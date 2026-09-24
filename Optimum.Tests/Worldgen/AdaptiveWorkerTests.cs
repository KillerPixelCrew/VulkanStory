// Source: Optimum.Tests/adaptive-tess-worker-controller-tests.cs
namespace Optimum.Tests
{
using System.Threading;
using Vintagestory.API.Config;
using Xunit;

public class AdaptiveTessWorkerControllerTests
{
    [Fact]
    public void ZeroMaxCapStaysZero()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 0, evalInterval: 10);
        Assert.Equal(0, ctrl.ActiveWorkerCap);

        ctrl.ForceCapForTesting(3);
        Assert.Equal(0, ctrl.ActiveWorkerCap);

        for (int i = 0; i < 10; i++)
            ctrl.RecordChunk(backpressureTicks: 1000, workTicks: 1000);

        Assert.Equal(0, ctrl.Evaluate(queueDepth: 50));
        ctrl.Reset();
        Assert.Equal(0, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void HighBackpressureScalesDown()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(4);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 8000, workTicks: 2000);

        int newCap = ctrl.Evaluate(queueDepth: 2);
        Assert.True(newCap < 4, $"Expected scale-down, got cap={newCap}");
    }

    [Fact]
    public void LowBackpressureScalesUp()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(2);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 100, workTicks: 9000);

        int newCap = ctrl.Evaluate(queueDepth: 20);
        Assert.True(newCap > 2, $"Expected scale-up, got cap={newCap}");
    }

    [Fact]
    public void StarvationGuardPreventsScaleDown()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(3);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 6000, workTicks: 4000);

        int newCap = ctrl.Evaluate(queueDepth: 100);
        Assert.Equal(3, newCap);
    }

    [Fact]
    public void NeverExceedsMaxWorkers()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 2, evalInterval: 5);
        ctrl.ForceCapForTesting(2);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 0, workTicks: 10000);

        int newCap = ctrl.Evaluate(queueDepth: 20);
        Assert.True(newCap <= 2);
    }

    [Fact]
    public void SetMaxWorkersClampsCurrent()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        Assert.Equal(4, ctrl.ActiveWorkerCap);

        ctrl.SetMaxWorkers(1);
        Assert.Equal(1, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void ResetRestoresMaxWorkerCap()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 3, evalInterval: 5);
        ctrl.ForceCapForTesting(1);
        Assert.Equal(1, ctrl.ActiveWorkerCap);

        ctrl.Reset();
        Assert.Equal(3, ctrl.ActiveWorkerCap);
        Assert.Equal(0.0, ctrl.BackpressureRatio);
    }

    [Fact]
    public void EwmaSmoothsPeaks()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(3);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 5000, workTicks: 5000);
        ctrl.Evaluate(queueDepth: 10);
        double first = ctrl.BackpressureRatio;

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 0, workTicks: 10000);
        ctrl.Evaluate(queueDepth: 10);
        double second = ctrl.BackpressureRatio;

        Assert.True(second < first, $"EWMA should smooth down: first={first}, second={second}");
        Assert.True(second > 0, "EWMA should not drop to zero after one good interval");
    }

    [Fact]
    public void OnCapChangedFires()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(4);

        int? reportedOld = null;
        int? reportedNew = null;
        ctrl.OnCapChanged = (old, @new, _) => { reportedOld = old; reportedNew = @new; };

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 9000, workTicks: 1000);
        ctrl.Evaluate(queueDepth: 2);

        Assert.NotNull(reportedOld);
        Assert.NotNull(reportedNew);
        Assert.True(reportedNew < reportedOld);
    }

    [Fact]
    public void NoScaleUpWithEmptyQueue()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(2);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 0, workTicks: 10000);

        int newCap = ctrl.Evaluate(queueDepth: 0);
        Assert.Equal(2, newCap);
    }

    [Fact]
    public void ResetWithNewMax()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.Reset(newMax: 2);
        Assert.Equal(2, ctrl.ActiveWorkerCap);
    }
}
}

// Source: Optimum.Tests/adaptive-worker-controller-tests.cs
namespace Optimum.Tests
{
using Vintagestory.API.Config;
using Xunit;

public class AdaptiveWorkerControllerTests
{
    [Fact]
    public void ZeroMaxCapStaysZeroAcrossControllerOperations()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 0, evalInterval: 10);

        Assert.Equal(0, ctrl.ActiveWorkerCap);

        ctrl.ForceCapForTesting(3);
        Assert.Equal(0, ctrl.ActiveWorkerCap);

        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 0, workTicks: 1000);
        }

        Assert.Equal(0, ctrl.Evaluate(queueDepth: 100));

        ctrl.Reset();
        Assert.Equal(0, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void ResetToZeroMaxCapBlocksScaleUp()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);

        ctrl.Reset(newMax: 0);
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 0, workTicks: 1000);
        }

        Assert.Equal(0, ctrl.Evaluate(queueDepth: 100));
        Assert.Equal(0, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void ColdStartUsesMaxCap()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3);
        Assert.Equal(3, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void ScalesDownOnHighContention()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);

        // Simulate 10 chunks with 60% lock wait (well above 40% threshold)
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 600, workTicks: 400);
        }

        Assert.True(ctrl.ShouldEvaluate());
        int newCap = ctrl.Evaluate(queueDepth: 5); // queue below pressure threshold

        // Should drop from 3 to 2
        Assert.Equal(2, newCap);
        Assert.Equal(2, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void DoesNotScaleBelowOne()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 2, evalInterval: 10);

        // Drive contention high twice to force cap to 1
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < 10; i++)
            {
                ctrl.RecordChunk(lockWaitTicks: 800, workTicks: 200);
            }
            ctrl.Evaluate(queueDepth: 5);
        }

        // Cap should be 1, not 0
        Assert.Equal(1, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void ScalesUpOnLowContentionWithQueuePressure()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);
        ctrl.ForceCapForTesting(1); // Start at 1

        // Simulate 10 chunks with 5% lock wait (well below 15% threshold)
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 50, workTicks: 950);
        }

        int newCap = ctrl.Evaluate(queueDepth: 20); // queue above pressure floor

        // Should scale up from 1 to 2
        Assert.Equal(2, newCap);
    }

    [Fact]
    public void DoesNotScaleUpWithoutQueuePressure()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);
        ctrl.ForceCapForTesting(1);

        // Low contention but empty queue
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 50, workTicks: 950);
        }

        int newCap = ctrl.Evaluate(queueDepth: 2); // queue below floor

        // Should stay at 1: no work to justify more workers
        Assert.Equal(1, newCap);
    }

    [Fact]
    public void DoesNotScaleUpAboveMax()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);

        // Already at max, low contention, deep queue
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 10, workTicks: 990);
        }

        int newCap = ctrl.Evaluate(queueDepth: 100);

        // Already at max 3, should stay
        Assert.Equal(3, newCap);
    }

    [Fact]
    public void HysteresisPreventsThrashing()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);

        // Moderate contention (25%) sits between the scale-down (40%) and
        // scale-up (15%) bands. No adjustment should occur.
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 250, workTicks: 750);
        }

        int newCap = ctrl.Evaluate(queueDepth: 20);
        Assert.Equal(3, newCap); // Stays at max, in the dead zone
    }

    [Fact]
    public void EwmaSmoothsSpikes()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);

        // First window: low contention (5%), seeds the EWMA
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 50, workTicks: 950);
        }
        ctrl.Evaluate(queueDepth: 20);

        // Second window: spike to 80% contention
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 800, workTicks: 200);
        }
        int cap = ctrl.Evaluate(queueDepth: 5);

        // EWMA after spike: 0.3 * 0.80 + 0.7 * 0.05 = 0.275
        // 0.275 < 0.40 threshold, so no scale-down yet (spike gets smoothed)
        Assert.Equal(3, cap);

        // Third window: still high (80%)
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 800, workTicks: 200);
        }
        cap = ctrl.Evaluate(queueDepth: 5);

        // EWMA: 0.3 * 0.80 + 0.7 * 0.275 = 0.4325 > 0.40, now scales down
        Assert.Equal(2, cap);
    }

    [Fact]
    public void StarvationGuardPreventsScaleDownUnderLoad()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);

        // High contention (50%) but the queue is massive (growing workload)
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 500, workTicks: 500);
        }

        // Queue depth > QueuePressureFloor * current (10 * 3 = 30)
        int newCap = ctrl.Evaluate(queueDepth: 50);

        // Starvation guard blocks the scale-down
        Assert.Equal(3, newCap);
    }

    [Fact]
    public void ShouldEvaluateRespectsInterval()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 50);

        for (int i = 0; i < 49; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 100, workTicks: 100);
        }
        Assert.False(ctrl.ShouldEvaluate());

        ctrl.RecordChunk(lockWaitTicks: 100, workTicks: 100);
        Assert.True(ctrl.ShouldEvaluate());
    }

    [Fact]
    public void ResetClearsState()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);
        ctrl.ForceCapForTesting(1);

        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 800, workTicks: 200);
        }

        ctrl.Reset();

        Assert.Equal(3, ctrl.ActiveWorkerCap);
        Assert.Equal(0.0, ctrl.ContentionRatio);
        Assert.Equal(0, ctrl.TotalChunksGenerated);
        Assert.False(ctrl.ShouldEvaluate());
    }

    [Fact]
    public void FullAdaptiveCycle_ConvergesToSteadyState()
    {
        // Simulate a realistic scenario: start at max (3), heavy pass-3/4
        // workload causes high contention, controller drops to 2 then stabilizes.
        var ctrl = new AdaptiveWorkerController(maxWorkers: 3, evalInterval: 10);

        // Phase 1: heavy illuminator passes, 60% contention for several windows
        for (int window = 0; window < 5; window++)
        {
            for (int i = 0; i < 10; i++)
            {
                ctrl.RecordChunk(lockWaitTicks: 600, workTicks: 400);
            }
            ctrl.Evaluate(queueDepth: 5);
        }

        // After sustained high contention, should have dropped to 1 (3->2->1)
        Assert.Equal(1, ctrl.ActiveWorkerCap);

        // Phase 2: with 1 worker, no lock contention (passes don't overlap).
        // EWMA decays: needs ~5 windows of 0% to drop below the 15% scale-up
        // threshold from its saturated ~0.60 level.
        for (int window = 0; window < 6; window++)
        {
            for (int i = 0; i < 10; i++)
            {
                ctrl.RecordChunk(lockWaitTicks: 0, workTicks: 1000);
            }
            ctrl.Evaluate(queueDepth: 50); // deep queue
        }

        // Should have scaled back up since contention vanished and queue is deep
        Assert.True(ctrl.ActiveWorkerCap >= 2, $"Expected cap >= 2, got {ctrl.ActiveWorkerCap} (EWMA: {ctrl.ContentionRatio:F3})");
    }
}
}

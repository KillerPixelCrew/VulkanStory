// Source: Optimum.Tests/optimum-bounded-handoff-tests.cs
namespace Optimum.Tests
{
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

// Shares OptimumDiagnostics' static tessellation counters with
// OptimumDiagnosticsCountersTests's worker-registry test - both must run in the same
// xUnit collection (serialized) or ResetTessellation() calls from one interleave with
// the other's assertions under xUnit's default cross-class parallelism.
[Collection("TessellationDiagnostics")]
public sealed class OptimumBoundedHandoffTests
{
    [Fact]
    public void CapacityLeavesRoomForPriorityWork()
    {
        var handoff = new OptimumBoundedHandoff(4, priorityReserve: 1);

        Assert.True(handoff.TryReserve(priority: false));
        Assert.True(handoff.TryReserve(priority: false));
        Assert.True(handoff.TryReserve(priority: false));
        Assert.False(handoff.TryReserve(priority: false));
        Assert.True(handoff.TryReserve(priority: true));
        Assert.Equal(4, handoff.Reserved);

        handoff.Release();
        Assert.True(handoff.TryReserve(priority: true));
        Assert.Equal(4, handoff.Reserved);
    }

    [Fact]
    public void ConcurrentReservationsNeverExceedCapacity()
    {
        var handoff = new OptimumBoundedHandoff(32, priorityReserve: 8);
        int accepted = 0;

        Parallel.For(0, 512, _ =>
        {
            if (handoff.TryReserve(priority: false))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        Assert.Equal(24, accepted);
        Assert.Equal(24, handoff.Reserved);

        for (int i = 0; i < accepted; i++)
        {
            handoff.Release();
        }

        Assert.Equal(0, handoff.Reserved);
    }

    // Step 6 of the worker-pool wiring plan: the handoff must publish its capacity and a
    // running reservation peak to OptimumDiagnostics, so `.optimum status` can prove the
    // worker pool's reserve/release path is actually live without a debugger.
    [Fact]
    public void ConstructorAndTryReservePublishCapacityAndPeakToDiagnostics()
    {
        OptimumDiagnostics.ResetTessellation();

        var handoff = new OptimumBoundedHandoff(6, priorityReserve: 1);
        Assert.True(handoff.TryReserve(priority: false));
        Assert.True(handoff.TryReserve(priority: false));
        handoff.Release();
        Assert.True(handoff.TryReserve(priority: false));

        string summary = OptimumDiagnostics.GetTessellationSummary();
        // Peak must reflect the high-water mark (2), not the final reserved count (1).
        Assert.Contains("handoffPeak=2/6", summary);

        OptimumDiagnostics.ResetTessellation();
        Assert.Contains("handoffPeak=0/6", OptimumDiagnostics.GetTessellationSummary());
    }
}
}

// Source: Optimum.Tests/optimum-tesselation-safety-gate-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

public class OptimumTesselationSafetyGateTests
{
    [Fact]
    public void ForeignTextureSourceForcesSingleWorker()
    {
        var types = new[] { typeof(ForeignTextureSource) };

        List<string> foreignTypes = OptimumTesselationSafetyGate.FindForeignTextureSources(types);

        Assert.Contains(typeof(ForeignTextureSource).FullName!, foreignTypes);
        Assert.Equal(1, OptimumTesselationSafetyGate.CapWorkerCount(4, types));
    }

    [Fact]
    public void KnownApiTextureSourceKeepsRequestedWorkerCount()
    {
        var types = new[] { typeof(ContainedTextureSource) };

        Assert.Empty(OptimumTesselationSafetyGate.FindForeignTextureSources(types));
        Assert.Equal(4, OptimumTesselationSafetyGate.CapWorkerCount(4, types));
    }

    private sealed class ForeignTextureSource : ITexPositionSource
    {
        public TextureAtlasPosition? this[string textureCode] => null;

        public Size2i? AtlasSize => null;
    }
}
}

// Source: Optimum.Tests/optimum-tesselation-worker-registry-tests.cs
namespace Optimum.Tests
{
using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

public class OptimumTesselationWorkerRegistryTests
{
    [Fact]
    public void RegisteredThreadIsRecognizedAndUnknownThreadIsRejected()
    {
        var registry = new OptimumTesselationWorkerRegistry();

        int firstSlot = registry.Register(17);
        int secondSlot = registry.Register(17);

        Assert.True(registry.Contains(17));
        Assert.False(registry.Contains(18));
        Assert.Equal(firstSlot, secondSlot);
        Assert.Equal(firstSlot, registry.GetSlot(17));
        Assert.Equal(0, registry.GetSlot(18));
    }

    [Fact]
    public void ConcurrentRegistrationKeepsAllWorkersVisible()
    {
        var registry = new OptimumTesselationWorkerRegistry();

        Parallel.For(1, 65, threadId => registry.Register(threadId));

        var slots = new HashSet<int>();
        for (int threadId = 1; threadId < 65; threadId++)
        {
            Assert.True(registry.Contains(threadId));
            Assert.True(slots.Add(registry.GetSlot(threadId)));
        }
        Assert.Equal(64, slots.Count);
        Assert.Contains(0, slots);
        Assert.Contains(63, slots);
    }
}
}

// Source: Optimum.Tests/optimum-thread-guard-tests.cs
namespace Optimum.Tests
{
using System;
using System.Threading;
using Vintagestory.API.Config;
using Xunit;

public class OptimumThreadGuardTests
{
    public OptimumThreadGuardTests()
    {
        // Ensure clean state per test
        OptimumThreadGuard.Enable();
        OptimumThreadGuard.ResetViolations();
    }

    [Fact]
    public void SameThreadPassesVerify()
    {
        var guard = new OptimumThreadGuard();
        guard.Mark();
        Assert.True(guard.Verify());
        Assert.Equal(0, OptimumThreadGuard.ViolationCount);
    }

    [Fact]
    public void DifferentThreadFailsVerify()
    {
        var guard = new OptimumThreadGuard();
        guard.Mark(); // Marks on current (xUnit) thread

        bool passed = true;
        var t = new Thread(() =>
        {
            passed = guard.Verify(throwOnViolation: false);
        });
        t.Start();
        t.Join();

        Assert.False(passed);
        Assert.Equal(1, OptimumThreadGuard.ViolationCount);
    }

    [Fact]
    public void DifferentThreadThrowsWhenRequested()
    {
        var guard = new OptimumThreadGuard();
        guard.Mark();

        Exception caught = null;
        var t = new Thread(() =>
        {
            try { guard.Verify(throwOnViolation: true); }
            catch (Exception ex) { caught = ex; }
        });
        t.Start();
        t.Join();

        Assert.NotNull(caught);
        Assert.IsType<System.InvalidOperationException>(caught);
    }

    [Fact]
    public void DisabledGuardDoesNothing()
    {
        OptimumThreadGuard.Disable();

        var guard = new OptimumThreadGuard();
        guard.Mark();

        // Even on different thread, no violation when disabled
        bool passed = false;
        var t = new Thread(() => { passed = guard.Verify(); });
        t.Start();
        t.Join();

        Assert.True(passed);
        Assert.Equal(0, OptimumThreadGuard.ViolationCount);

        OptimumThreadGuard.Enable(); // Restore for other tests
    }

    [Fact]
    public void UnmarkedGuardSkipsVerify()
    {
        var guard = new OptimumThreadGuard();
        // Never call Mark()
        Assert.True(guard.Verify());
        Assert.Equal(0, OptimumThreadGuard.ViolationCount);
    }
}
}

// Source: Optimum.Tests/optimum-worker-instances-tests.cs
namespace Optimum.Tests
{
using System;
using Vintagestory.API.Config;
using Xunit;

public class OptimumWorkerInstancesTests
{
    [Fact]
    public void SlotsReturnDistinctInstances()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(4);
        Assert.Equal(4, pool.SlotCount);

        var a = pool.Get(0);
        var b = pool.Get(1);
        var c = pool.Get(2);
        var d = pool.Get(3);

        Assert.NotSame(a, b);
        Assert.NotSame(b, c);
        Assert.NotSame(c, d);
    }

    [Fact]
    public void SameSlotReturnsSameReference()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(3);
        Assert.Same(pool.Get(1), pool.Get(1));
        Assert.Same(pool.Get(0), pool.Get(0));
    }

    [Fact]
    public void MutatingOneSlotDoesNotAffectAnother()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(2);

        pool.Get(0).Counter = 42;
        Assert.Equal(0, pool.Get(1).Counter);
    }

    [Fact]
    public void OutOfRangeThrows()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Get(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Get(2));
    }

    [Fact]
    public void ZeroSlotCountThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimumWorkerInstances<TestWorkerState>(0));
    }

    [Fact]
    public void FactoryConstructorBuildsDistinct()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(3, i => new TestWorkerState { Counter = i * 10 });
        Assert.Equal(0, pool.Get(0).Counter);
        Assert.Equal(10, pool.Get(1).Counter);
        Assert.Equal(20, pool.Get(2).Counter);
        Assert.NotSame(pool.Get(0), pool.Get(1));
    }

    [Fact]
    public void FactoryReturningNullThrows()
    {
        Assert.Throws<InvalidOperationException>(() => new OptimumWorkerInstances<TestWorkerState>(2, _ => null!));
    }

    internal class TestWorkerState
    {
        public int Counter { get; set; }
    }
}
}

// Source: Optimum.Tests/optimum-worldgen-footprint-lease-tests.cs
namespace Optimum.Tests
{
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

public sealed class OptimumWorldgenFootprintLeaseTests
{
    [Fact]
    public async Task ConflictingFootprintsHaveOneOwnerUntilRelease()
    {
        var registry = new OptimumWorldgenFootprintRegistry();
        var keys = new[] { new OptimumWorldgenFootprintKey(0, 10, 20) };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Task<bool> owner = Task.Run(() =>
        {
            if (!registry.TryAcquire(keys, out OptimumWorldgenFootprintLease? lease)) return false;
            entered.Set();
            release.Wait();
            lease!.Dispose();
            return true;
        });

        Assert.True(entered.Wait(5000));
        Assert.False(registry.TryAcquire(keys, out OptimumWorldgenFootprintLease? blocked));
        Assert.Null(blocked);

        release.Set();
        Assert.True(await owner);
        Assert.True(registry.TryAcquire(keys, out OptimumWorldgenFootprintLease? available));
        available!.Dispose();
    }

    [Fact]
    public void FailedAcquireRollsBackEveryPartialReservation()
    {
        var registry = new OptimumWorldgenFootprintRegistry();
        var heldKey = new OptimumWorldgenFootprintKey(0, 1, 1);
        var freeKey = new OptimumWorldgenFootprintKey(0, 2, 2);
        var overlap = new[] { heldKey, freeKey };

        Assert.True(registry.TryAcquire(new[] { heldKey }, out OptimumWorldgenFootprintLease? held));
        Assert.False(registry.TryAcquire(overlap, out OptimumWorldgenFootprintLease? failed));
        Assert.Null(failed);

        held!.Dispose();
        Assert.True(registry.TryAcquire(overlap, out OptimumWorldgenFootprintLease? recovered));
        recovered!.Dispose();
    }

    [Fact]
    public void DuplicateKeysProduceOneReservation()
    {
        var registry = new OptimumWorldgenFootprintRegistry();
        var key = new OptimumWorldgenFootprintKey(-1, -4, 9);
        var duplicateKeys = new List<OptimumWorldgenFootprintKey> { key, key, key };

        Assert.True(registry.TryAcquire(duplicateKeys, out OptimumWorldgenFootprintLease? lease));
        Assert.False(registry.TryAcquire(new[] { key }, out OptimumWorldgenFootprintLease? blocked));
        lease!.Dispose();
        Assert.Null(blocked);
    }

    [Fact]
    public void UnloadCannotReserveAColumnInsideAWorkerFootprint()
    {
        var registry = new OptimumWorldgenFootprintRegistry();
        var workerFootprint = new[]
        {
            new OptimumWorldgenFootprintKey(0, 10, 20),
            new OptimumWorldgenFootprintKey(0, 11, 20),
            new OptimumWorldgenFootprintKey(0, 10, 21),
            new OptimumWorldgenFootprintKey(0, 11, 21),
        };

        Assert.True(registry.TryAcquire(workerFootprint, out OptimumWorldgenFootprintLease? workerLease));
        Assert.False(registry.TryAcquire(
            new[] { new OptimumWorldgenFootprintKey(0, 11, 20) },
            out OptimumWorldgenFootprintLease? unloadLease));
        Assert.Null(unloadLease);

        workerLease!.Dispose();
        Assert.True(registry.TryAcquire(
            new[] { new OptimumWorldgenFootprintKey(0, 11, 20) },
            out OptimumWorldgenFootprintLease? available));
        available!.Dispose();
    }
}
}

// Source: Optimum.Tests/optimum-worldgen-safety-gate-tests.cs
namespace Optimum.Tests
{
using System;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;

public sealed class OptimumDispatchClaimTests
{
    [Fact]
    public void ConcurrentClaimHasOneOwnerAndReleaseRestoresAvailability()
    {
        var claim = new OptimumDispatchClaim();
        int winners = 0;

        Parallel.For(0, 256, _ =>
        {
            if (claim.TryClaim())
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
        claim.Release();
        Assert.True(claim.TryClaim());
        claim.Release();
    }

    [Fact]
    public void ClaimReleaseRunsThroughFinallyAfterFailure()
    {
        var claim = new OptimumDispatchClaim();

        try
        {
            Assert.True(claim.TryClaim());
            throw new InvalidOperationException("fixture failure");
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            claim.Release();
        }

        Assert.True(claim.TryClaim());
        claim.Release();
    }

    [Fact]
    public void ForeignWorldgenAssembliesAreOutsideTheAuditedSet()
    {
        Assert.False(OptimumWorldgenSafetyGate.IsKnownSafeAssembly("Optimum.Tests"));
        Assert.True(OptimumWorldgenSafetyGate.IsKnownSafeAssembly("VSEssentials"));
        Assert.Contains("Optimum.Tests", OptimumWorldgenSafetyGate.FindForeignAssemblies(new[] { "VSEssentials", "Optimum.Tests" }));
    }
}
}

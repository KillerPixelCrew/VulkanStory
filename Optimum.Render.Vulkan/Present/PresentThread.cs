using System;
using System.Threading;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Optimum.Render.Vulkan.Present;

/// <summary>
/// The queue the present thread submits and presents on, and the lock that guards
/// it. vkQueueSubmit and vkQueuePresentKHR on one queue from two threads is
/// undefined behaviour, so whoever shares the queue shares the lock.
/// </summary>
internal readonly record struct PresentQueueBinding(Queue Queue, object Lock, bool SharedWithGraphics)
{
    /// <summary>
    /// A stand-in until the device has a present queue of its own (the queue stream
    /// adds <c>PresentQueue</c> and <c>PresentQueueLock</c> to the context): the
    /// graphics queue under its own <see cref="VulkanContext.QueueLock" />. Correct -
    /// every submit and present still takes the one lock - but not out-of-band: the NV
    /// backend refuses to mark the shared graphics queue, so Reflex keeps counting the
    /// present thread's work as in-band. Tests and the double-present switch only.
    /// </summary>
    public static PresentQueueBinding SharedGraphicsQueueStandIn(VulkanContext context) =>
        new(context.GraphicsQueue, context.QueueLock, SharedWithGraphics: true);
}

/// <summary>One present the present thread made, for tests and diagnostics.</summary>
internal readonly record struct PacedPresentRecord(
    PacedPresentKind Kind,
    ulong PresentId,
    ulong LatencyFrameId,
    int PairIndex,
    ulong FrameValue,
    ulong PresentValue,
    long ReturnUs,
    bool Captured,
    uint CapturedPixel);

/// <summary>
/// The present thread's own timeline as the clock the swapchain keys its acquire
/// semaphores and retired slots on while the present thread owns it.
///
/// Threading: the semaphore is immutable. <see cref="Reserve" /> is called by the
/// present thread; <see cref="FrameRecorded" /> is read by the render thread during
/// the hand-over after the thread stopped, and both go through Interlocked.
/// </summary>
internal sealed unsafe class PresentTimelineClock : ITimelineClock
{
    private readonly VulkanContext _context;
    private long _reserved;

    public PresentTimelineClock(VulkanContext context, Semaphore semaphore)
    {
        _context = context;
        Semaphore = semaphore;
    }

    public Semaphore Semaphore { get; }

    /// <summary>The value the next present submission signals.</summary>
    public ulong Reserve() => (ulong)Interlocked.Increment(ref _reserved);

    /// <summary>The newest value a present submission was given.</summary>
    public ulong FrameRecorded => (ulong)Interlocked.Read(ref _reserved);

    public ulong TransferRecorded => 0;

    public ulong FrameCompleted
    {
        get
        {
            ulong value;
            VulkanResult.Check(_context.Api.GetSemaphoreCounterValue(_context.Device, Semaphore, &value),
                "vkGetSemaphoreCounterValue on the present timeline");
            return value;
        }
    }

    public ulong TransferCompleted => 0;
}

/// <summary>
/// Where the present thread stamps the out-of-band present markers.
///
/// Not <see cref="ILatencyBackend.Marker" /> for the NV backend: that call re-settles
/// the backend's per-frame marker state (<c>BeginMarkerFrame</c>) whenever the frame
/// id differs from the current one - and from the present thread it always does,
/// because the render thread is already on a newer frame. Calling it from here would
/// rewrite the render thread's marker present id under it. The None, Native and AMD
/// backends return before touching any state for an out-of-band marker
/// (<c>LatencyPhaseTracker.Mark</c>), so for them the ordinary call is safe.
///
/// Threading: the backend is captured at construction, on the render thread, and
/// immutable after the hand-off.
/// </summary>
internal sealed class OutOfBandPresentMarkers
{
    private readonly ILatencyBackend _backend;

    public OutOfBandPresentMarkers(ILatencyBackend backend) => _backend = backend;

    public void Mark(ulong presentId, ulong frameId, LatencyMarker marker)
    {
        if (_backend is NvLowLatency2Backend nv)
        {
            nv.OutOfBandPresentMarker(presentId, marker);
            return;
        }
        _backend.Marker(frameId, marker);
    }
}

/// <summary>
/// The present thread of the paced present (ROADMAP, "The paced present: the design").
///
/// It owns the swapchain outright while it runs - acquire, present, rebuild, slot
/// retirement - and presents each output pair the render thread hands off: acquire,
/// blit the interpolated image, submit, present (Generated), tell the pacer, hold
/// until the pacer's target, acquire, blit the real image, submit, present (Real),
/// tell the pacer, release the pair. Every step is sequential on this one thread, so
/// the step-0 hazard of a rebuild between two acquires cannot recur.
///
/// <para><b>Lifetime without a second retire model.</b> Its submissions signal a
/// timeline of its own (<see cref="PresentTimeline" />). A present submission waits on
/// the Frame timeline value that completes its pair's write; the render thread's next
/// write into that pair waits on the present value this thread's last read of it
/// signalled, inside its own submission. So neither thread ever waits on the other's
/// CPU progress through the GPU, and the only CPU rendezvous is the pool's handoff
/// lock (<see cref="OutputPairPool" />).</para>
///
/// <para><b>Threading.</b> Every readonly field is immutable after construction on the
/// render thread. Command buffers, their submission values and the last-present
/// timestamp are the present thread's alone. Counters are Interlocked, the failure is
/// published with Interlocked.CompareExchange, the interval rings lock internally, and
/// the test hooks are set before <see cref="Start" /> and immutable afterwards. The
/// render thread touches <see cref="_thread" /> only.</para>
/// </summary>
internal sealed unsafe class PresentThread : IDisposable
{
    /// <summary>How long a quiesce or a stop waits for the thread's own submissions to complete.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);

    private readonly VulkanContext _context;
    private readonly Swapchain _swapchain;
    private readonly OutputPairPool _pool;
    private readonly IFramePacer _pacer;
    private readonly PresentQueueBinding _queue;
    private readonly Semaphore _frameTimeline;
    private readonly OutOfBandPresentMarkers _markers;
    private readonly Action<string>? _log;
    private readonly PresentTimelineClock _clock;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer[] _commandBuffers;
    private readonly FrameIntervalRing _generatedIntervals = new(FrameIntervalRing.DefaultCapacity);
    private readonly FrameIntervalRing _realIntervals = new(FrameIntervalRing.DefaultCapacity);
    private readonly VulkanBuffer? _capture;

    // Present thread only.
    private readonly ulong[] _commandValues;
    private int _nextCommandBuffer;
    private long _lastPresentReturnUs;

    // Interlocked.
    private long _generatedPresents;
    private long _realPresents;
    private long _skippedPresents;
    private Exception? _failure;

    // Render thread only.
    private Thread? _thread;
    private bool _disposed;

    /// <param name="framesInFlight">
    /// The ring depth; sizes the command buffer ring at two presents per pair plus
    /// slack, so a buffer is only reused once the GPU is at most that far behind.
    /// </param>
    /// <param name="captureForTests">Copy one centre pixel of every presented source into a host buffer (tests).</param>
    public PresentThread(VulkanContext context, Swapchain swapchain, OutputPairPool pool, IFramePacer pacer,
        PresentQueueBinding queue, Semaphore frameTimeline, OutOfBandPresentMarkers markers, Action<string>? log,
        int framesInFlight, bool captureForTests = false)
    {
        _context = context;
        _swapchain = swapchain;
        _pool = pool;
        _pacer = pacer;
        _queue = queue;
        _frameTimeline = frameTimeline;
        _markers = markers;
        _log = log;

        _clock = new PresentTimelineClock(context, CreateTimeline(context));

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = context.GraphicsQueueFamily,
            // Buffers are reset one at a time, each once its own submission completed:
            // a pool reset would need every buffer idle at once, which a thread that
            // keeps presenting never guarantees.
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        VulkanResult.Check(context.Api.CreateCommandPool(context.Device, &poolInfo, null, out CommandPool commandPool),
            "vkCreateCommandPool for the present thread");
        _commandPool = commandPool;

        int count = Math.Max(2, framesInFlight) * PresentPressure.GeneratedPlusRealPerFrame + 2;
        _commandBuffers = new CommandBuffer[count];
        _commandValues = new ulong[count];
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)count,
        };
        fixed (CommandBuffer* buffers = _commandBuffers)
        {
            VulkanResult.Check(context.Api.AllocateCommandBuffers(context.Device, &allocateInfo, buffers),
                "vkAllocateCommandBuffers for the present thread");
        }

        if (captureForTests)
        {
            _capture = new VulkanBuffer(context, 4, BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        }
    }

    /// <summary>The present timeline; the render thread's pair writes wait on its values.</summary>
    public Semaphore PresentTimeline => _clock.Semaphore;

    /// <summary>The clock the swapchain is handed over to while this thread owns it.</summary>
    public ITimelineClock Clock => _clock;

    /// <summary>Why the thread died, or null while it is healthy. Any thread.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    public bool IsRunning => _thread != null && _thread.IsAlive;

    public long GeneratedPresents => Interlocked.Read(ref _generatedPresents);

    public long RealPresents => Interlocked.Read(ref _realPresents);

    /// <summary>Presents skipped because no image could be acquired (parked, out of date twice).</summary>
    public long SkippedPresents => Interlocked.Read(ref _skippedPresents);

    /// <summary>
    /// Present-to-present intervals, keyed by the kind of the later present: the
    /// number <c>pacing-gate.sh</c> judges. Kept here, privately, because the pacer
    /// stream's shared <c>PresentIntervalRing</c> has not landed in this branch; the
    /// wiring stage replaces these two with it.
    /// </summary>
    public FramePacingSnapshot PresentIntervals(PacedPresentKind kind) =>
        kind == PacedPresentKind.Generated ? _generatedIntervals.Snapshot() : _realIntervals.Snapshot();

    /// <summary>Sleeps this long before presenting each pair. Tests only; set before <see cref="Start" />.</summary>
    internal TimeSpan DelayPerPairForTests { get; set; }

    /// <summary>Called on the present thread after every present. Tests only; set before <see cref="Start" />.</summary>
    internal Action<PacedPresentRecord>? PresentedForTests { get; set; }

    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("the present thread was already started");
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Optimum present",
        };
        _thread.Start();
    }

    /// <summary>
    /// Stops the thread: it finishes the pair it is presenting (without the hold),
    /// drops every queued pair, waits for its own submissions and exits. Returns
    /// whether it joined within <paramref name="timeout" />; the caller must not destroy
    /// anything the thread uses when it did not.
    /// </summary>
    public bool StopAndJoin(TimeSpan timeout)
    {
        _pool.RequestStop();
        if (_thread == null) return true;
        bool joined = _thread.Join(timeout);
        if (!joined)
        {
            _log?.Invoke("paced present: the present thread did not stop within " +
                (int)timeout.TotalMilliseconds + " ms");
        }
        return joined;
    }

    /// <summary>
    /// After a join: waits for every present submission this thread made. A thread
    /// that died mid-pair skipped its own idle wait, and the swapchain hand-over back to
    /// the render thread requires it.
    /// </summary>
    public void WaitIdleAfterJoin()
    {
        if (_thread != null && _thread.IsAlive) throw new InvalidOperationException("the present thread is still running");
        WaitForPresentValue(_clock.FrameRecorded, IdleTimeout);
    }

    /// <summary>
    /// Blocks until the present timeline reached <paramref name="value" />. The
    /// swapchain calls it (owner thread) when every acquire semaphore is still waited on.
    /// </summary>
    public void WaitForPresentValue(ulong value) => WaitForPresentValue(value, IdleTimeout);

    private void Run()
    {
        try
        {
            while (true)
            {
                PresentWork work = _pool.WaitForWork(out OutputPair? pair);
                if (work == PresentWork.Stop)
                {
                    GoIdle("stop");
                    return;
                }
                if (work == PresentWork.Quiesce)
                {
                    GoIdle("quiesce");
                    _pool.AcknowledgeQuiesceAndWait();
                    continue;
                }
                PresentPair(pair!);
            }
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref _failure, error, null);
            _log?.Invoke("paced present: the present thread failed: " + error);
            // Nothing will consume pairs again; nobody may keep waiting for one.
            _pool.NotePresenterFailed();
        }
    }

    /// <summary>Drops the queue and waits for this thread's own submissions: nothing it submitted is outstanding afterwards.</summary>
    private void GoIdle(string why)
    {
        int dropped = _pool.DropQueued();
        if (dropped > 0) _log?.Invoke("paced present: " + why + " dropped " + dropped + " queued output pair(s)");
        WaitForPresentValue(_clock.FrameRecorded, IdleTimeout);
    }

    private void PresentPair(OutputPair pair)
    {
        if (DelayPerPairForTests > TimeSpan.Zero) Thread.Sleep(DelayPerPairForTests);

        ulong lastPresentValue = 0;
        bool generated = PresentOne(pair, PacedPresentKind.Generated, pair.Interpolated, pair.GeneratedPresentId,
            ref lastPresentValue, out long generatedReturnUs);
        if (generated)
        {
            // A stop or a quiesce counts as "the next pair is already due": the held
            // real frame goes out now instead of making the render thread wait out a hold.
            long target = _pacer.RealPresentTargetUs(generatedReturnUs, _pool.NextPairQueuedOrLeaving);
            LatencyHold.Until(target);
        }
        PresentOne(pair, PacedPresentKind.Real, pair.Real, pair.RealPresentId, ref lastPresentValue, out _);
        _pool.ReleasePresented(pair, lastPresentValue);
    }

    private bool PresentOne(OutputPair pair, PacedPresentKind kind, VulkanTexture? source, ulong presentId,
        ref ulong lastPresentValue, out long returnUs)
    {
        returnUs = 0;
        if (!_swapchain.TryAcquire(out PresentTarget target))
        {
            Interlocked.Increment(ref _skippedPresents);
            return false;
        }

        ulong value = _clock.Reserve();
        int slot = BeginCommandBuffer();
        CommandBuffer commandBuffer = _commandBuffers[slot];
        Record(commandBuffer, target, source);
        Submit(commandBuffer, target, pair.FrameValue, value);
        _commandValues[slot] = value;
        _swapchain.NotePresentSubmitted(target, value);
        lastPresentValue = value;

        _markers.Mark(presentId, pair.LatencyFrameId, LatencyMarker.OutOfBandPresentStart);
        _swapchain.Present(target, pair.LatencyFrameId, presentId);
        _markers.Mark(presentId, pair.LatencyFrameId, LatencyMarker.OutOfBandPresentEnd);

        returnUs = LatencyClock.NowUs();
        if (_lastPresentReturnUs != 0)
        {
            double intervalMs = (returnUs - _lastPresentReturnUs) / 1000.0;
            (kind == PacedPresentKind.Generated ? _generatedIntervals : _realIntervals).Add(intervalMs);
        }
        _lastPresentReturnUs = returnUs;
        _pacer.NotePresented(kind, returnUs);
        Interlocked.Increment(ref kind == PacedPresentKind.Generated ? ref _generatedPresents : ref _realPresents);

        Action<PacedPresentRecord>? presented = PresentedForTests;
        if (presented != null)
        {
            bool captured = false;
            uint pixel = 0;
            if (_capture != null && source != null)
            {
                // The copy rode this present's submission; read it once that completed.
                WaitForPresentValue(value, IdleTimeout);
                pixel = *(uint*)_capture.Mapped;
                captured = true;
            }
            presented(new PacedPresentRecord(kind, presentId, pair.LatencyFrameId, pair.Index, pair.FrameValue,
                value, returnUs, captured, pixel));
        }
        return true;
    }

    /// <summary>The next buffer of the ring, reset and begun; waits (on this thread's own timeline) until its last submission completed.</summary>
    private int BeginCommandBuffer()
    {
        int slot = _nextCommandBuffer;
        _nextCommandBuffer = (slot + 1) % _commandBuffers.Length;
        ulong previous = _commandValues[slot];
        if (previous != 0 && _clock.FrameCompleted < previous) WaitForPresentValue(previous, IdleTimeout);

        CommandBuffer commandBuffer = _commandBuffers[slot];
        VulkanResult.Check(_context.Api.ResetCommandBuffer(commandBuffer, 0),
            "vkResetCommandBuffer for the present thread");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanResult.Check(_context.Api.BeginCommandBuffer(commandBuffer, &begin),
            "vkBeginCommandBuffer for the present thread");
        return slot;
    }

    /// <summary>
    /// The present of one image. No <see cref="TextureManager" /> tracker is touched:
    /// those belong to the render thread. The source's layout is a contract instead -
    /// the render thread leaves both images of a pair in TRANSFER_SRC_OPTIMAL before it
    /// hands the pair off and this thread never transitions them, so the render thread's
    /// tracker stays true and nothing here needs a barrier on the source.
    /// </summary>
    private void Record(CommandBuffer commandBuffer, in PresentTarget target, VulkanTexture? source)
    {
        Vk api = _context.Api;
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

        // Every present leaves the swapchain image in PRESENT_SRC and nothing else
        // writes it, so UNDEFINED discards nothing that matters.
        var toDestination = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = target.Image,
            SubresourceRange = range,
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &toDestination);

        if (source != null)
        {
            BlitPresentPath.RecordFlippedBlit(api, commandBuffer, source.Image, source.Width, source.Height,
                target.Image, target.Extent);

            if (_capture != null)
            {
                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageOffset = new Offset3D((int)(source.Width / 2), (int)(source.Height / 2), 0),
                    ImageExtent = new Extent3D(1, 1, 1),
                };
                api.CmdCopyImageToBuffer(commandBuffer, source.Image, ImageLayout.TransferSrcOptimal,
                    _capture.Handle, 1, &region);
            }
        }

        var toPresent = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = 0,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.PresentSrcKhr,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = target.Image,
            SubresourceRange = range,
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit,
            0, 0, null, 0, null, 1, &toPresent);

        VulkanResult.Check(api.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer for the present thread");
    }

    /// <summary>
    /// Waits on the acquire semaphore (at TRANSFER, the image's first use) and on the
    /// Frame timeline at the pair's value (both images written); signals the image's
    /// present semaphore and this thread's own timeline.
    /// </summary>
    private void Submit(CommandBuffer commandBuffer, in PresentTarget target, ulong frameValue, ulong presentValue)
    {
        Semaphore* waits = stackalloc Semaphore[2];
        ulong* waitValues = stackalloc ulong[2];
        PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[2];
        uint waitCount = 0;
        waits[waitCount] = target.AcquireSemaphore;
        waitValues[waitCount] = 0;
        waitStages[waitCount] = PresentWaitStages.BlitAcquireWait;
        waitCount++;
        if (frameValue != 0)
        {
            waits[waitCount] = _frameTimeline;
            waitValues[waitCount] = frameValue;
            waitStages[waitCount] = PresentWaitStages.FrameWait;
            waitCount++;
        }

        Semaphore* signals = stackalloc Semaphore[2];
        ulong* signalValues = stackalloc ulong[2];
        signals[0] = target.PresentSemaphore;
        signalValues[0] = 0;
        signals[1] = _clock.Semaphore;
        signalValues[1] = presentValue;

        var timelineInfo = new TimelineSemaphoreSubmitInfo
        {
            SType = StructureType.TimelineSemaphoreSubmitInfo,
            WaitSemaphoreValueCount = waitCount,
            PWaitSemaphoreValues = waitValues,
            SignalSemaphoreValueCount = 2,
            PSignalSemaphoreValues = signalValues,
        };
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            PNext = &timelineInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            WaitSemaphoreCount = waitCount,
            PWaitSemaphores = waits,
            PWaitDstStageMask = waitStages,
            SignalSemaphoreCount = 2,
            PSignalSemaphores = signals,
        };

        lock (_queue.Lock)
        {
            VulkanResult.Check(_context.Api.QueueSubmit(_queue.Queue, 1, &submit, default(Fence)),
                "vkQueueSubmit for a paced present");
        }
    }

    private void WaitForPresentValue(ulong value, TimeSpan timeout)
    {
        if (value == 0) return;
        Semaphore handle = _clock.Semaphore;
        ulong target = value;
        var info = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &handle,
            PValues = &target,
        };
        Result result = _context.Api.WaitSemaphores(_context.Device, &info, (ulong)timeout.Ticks * 100UL);
        if (result == Result.Timeout)
        {
            _log?.Invoke("paced present: present timeline value " + value + " did not complete within " +
                (int)timeout.TotalMilliseconds + " ms");
            return;
        }
        VulkanResult.Check(result, "vkWaitSemaphores on the present timeline");
    }

    private static Semaphore CreateTimeline(VulkanContext context)
    {
        var type = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var info = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &type,
        };
        Semaphore semaphore;
        VulkanResult.Check(context.Api.CreateSemaphore(context.Device, &info, null, &semaphore),
            "vkCreateSemaphore for the present timeline");
        return semaphore;
    }

    /// <summary>
    /// Destroys the command pool, the present timeline and the capture buffer. The
    /// device retires this through the frame ring after the thread joined, so every
    /// render-thread submission that waited on the present timeline completed first.
    /// Refuses (and leaks) while the thread is still alive.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != null && _thread.IsAlive)
        {
            _log?.Invoke("paced present: not destroying a present thread that is still running");
            return;
        }
        _disposed = true;
        _context.Api.DestroyCommandPool(_context.Device, _commandPool, null);
        _context.Api.DestroySemaphore(_context.Device, _clock.Semaphore, null);
        _capture?.Dispose();
    }
}

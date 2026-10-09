using System;
using System.Collections.Generic;
using System.Threading;
using Silk.NET.Vulkan;

using Semaphore = Silk.NET.Vulkan.Semaphore;

// The Frame/ folder follows the plan's layout; the namespace stays Core until the
// renderer is reorganised, so every existing consumer keeps its one using.
namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// The four numbers a <see cref="RetireQueue" /> needs from the timelines. An
/// interface so the lifetime rules can be tested without a device.
/// </summary>
internal interface ITimelineClock
{
    /// <summary>
    /// The newest Frame value any command recorded or submitted so far carries:
    /// a resource released now can only be referenced by work at or below it.
    /// </summary>
    ulong FrameRecorded { get; }

    /// <summary>The same for the Transfer timeline.</summary>
    ulong TransferRecorded { get; }

    /// <summary>The Frame value the GPU has finished (the semaphore's counter).</summary>
    ulong FrameCompleted { get; }

    /// <summary>The Transfer value the GPU has finished.</summary>
    ulong TransferCompleted { get; }
}

/// <summary>
/// The renderer's clock: two timeline semaphores.
///
/// Every graphics submission of frame <c>n</c> signals <see cref="Frame" /> to
/// <c>n</c>; transfer work signals <see cref="Transfer" />. Pacing waits on the
/// Frame timeline, and deferred destruction compares recorded values against the
/// counters, so nothing in steady state needs a fence.
///
/// Values are reserved before recording (<see cref="ReserveFrame" />) and noted as
/// signalled once the submit that carries them was accepted
/// (<see cref="NoteFrameSubmitted" />). Waits are clamped to the signalled value:
/// waiting for a value no submission will ever signal would hang forever.
///
/// Presentation cannot wait on a timeline, so the per-image present semaphores
/// stay binary and live in the swapchain.
/// </summary>
internal sealed unsafe class FrameTimeline : ITimelineClock, IDisposable
{
    private readonly VulkanContext _context;
    private long _frameReserved;
    private long _frameSignalled;
    private long _transferReserved;
    private long _transferSignalled;
    private bool _disposed;

    public Semaphore Frame { get; }
    public Semaphore Transfer { get; }

    public FrameTimeline(VulkanContext context)
    {
        _context = context;
        Frame = CreateTimeline(context, "the Frame timeline semaphore");
        try { Transfer = CreateTimeline(context, "the Transfer timeline semaphore"); }
        catch
        {
            context.Api.DestroySemaphore(context.Device, Frame, null);
            throw;
        }
    }

    private static Semaphore CreateTimeline(VulkanContext context, string what)
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
            "vkCreateSemaphore for " + what);
        return semaphore;
    }

    // ------------------------------------------------------------------ Frame

    /// <summary>The value the next frame's submission will signal. Render thread.</summary>
    public ulong ReserveFrame() => (ulong)Interlocked.Increment(ref _frameReserved);

    /// <summary>The submission carrying <paramref name="value" /> was accepted by the queue.</summary>
    public void NoteFrameSubmitted(ulong value) => RaiseTo(ref _frameSignalled, value);

    public ulong FrameRecorded => (ulong)Interlocked.Read(ref _frameReserved);

    /// <summary>The newest Frame value handed to an accepted submission.</summary>
    public ulong FrameSignalled => (ulong)Interlocked.Read(ref _frameSignalled);

    public ulong FrameCompleted => Counter(Frame, "the Frame timeline");

    /// <summary>
    /// Blocks until the GPU finished Frame value <paramref name="value" /> (clamped
    /// to what was signalled) and counts one wait at <paramref name="site" />, even
    /// when the value has already passed: the count is the number of pacing points,
    /// which is what the stats gate compares.
    /// </summary>
    public void WaitForFrame(ulong value, WaitSite site) =>
        Wait(Frame, WaitTarget(value, FrameSignalled), site, "the Frame timeline");

    /// <summary>
    /// Teardown only: waits for every signalled frame before the caller destroys
    /// what those frames name (a caller unwinding past its own idle wait must not
    /// destroy objects a queued command buffer still uses), and never throws. A
    /// failed wait (a lost device, or the 5 s bound passing) is reported through
    /// <see cref="VulkanResult.OnFailure" /> and returns false; the caller still
    /// destroys what it owns, so a lost device does not leak the whole ring.
    /// </summary>
    public bool WaitForSignalledFramesAtTeardown() => WaitAtTeardown(Frame, FrameSignalled);

    /// <summary>The Transfer timeline's counterpart of <see cref="WaitForSignalledFramesAtTeardown" />.</summary>
    public bool WaitForSignalledTransfersAtTeardown() => WaitAtTeardown(Transfer, TransferSignalled);

    private bool WaitAtTeardown(Semaphore semaphore, ulong signalled)
    {
        Semaphore handle = semaphore;
        ulong target = signalled;
        var info = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &handle,
            PValues = &target,
        };
        long waitStart = VulkanStats.WaitStart();
        Result result = _context.Api.WaitSemaphores(_context.Device, &info, 5UL * 1000 * 1000 * 1000);
        VulkanStats.NoteWait(WaitSite.DeviceWaitIdle, waitStart);
        if (result == Result.Success) return true;

        // Check reports the failure (once, for a lost device) and throws; the
        // throw is what this method must not pass on.
        try { VulkanResult.Check(result, "draining a timeline before teardown"); }
        catch (Exception) { }
        return false;
    }

    // --------------------------------------------------------------- Transfer

    /// <summary>The value the next transfer submission will signal.</summary>
    public ulong ReserveTransfer() => (ulong)Interlocked.Increment(ref _transferReserved);

    public void NoteTransferSubmitted(ulong value) => RaiseTo(ref _transferSignalled, value);

    public ulong TransferRecorded => (ulong)Interlocked.Read(ref _transferReserved);

    public ulong TransferSignalled => (ulong)Interlocked.Read(ref _transferSignalled);

    public ulong TransferCompleted => Counter(Transfer, "the Transfer timeline");

    public void WaitForTransfer(ulong value, WaitSite site) =>
        Wait(Transfer, WaitTarget(value, TransferSignalled), site, "the Transfer timeline");

    // ------------------------------------------------------------------ rules

    /// <summary>Never wait past the newest signalled value; nothing would ever wake the wait.</summary>
    public static ulong WaitTarget(ulong requested, ulong signalled) => Math.Min(requested, signalled);

    // ---------------------------------------------------------------- helpers

    private ulong Counter(Semaphore semaphore, string what)
    {
        ulong value;
        VulkanResult.Check(_context.Api.GetSemaphoreCounterValue(_context.Device, semaphore, &value),
            "vkGetSemaphoreCounterValue on " + what);
        return value;
    }

    private void Wait(Semaphore semaphore, ulong value, WaitSite site, string what)
    {
        Semaphore handle = semaphore;
        ulong target = value;
        var info = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &handle,
            PValues = &target,
        };

        long waitStart = VulkanStats.WaitStart();
        Result result = _context.Api.WaitSemaphores(_context.Device, &info, ulong.MaxValue);
        VulkanStats.NoteWait(site, waitStart);
        VulkanResult.Check(result, "vkWaitSemaphores on " + what);
    }

    private static void RaiseTo(ref long field, ulong value)
    {
        long wanted = (long)Math.Min(value, long.MaxValue);
        long seen = Interlocked.Read(ref field);
        while (wanted > seen)
        {
            long previous = Interlocked.CompareExchange(ref field, wanted, seen);
            if (previous == seen) break;
            seen = previous;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Api.DestroySemaphore(_context.Device, Frame, null);
        _context.Api.DestroySemaphore(_context.Device, Transfer, null);
    }
}

/// <summary>
/// Resources waiting for the GPU to stop referencing them.
///
/// Each entry records the newest Frame and Transfer values that existed when it
/// was retired: any command that could name the resource carries one of those
/// values or an older one. The entry is destroyed at the first
/// <see cref="Collect" /> that sees both counters at or past its values, never
/// earlier, and ready entries are destroyed in the order they were retired.
///
/// Entries are appended under the lock with the clock's recorded values, which
/// only grow, so both values are non-decreasing along the list: the entries
/// that completed are always a prefix of it, removed in one step.
///
/// <see cref="Retire" /> is safe from any thread (the game's VAO and UBO
/// finalizers release from the finalizer thread). <see cref="Collect" /> runs on
/// the render thread at the start of a frame, and disposes outside the lock so a
/// resource whose Dispose retires something else cannot deadlock.
/// </summary>
internal sealed class RetireQueue
{
    private sealed record Entry(IDisposable Resource, ulong Frame, ulong Transfer);

    private readonly ITimelineClock _clock;
    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();
    private Exception? _lifetimeFailure;
    private int _count;

    public RetireQueue(ITimelineClock clock) => _clock = clock;

    /// <summary>Entries not yet destroyed.</summary>
    public int PendingCount => Volatile.Read(ref _count);

    /// <summary>Queues <paramref name="resource" /> against the timeline values recorded right now.</summary>
    public void Retire(IDisposable resource)
    {
        lock (_lock)
        {
            _entries.Add(new Entry(resource, _clock.FrameRecorded, _clock.TransferRecorded));
            Volatile.Write(ref _count, _entries.Count);
        }
    }

    /// <summary>
    /// Destroys every entry whose Frame and Transfer values have both completed,
    /// oldest first. Those are a prefix of the queue (see the class remarks), so
    /// the scan stops at the first entry that has not passed. Returns how many
    /// were destroyed.
    /// </summary>
    public int Collect()
    {
        RequireLifetime();
        if (PendingCount == 0) return 0;

        // Completion only moves forward, so counters read before taking the lock
        // are still true for every entry inside it.
        ulong frameCompleted = _clock.FrameCompleted;
        ulong transferCompleted = _clock.TransferCompleted;

        Entry[] ready;
        lock (_lock)
        {
            int count = 0;
            while (count < _entries.Count &&
                   _entries[count].Frame <= frameCompleted &&
                   _entries[count].Transfer <= transferCompleted)
            {
                count++;
            }
            if (count == 0) return 0;
            ready = new Entry[count];
            _entries.CopyTo(0, ready, 0, count);
        }

        DisposePrefix(ready);
        return ready.Length;
    }

    /// <summary>A failed release is terminal: retain its owner and never retry it.</summary>
    internal void RequireLifetime()
    {
        lock (_lock)
        {
            if (_lifetimeFailure != null)
                throw new InvalidOperationException("Deferred resource destruction failed; cleanup is terminal.", _lifetimeFailure);
        }
    }

    /// <summary>
    /// Destroys <paramref name="prefix" />, the oldest entries of the queue in
    /// order, outside the lock (a Dispose that retires something appends behind
    /// them), then removes the ones destroyed with a single RemoveRange. A failed
    /// Dispose is terminal (<see cref="RequireLifetime" />): the entries before it
    /// are still removed; it and the ones after it stay queued.
    /// </summary>
    private void DisposePrefix(Entry[] prefix)
    {
        int disposed = 0;
        try
        {
            foreach (Entry entry in prefix)
            {
                DisposeEntry(entry);
                disposed++;
            }
        }
        finally
        {
            if (disposed > 0)
            {
                lock (_lock)
                {
                    _entries.RemoveRange(0, disposed);
                    Volatile.Write(ref _count, _entries.Count);
                }
            }
        }
    }

    private void DisposeEntry(Entry entry)
    {
        RequireLifetime();
        try { entry.Resource.Dispose(); }
        catch (Exception error)
        {
            lock (_lock) { _lifetimeFailure ??= error; }
            throw;
        }
    }

    /// <summary>
    /// Destroys everything regardless of the timelines, oldest first. Teardown
    /// only, after the GPU has finished all submitted work. Repeats until the
    /// queue is empty, since a Dispose may retire further resources.
    /// </summary>
    public void DisposeAll()
    {
        while (true)
        {
            RequireLifetime();
            Entry[] all;
            lock (_lock)
            {
                if (_entries.Count == 0) return;
                all = _entries.ToArray();
            }
            DisposePrefix(all);
        }
    }
}

/// <summary>
/// Tells a resource created in the last few frames from a long-lived one, with
/// no per-resource storage.
///
/// Resource ids (<see cref="ResourceIds" />) only ever increase, so the highest id
/// issued when a frame began is a watermark: every id above the watermark of the
/// frame <c>N - 1</c> frames back was created within the last N frames. The class
/// keeps one watermark per frame in a ring.
///
/// The descriptor layer uses it to route sets naming short-lived resources (GUI
/// text, atlas tasks, fresh chunk meshes, overflow uniform copies) to the per-slot
/// arena that is reset every frame, instead of caching them in
/// <see cref="DescriptorCache" /> only to evict them moments later.
/// </summary>
internal sealed class ResourceAge
{
    public const int DefaultShortLivedFrames = 60;

    private readonly ulong[] _watermarks;
    private long _frames;
    private int _shortLivedFrames;

    public ResourceAge(int capacity = DefaultShortLivedFrames)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _watermarks = new ulong[capacity];
        _shortLivedFrames = capacity;
    }

    /// <summary>
    /// How many frames a resource counts as short-lived for, at most the ring's
    /// capacity. Zero makes every resource long-lived (the arena is never used).
    /// </summary>
    public int ShortLivedFrames
    {
        get => _shortLivedFrames;
        set
        {
            if (value < 0 || value > _watermarks.Length) throw new ArgumentOutOfRangeException(nameof(value));
            _shortLivedFrames = value;
        }
    }

    /// <summary>Frames noted so far.</summary>
    public long Frames => _frames;

    /// <summary>Records the watermark at the start of a frame: the highest resource id issued so far.</summary>
    public void NoteFrame(ulong highestIssuedId)
    {
        _watermarks[_frames % _watermarks.Length] = highestIssuedId;
        _frames++;
    }

    /// <summary>
    /// Whether <paramref name="resource" /> was created within the last
    /// <see cref="ShortLivedFrames" /> frames, the current one included. Id 0 (a
    /// permanent resource) never is. Before that many frames have been noted,
    /// every resource is: none can be older.
    /// </summary>
    public bool IsShortLived(ulong resource)
    {
        if (resource == 0 || _shortLivedFrames == 0) return false;
        if (_frames < _shortLivedFrames) return true;

        ulong watermark = _watermarks[(_frames - _shortLivedFrames) % _watermarks.Length];
        return resource > watermark;
    }

    /// <summary>Whether any resource a set names is short-lived.</summary>
    public bool NamesShortLived(DescriptorSetContents contents) =>
        NamesShortLived(contents.Samplers, contents.Buffers);

    /// <summary>Whether any resource a borrowed set key names is short-lived.</summary>
    public bool NamesShortLived(DescriptorSetKey key) => NamesShortLived(key.Samplers, key.Buffers);

    private bool NamesShortLived(ReadOnlySpan<SamplerBindingValue> samplers, ReadOnlySpan<BufferBindingValue> buffers)
    {
        foreach (SamplerBindingValue sampler in samplers)
        {
            if (IsShortLived(sampler.Resource)) return true;
        }
        foreach (BufferBindingValue buffer in buffers)
        {
            if (IsShortLived(buffer.Resource)) return true;
        }
        return false;
    }
}

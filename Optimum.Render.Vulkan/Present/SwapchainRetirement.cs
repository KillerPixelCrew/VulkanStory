using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

// The Present/ folder follows the plan's layout; the namespace stays Core until
// the renderer is reorganised, like Frame/.
namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Swapchain slots that were replaced but may still be referenced by work the GPU
/// has not finished.
///
/// A slot (its swapchain handle, images, views, acquire and present semaphores)
/// is retired as one unit, keyed on the Frame timeline value of the last present
/// submission that used one of its images. It is destroyed at the first
/// <see cref="Collect" /> that sees the Frame counter at or past that value: the
/// batch that blitted into its image and signalled its present semaphore has
/// completed, and vkQueuePresentKHR, which is synchronous on the CPU, returned
/// before the slot could be replaced. No vkDeviceWaitIdle is involved.
///
/// Owner thread only - the render thread, or the present thread while paced
/// presentation runs: slots are created, presented and retired by the swapchain's
/// owner, and ownership moves only through <see cref="Rebase" /> while neither thread
/// presents.
/// </summary>
internal sealed class SwapchainRetirement
{
    private readonly record struct Entry(IDisposable Slot, ulong LastPresentValue);

    private ITimelineClock _clock;
    private readonly List<Entry> _entries = new();

    public SwapchainRetirement(ITimelineClock clock) => _clock = clock;

    public int PendingCount => _entries.Count;

    /// <summary>Queues <paramref name="slot" /> until Frame value <paramref name="lastPresentValue" /> completed (0: never presented).</summary>
    public void Retire(IDisposable slot, ulong lastPresentValue) =>
        _entries.Add(new Entry(slot, lastPresentValue));

    /// <summary>Destroys, oldest first, every slot whose last present submission completed. Returns how many.</summary>
    public int Collect()
    {
        if (_entries.Count == 0) return 0;

        ulong completed = _clock.FrameCompleted;
        int destroyed = 0;
        int kept = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (entry.LastPresentValue <= completed)
            {
                entry.Slot.Dispose();
                destroyed++;
            }
            else
            {
                _entries[kept++] = entry;
            }
        }
        _entries.RemoveRange(kept, _entries.Count - kept);
        return destroyed;
    }

    /// <summary>
    /// Ownership moved to <paramref name="clock" /> after every submission of the old
    /// clock completed. Values of the old clock mean nothing on the new one, so every
    /// slot that was waiting is re-keyed onto <paramref name="firstNewValue" />, the
    /// first submission the new clock will carry - queued after every old present,
    /// which is the proof <see cref="SwapchainPolicy.RetireAfter" /> asks for. A slot that
    /// never presented (0) stays destroyable at once.
    /// </summary>
    public void Rebase(ITimelineClock clock, ulong firstNewValue)
    {
        _clock = clock;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (entry.LastPresentValue != 0) _entries[i] = entry with { LastPresentValue = firstNewValue };
        }
    }

    /// <summary>Teardown only, after the GPU finished every submission.</summary>
    public void DisposeAll()
    {
        foreach (Entry entry in _entries) entry.Slot.Dispose();
        _entries.Clear();
    }
}

/// <summary>What the present path does with the result of vkAcquireNextImageKHR.</summary>
internal enum AcquireAction
{
    /// <summary>The image is good; present it.</summary>
    Present,
    /// <summary>SUBOPTIMAL: the image is acquired and usable; rebuild before the next acquire.</summary>
    PresentThenRebuild,
    /// <summary>OUT_OF_DATE on the first attempt: rebuild now and acquire once more.</summary>
    RebuildAndRetry,
    /// <summary>OUT_OF_DATE again after the rebuild: skip presenting this frame, rebuild next frame.</summary>
    SkipFrame,
    /// <summary>Anything else (a lost device above all): reported, never a silent skip.</summary>
    Fail,
}

/// <summary>
/// The swapchain's decisions, as pure functions so they are tested without a
/// window (SwapchainRetirementTests).
/// </summary>
internal static class SwapchainPolicy
{
    /// <summary>
    /// SUBOPTIMAL rebuilds before the next acquire; OUT_OF_DATE rebuilds and
    /// re-acquires once; a second OUT_OF_DATE gives up on this frame.
    /// </summary>
    public static AcquireAction OnAcquire(Result result, int attempt)
    {
        if (result == Result.Success) return AcquireAction.Present;
        if (result == Result.SuboptimalKhr) return AcquireAction.PresentThenRebuild;
        if (result == Result.ErrorOutOfDateKhr) return attempt == 0 ? AcquireAction.RebuildAndRetry : AcquireAction.SkipFrame;
        return AcquireAction.Fail;
    }

    /// <summary>
    /// <c>max(caps.min + 1, mailbox ? 3 : 2)</c>, clamped to the surface maximum
    /// (0 means unbounded). Mailbox wants a third image so a finished frame can
    /// replace the queued one while another is on screen.
    ///
    /// Deliberately NOT grown for frame generation's second present, so that a
    /// run with the feature off allocates exactly the images main allocates. What
    /// a second acquire needs is not more images but the guarantee that it may be
    /// made at all, and that is <see cref="SimultaneousAcquireLimit" />: at
    /// <c>caps.min + 1</c> it is exactly 2, which is what the frame takes.
    /// </summary>
    public static uint ChooseImageCount(uint capabilitiesMin, uint capabilitiesMax, PresentModeKHR mode)
    {
        uint wanted = Math.Max(capabilitiesMin + 1, mode == PresentModeKHR.MailboxKhr ? 3u : 2u);
        if (capabilitiesMax > 0 && wanted > capabilitiesMax) wanted = capabilitiesMax;
        return wanted;
    }

    /// <summary>
    /// How many images may be held acquired at once without risking an acquire
    /// that never returns: <c>imageCount - caps.min + 1</c>, the WSI's own rule.
    ///
    /// Frame generation acquires both of a frame's images before it presents
    /// either, so a chain whose limit is below the presents of one frame must
    /// present fewer times that frame: past the limit vkAcquireNextImageKHR is
    /// allowed to block until an image is presented, and neither of this frame's
    /// images is presented until both have been acquired - a hang, not a slow
    /// frame. The usual <c>caps.min + 1</c> gives exactly 2; a surface whose
    /// maximum clamps the count back to <c>caps.min</c> gives 1, and there the
    /// generated present is simply never made.
    /// </summary>
    public static int SimultaneousAcquireLimit(uint imageCount, uint capabilitiesMin)
    {
        long limit = (long)imageCount - capabilitiesMin + 1;
        return limit < 1 ? 1 : (int)limit;
    }

    /// <summary>
    /// The Frame value a replaced slot retires on. Completing the present submission
    /// (Submit B, <paramref name="lastPresentValue" />) only proves the present semaphore
    /// was signalled; vkQueuePresentKHR on that image is queued after it and may still be
    /// pending in the WSI (VUID-vkDestroySwapchainKHR-swapchain-01282). The next Frame
    /// value is reserved only by the following frame, whose submission is queued after that
    /// vkQueuePresentKHR, so its completion is the first timeline proof the present was
    /// processed. Without present fences (VK_EXT_swapchain_maintenance) the Khronos
    /// swapchain_recreation sample likewise waits for a later operation. 0: never presented.
    /// </summary>
    public static ulong RetireAfter(ulong lastPresentValue) => lastPresentValue == 0 ? 0 : lastPresentValue + 1;

    /// <summary>A minimised window reports a zero extent; presentation parks until it grows again.</summary>
    public static bool IsParked(Extent2D extent) => extent.Width == 0 || extent.Height == 0;

    /// <summary>
    /// With vsync: FIFO, promoted to FIFO_RELAXED once sustained missed vsyncs
    /// were seen and the surface offers it (a late frame tears instead of waiting
    /// a whole interval). Without vsync: MAILBOX (drops frames, never tears), then
    /// IMMEDIATE, then FIFO, the only mode every driver must have.
    /// </summary>
    public static PresentModeKHR ChoosePresentMode(bool vsync, bool relaxedPromoted, IReadOnlyList<PresentModeKHR> supported) =>
        ChoosePresentMode(vsync, relaxedPromoted, supported, frameGeneration: false);

    /// <summary>
    /// The same, with the frame-generation rule (ROADMAP, "The paced present: the
    /// design"): never MAILBOX, because MAILBOX replaces the queued generated frame
    /// with the real one presented right after it and the generated frame is never
    /// seen; never FIFO_RELAXED, because a late generated frame would tear into its
    /// real one. So vsync on is FIFO, and vsync off is IMMEDIATE - the only mode where
    /// the pacer, not the display, owns the spacing - falling back to FIFO, which
    /// every driver has, when the surface offers no IMMEDIATE.
    /// </summary>
    public static PresentModeKHR ChoosePresentMode(bool vsync, bool relaxedPromoted,
        IReadOnlyList<PresentModeKHR> supported, bool frameGeneration)
    {
        if (frameGeneration)
        {
            if (vsync) return PresentModeKHR.FifoKhr;
            return Contains(supported, PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr : PresentModeKHR.FifoKhr;
        }
        if (vsync)
        {
            return relaxedPromoted && Contains(supported, PresentModeKHR.FifoRelaxedKhr)
                ? PresentModeKHR.FifoRelaxedKhr
                : PresentModeKHR.FifoKhr;
        }
        if (Contains(supported, PresentModeKHR.MailboxKhr)) return PresentModeKHR.MailboxKhr;
        if (Contains(supported, PresentModeKHR.ImmediateKhr)) return PresentModeKHR.ImmediateKhr;
        return PresentModeKHR.FifoKhr;
    }

    private static bool Contains(IReadOnlyList<PresentModeKHR> modes, PresentModeKHR mode)
    {
        for (int i = 0; i < modes.Count; i++)
        {
            if (modes[i] == mode) return true;
        }
        return false;
    }
}

/// <summary>
/// How much presentation pressure the acquire-semaphore free list has to survive:
/// how deep the frame ring is, and how many times one rendered frame presents.
///
/// Both are one number today (<see cref="FrameRing.FramesInFlight" /> and one
/// present per frame), but they enter the bound differently - the ring depth says
/// how many frames' presents can still be in flight, the presents per frame say
/// how many acquires each of those frames made - so they are carried separately
/// rather than multiplied at the call site.
/// </summary>
internal readonly record struct PresentPressure(int FramesInFlight, int PresentsPerFrame)
{
    /// <summary>One present per rendered frame: the path before frame generation.</summary>
    public const int SinglePresentPerFrame = 1;

    /// <summary>
    /// What DLSS frame generation will ask for: the generated frame and the
    /// retained real one, presented from the same rendered frame.
    /// </summary>
    public const int GeneratedPlusRealPerFrame = 2;

    public static PresentPressure ForFrames(int framesInFlight, int presentsPerFrame = SinglePresentPerFrame) =>
        new(Math.Max(1, framesInFlight), Math.Max(1, presentsPerFrame));

    /// <summary>
    /// The most acquire semaphores that can be held at once, derived below and
    /// used by <see cref="AcquireSemaphoreFreeList.CapacityFor" />.
    /// </summary>
    public int PeakHeldAcquireSemaphores => Math.Max(1, FramesInFlight) * Math.Max(1, PresentsPerFrame);
}

/// <summary>
/// A slot's acquire semaphores, taken for each vkAcquireNextImageKHR; how many
/// there are is <see cref="CapacityFor" />.
///
/// A semaphore whose acquire failed is untouched and returns at once
/// (<see cref="Return" />). One whose signal a present submission waits on stays
/// in use until that submission completed on the GPU: a binary semaphore with an
/// uncompleted wait must not be handed to another acquire
/// (VUID-vkAcquireNextImageKHR-semaphore-01779), so it is parked against the
/// submission's Frame value (<see cref="ReturnAfter" />) and reclaimed by a later
/// <see cref="Take" /> once the Frame counter passed it. A semaphore whose acquire
/// succeeded but was never submitted is never returned; it dies with the slot.
/// </summary>
internal sealed class AcquireSemaphoreFreeList
{
    private readonly Stack<ulong> _free = new();
    private readonly List<(ulong Handle, ulong FrameValue)> _pending = new();

    public AcquireSemaphoreFreeList(IReadOnlyList<ulong> handles)
    {
        for (int i = handles.Count - 1; i >= 0; i--) _free.Push(handles[i]);
        Capacity = handles.Count;
    }

    public int Capacity { get; }
    public int FreeCount => _free.Count;
    public int PendingCount => _pending.Count;

    /// <summary>The smallest submission value a parked semaphore waits for; 0 when none is parked.</summary>
    public ulong OldestPendingValue
    {
        get
        {
            ulong oldest = 0;
            foreach ((ulong _, ulong frameValue) in _pending)
            {
                if (oldest == 0 || frameValue < oldest) oldest = frameValue;
            }
            return oldest;
        }
    }

    /// <summary>
    /// Every submission a parked semaphore waited for has completed (the owner
    /// changed clocks after waiting for the old one): all of them are free again.
    /// </summary>
    public void ReleaseAllPending()
    {
        foreach ((ulong handle, ulong _) in _pending) _free.Push(handle);
        _pending.Clear();
    }

    /// <summary>
    /// How many semaphores a slot is created with.
    ///
    /// The derivation, redone for design step 4 (FramesInFlight 2 -> 3) because
    /// the old one - "the ring paces on frame n - FramesInFlight, so at most
    /// FramesInFlight - 1 present submissions are uncompleted when an acquire
    /// starts; imageCount + 1 covers that" - was stated in terms of one present
    /// per frame, which frame generation breaks.
    ///
    /// Write F for the ring depth and P for the presents of one rendered frame.
    /// A semaphore is held from <see cref="Take" /> until either the acquire
    /// failed (<see cref="Return" />, at once) or the present submission that
    /// waits on it completed on the GPU (<see cref="ReturnAfter" />, reclaimed by
    /// a later Take). So the question is how many present submissions can be
    /// uncompleted at the moment of a Take.
    ///
    /// <c>FrameRing.BeginFrame</c> waits for the newest value its slot signalled,
    /// and a frame's present submission is the last submission it makes into that
    /// slot. So when frame n begins, every submission of frame n - F, including
    /// all P of its presents, has completed. What can still be outstanding when
    /// frame n takes its p-th acquire semaphore (p counted from 0) is therefore
    /// the presents of frames n-F+1 .. n-1 - (F - 1) x P of them - plus the p
    /// presents frame n has already submitted. Take then removes one more, and
    /// the peak is at p = P - 1:
    ///
    ///     (F - 1) x P + (P - 1) + 1  =  F x P
    ///
    /// At F = 2, P = 1 that is 2, which is why a bound of imageCount + 1 never
    /// showed the assumption. At F = 3, P = 2 it is 6 - above imageCount + 1 on
    /// any ordinary swapchain (3 or 4 images), which is exactly the case the
    /// design said to re-derive rather than recompile.
    ///
    /// imageCount + 1 is kept as a floor rather than replaced. It is not part of
    /// the derivation; it is the slack the free list has always had for the one
    /// case the derivation does not cover - a semaphore whose acquire succeeded
    /// and whose frame never submitted a present (an abandoned frame, a teardown
    /// between acquire and submit), which is never returned and dies with the
    /// slot. Keeping the larger of the two means this change can only add
    /// semaphores, never take slack away. Each one is a binary semaphore: a
    /// handful of bytes, created once per swapchain.
    /// </summary>
    public static int CapacityFor(uint imageCount, PresentPressure pressure) =>
        Math.Max((int)imageCount, pressure.PeakHeldAcquireSemaphores) + 1;

    /// <summary>A free semaphore; parked ones whose submission completed (Frame counter <paramref name="frameCompleted" />) are reclaimed first when none is free.</summary>
    public ulong Take(ulong frameCompleted)
    {
        if (_free.Count == 0) Reclaim(frameCompleted);
        if (_free.Count == 0)
        {
            throw new InvalidOperationException(
                "every acquire semaphore of this swapchain is waited on by an uncompleted present submission " +
                "or was signalled by an acquire that was never presented");
        }
        return _free.Pop();
    }

    /// <summary>The acquire failed; the semaphore was never signalled.</summary>
    public void Return(ulong handle)
    {
        CheckRoom();
        _free.Push(handle);
    }

    /// <summary>A submission carrying Frame value <paramref name="frameValue" /> waits on the semaphore.</summary>
    public void ReturnAfter(ulong handle, ulong frameValue)
    {
        CheckRoom();
        _pending.Add((handle, frameValue));
    }

    private void CheckRoom()
    {
        if (_free.Count + _pending.Count >= Capacity) throw new InvalidOperationException("acquire semaphore returned twice");
    }

    private void Reclaim(ulong frameCompleted)
    {
        int kept = 0;
        for (int i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].FrameValue <= frameCompleted) _free.Push(_pending[i].Handle);
            else _pending[kept++] = _pending[i];
        }
        _pending.RemoveRange(kept, _pending.Count - kept);
    }
}

/// <summary>
/// Decides when FIFO should be promoted to FIFO_RELAXED: once enough frames in a
/// window missed their vsync. The refresh interval is estimated as the shortest
/// frame interval seen in the window (under FIFO nothing presents faster than the
/// display), and a frame counts as a miss when it took more than 1.5 of those.
/// A game that is consistently slower than the display never looks like it missed
/// anything, which is intended: relaxed FIFO only helps occasional late frames.
/// </summary>
internal sealed class MissedVsyncDetector
{
    public const int Window = 120;
    public const int MissesToPromote = 12;
    /// <summary>Intervals below this are not display refreshes (a burst after a stall).</summary>
    public const double ShortestPlausibleIntervalMs = 4.0;

    private readonly double[] _intervals = new double[Window];
    private int _count;
    private int _next;

    public void Reset()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>Adds one present-to-present interval; true once promotion is warranted (then resets).</summary>
    public bool NoteInterval(double milliseconds)
    {
        if (!(milliseconds > 0) || double.IsInfinity(milliseconds)) return false;

        _intervals[_next] = milliseconds;
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;
        if (_count < Window) return false;

        double period = double.MaxValue;
        for (int i = 0; i < Window; i++)
        {
            if (_intervals[i] >= ShortestPlausibleIntervalMs && _intervals[i] < period) period = _intervals[i];
        }
        if (period == double.MaxValue) return false;

        int misses = 0;
        for (int i = 0; i < Window; i++)
        {
            if (_intervals[i] > period * 1.5) misses++;
        }
        if (misses < MissesToPromote) return false;

        Reset();
        return true;
    }
}

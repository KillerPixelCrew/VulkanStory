using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Optimum.Render.Vulkan.Present;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

/// <summary>
/// The paced present, render-thread side (ROADMAP, "The paced present: the design").
///
/// With <see cref="PacedPresentEnabled" /> set, <see cref="Present" /> no longer
/// presents: it writes the frame into a free (interpolated, real) output pair inside
/// the frame's own command buffer, submits the frame, reserves the pair's two present
/// ids in render order and hands the pair to the <see cref="PresentThread" />, which
/// owns the swapchain from then on. With it clear, none of this exists - no thread, no
/// pool, no second queue use - and <see cref="Present" /> is the single synchronous
/// present it always was.
///
/// Threading: every field here is the render thread's. What crosses to the present
/// thread goes through <see cref="OutputPairPool" />'s handoff lock, the swapchain's
/// locked request record, or is immutable after <see cref="StartPacedPresent" />.
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>
    /// OPTIMUM_DLSSG_DOUBLE_PRESENT=1 runs the paced present with duplicate images - the
    /// frame copied into both halves of each pair - so the present thread and the pacer
    /// can be measured on any GPU. Off by default: a normal run presents once per frame.
    /// </summary>
    internal const string GeneratedPresentVariable = "OPTIMUM_DLSSG_DOUBLE_PRESENT";

    /// <summary>How long the render thread waits for a free output pair before it skips a frame's present.</summary>
    internal static readonly TimeSpan OutputPairWait = TimeSpan.FromSeconds(2);

    /// <summary>How long a quiesce or a stop waits for the present thread.</summary>
    internal static readonly TimeSpan PresentThreadJoinTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Paced presentation on. Set by the wiring stage when frame generation is
    /// effective, or by <see cref="GeneratedPresentVariable" />; read once per
    /// <see cref="Present" />, which starts or stops the present thread to match.
    /// </summary>
    internal bool PacedPresentEnabled { get; set; } =
        Environment.GetEnvironmentVariable(GeneratedPresentVariable)?.Trim() == "1";

    /// <summary>
    /// The pacer the next present thread uses. The stand-in presents every real frame
    /// immediately; the pacer stream's deterministic pacer replaces it at wiring. Read
    /// when the thread starts, immutable for that thread's life.
    /// </summary>
    internal IFramePacer FramePacer { get; set; } = new ImmediateFramePacer();

    /// <summary>
    /// Writes a frame into an output pair, on the render thread, into the frame's own
    /// command buffer: the DLSS-G evaluate at wiring. Null copies the default colour image
    /// into both halves (the double-present test switch). Whatever writes, the device
    /// moves both images to TRANSFER_SRC afterwards, which is the layout the present
    /// thread reads them in.
    /// </summary>
    internal PacedPairWriter? PacedPairWriter { get; set; }

    private PresentThread? _presentThread;
    private OutputPairPool? _pairPool;

    /// <summary>The present thread, while paced presentation runs. Tests only.</summary>
    internal PresentThread? PresentThreadForTests => _presentThread;

    /// <summary>The output pairs, while paced presentation runs. Tests only.</summary>
    internal OutputPairPool? OutputPairPoolForTests => _pairPool;

    /// <summary>Called with the present thread after it is built and before it starts. Tests only.</summary>
    internal Action<PresentThread>? ConfigurePresentThreadForTests { get; set; }

    /// <summary>Record every pair's state history. Tests only.</summary>
    internal bool TrackOutputPairHistoryForTests { get; set; }

    /// <summary>Copy one presented pixel of every present back to the CPU. Tests only.</summary>
    internal bool CapturePresentedPixelsForTests { get; set; }

    /// <summary>Stands in for the queue stream's own present queue until it is wired. Tests may replace it.</summary>
    internal Func<VulkanContext, PresentQueueBinding> PresentQueueForPacing { get; set; } =
        PresentQueueBinding.SharedGraphicsQueueStandIn;

    /// <summary>What the render thread handed to the present thread for one frame. Tests only.</summary>
    internal readonly record struct PacedHandoff(
        ulong GeneratedPresentId, ulong RealPresentId, ulong LatencyFrameId, int PairIndex, ulong FrameValue);

    /// <summary>The last handoff. Tests only.</summary>
    internal PacedHandoff LastPacedHandoffForTests { get; private set; }

    /// <summary>
    /// Stops paced presentation: the present thread finishes its current present, drops
    /// the queue, waits for its own submissions and joins; the swapchain goes back to the
    /// render thread's clock and queue; the output pairs and the thread's own Vulkan
    /// objects retire on the frame timeline. Idempotent.
    ///
    /// <para>The one method the wiring stage calls before <c>NgxLifetime.ShutDown</c>
    /// and before device teardown: both assume nobody else submits or presents, and a
    /// DLSS-G output still being blitted when NGX goes down is the use-after-free class
    /// <c>NgxLifetimeOwner</c> already paid for once. <see cref="Dispose" /> and
    /// <see cref="DrainDeferredDeletions" /> call it too, so neither can run under a live
    /// thread.</para>
    /// </summary>
    internal void ShutDownPacedPresent()
    {
        PresentThread? thread = _presentThread;
        if (thread == null) return;

        bool joined = thread.StopAndJoin(PresentThreadJoinTimeout);
        OutputPairPool? pool = _pairPool;
        _presentThread = null;
        _pairPool = null;

        if (!joined)
        {
            // It still owns the swapchain and may be inside the driver with it. Destroying
            // anything it uses would trade a stall for a crash, so the present path is
            // abandoned: the device stops presenting and leaks the thread's objects.
            AddDiagnostic("paced present: the present thread did not stop within " +
                (int)PresentThreadJoinTimeout.TotalSeconds + " s; presentation is abandoned");
            _swapchain = null;
            _presentPath = null;
            return;
        }

        thread.WaitIdleAfterJoin();
        _swapchain?.HandOver(_frames.Timeline, _context.GraphicsQueue, _context.QueueLock,
            waitForClockValue: null, frameGeneration: false);
        if (pool != null)
        {
            foreach (OutputPair pair in pool.Pairs()) RetireOutputPair(pair);
        }
        // Not now: a submission of the render thread's that waits on the present
        // timeline may still be queued, and destroying a semaphore a pending wait names
        // is invalid. The frame ring destroys it once every such frame completed.
        _frames.DeferDeletion(thread);
        _missedVsyncs.Reset();
        _lastPresentReturn = 0;
    }

    /// <summary>
    /// Before Submit A: takes a free pair (blocking while none is - the back-pressure),
    /// and records the frame into it. Null when the frame will not be handed off.
    /// </summary>
    private OutputPair? BeginPacedHandoff()
    {
        VulkanTexture? source = DefaultColorTexture();
        if (source == null || _swapchain == null) return null;
        if (!EnsurePresentThread(source)) return null;

        if (!_pairPool!.TryAcquireForRendering(OutputPairWait, out OutputPair? pair) || pair == null)
        {
            PresentThread? thread = _presentThread;
            if (thread?.Failure != null) StopFailedPresentThread(thread);
            return null;
        }

        CommandBuffer commandBuffer = Commands;
        // The write into the pair must not overtake the present thread's last read of it.
        // LastPresentValue was published under the handoff lock when the pair was
        // released, and the pair is the render thread's until it is queued again, so this
        // read is stable. It is registered immediately before the write, with no
        // submission in between, so the wait lands on the submission that carries it.
        _frames.WaitOnTimelineInNextSubmit(_presentThread!.PresentTimeline, pair.LastPresentValue);

        _targets.FlushAllPendingClears(commandBuffer);
        _targets.EndRendering(commandBuffer);
        PacedPairWriter? writer = PacedPairWriter;
        if (writer == null || !writer(commandBuffer, source, pair))
        {
            CopyIntoPair(commandBuffer, source, pair);
        }

        // The contract with the present thread: both images in TRANSFER_SRC, which it
        // reads them in and never changes.
        VulkanTexture? interpolated = pair.Interpolated;
        VulkanTexture? real = pair.Real;
        if (interpolated != null) _textures.Require(_barriers, commandBuffer, interpolated, ResourceUsage.TransferSrc);
        if (real != null) _textures.Require(_barriers, commandBuffer, real, ResourceUsage.TransferSrc);
        _barriers.Flush(commandBuffer);
        return pair;
    }

    /// <summary>After Submit A: reserves the pair's present ids in render order and hands it off.</summary>
    private void HandOffPacedPair(OutputPair? pair, ulong renderValue, long presentEntry, long frameSubmitted)
    {
        if (pair == null || _pairPool == null)
        {
            LastPresentTimingsForTests = new PresentTimings(presentEntry, frameSubmitted, frameSubmitted, 0,
                renderValue, 0, false, false);
            return;
        }

        // Seam S4, paced: PresentStart/PresentEnd bracket the handoff, which is the
        // render thread's last act for the frame; the present thread stamps the
        // out-of-band markers around the real vkQueuePresentKHR calls. Both ids are
        // reserved here, generated first, so present ids increase in present order - and
        // the real one closes the frame's latency report now, in render order, which is
        // what keeps a present-id prediction made at the frame's start true however late
        // the present thread gets to it.
        Latency.Marker(_latencyFrameId, LatencyMarker.PresentStart);
        ulong generatedPresentId = PresentIdCounter.Next();
        ulong realPresentId = PresentIdCounter.Next();
        _pairPool.MarkQueued(pair, renderValue, generatedPresentId, realPresentId, _latencyFrameId);
        Latency.Marker(_latencyFrameId, LatencyMarker.PresentEnd);
        Latency.OnPresent(_latencyFrameId, realPresentId);

        LastPresentIdForTests = realPresentId;
        LastPacedHandoffForTests = new PacedHandoff(generatedPresentId, realPresentId, _latencyFrameId, pair.Index,
            renderValue);
        LastPresentTimingsForTests = new PresentTimings(presentEntry, frameSubmitted, frameSubmitted,
            System.Diagnostics.Stopwatch.GetTimestamp(), renderValue, 0, false, true);
    }

    /// <summary>Starts the thread on first use, rebuilds the pairs when the display image changed size or format.</summary>
    private bool EnsurePresentThread(VulkanTexture source)
    {
        if (_presentThread != null && _presentThread.Failure != null)
        {
            StopFailedPresentThread(_presentThread);
            return false;
        }
        if (_presentThread == null)
        {
            StartPacedPresent(source);
            return _presentThread != null;
        }
        if (_pairPool!.Width != source.Width || _pairPool.Height != source.Height || _pairPool.Format != source.Format)
        {
            return RebuildOutputPairs(source);
        }
        return true;
    }

    private void StopFailedPresentThread(PresentThread thread)
    {
        AddDiagnostic("paced present: the present thread failed (" + thread.Failure!.Message +
            "); paced presentation is off");
        ShutDownPacedPresent();
        PacedPresentEnabled = false;
    }

    private void StartPacedPresent(VulkanTexture source)
    {
        // The hand-over re-keys the swapchain's parked acquire semaphores and retired
        // slots onto the present thread's clock, which is sound only once every present
        // submission the render thread made has completed. A one-off wait at the switch,
        // never per frame.
        _frames.Timeline.WaitForFrame(_frames.Timeline.FrameSignalled, WaitSite.DeviceWaitIdle);

        var pool = new OutputPairPool(_frames.FramesInFlight, MirrorValidationMessage)
        {
            TrackHistory = TrackOutputPairHistoryForTests,
        };
        uint width = source.Width;
        uint height = source.Height;
        Format format = source.Format;
        pool.Build(width, height, format, index => CreateOutputPairImages(width, height, format));

        PresentQueueBinding queue = PresentQueueForPacing(_context);
        var thread = new PresentThread(_context, _swapchain!, pool, FramePacer, queue, _frames.Timeline.Frame,
            new OutOfBandPresentMarkers(Latency), MirrorValidationMessage, _frames.FramesInFlight,
            CapturePresentedPixelsForTests);
        ConfigurePresentThreadForTests?.Invoke(thread);

        _swapchain!.Log ??= MirrorValidationMessage;
        _swapchain.HandOver(thread.Clock, queue.Queue, queue.Lock, thread.WaitForPresentValue, frameGeneration: true);
        // The extension marks the queue for good, so once, at the start, and only for a
        // queue of the present thread's own: the backend refuses the shared graphics queue.
        (Latency as NvLowLatency2Backend)?.NotifyOutOfBandPresent(queue.Queue);

        _pairPool = pool;
        _presentThread = thread;
        thread.Start();
    }

    /// <summary>
    /// The display image changed size or format: quiesce the present thread (it finishes
    /// its present, drops the queue, waits for its own timeline and acknowledges), retire
    /// the old pairs, build new ones, resume.
    /// </summary>
    private bool RebuildOutputPairs(VulkanTexture source)
    {
        if (!_pairPool!.Quiesce(PresentThreadJoinTimeout))
        {
            AddDiagnostic("paced present: the present thread did not quiesce for an output pair rebuild; restarting it");
            ShutDownPacedPresent();
            if (_swapchain == null) return false;
            StartPacedPresent(source);
            return _presentThread != null;
        }

        uint width = source.Width;
        uint height = source.Height;
        Format format = source.Format;
        _pairPool.Build(width, height, format, index => CreateOutputPairImages(width, height, format),
            RetireOutputPair);
        _pairPool.Resume();
        return true;
    }

    private OutputPairImages CreateOutputPairImages(uint width, uint height, Format format)
    {
        // Storage as well for the unorm display format, which is what the DLSS-G
        // evaluate writes its outputs as; a format without storage support gets none.
        bool storage = format == Format.R8G8B8A8Unorm;
        int interpolated = CreateUpscaleTexture((int)width, (int)height, format, storage);
        int real = CreateUpscaleTexture((int)width, (int)height, format, storage);
        return new OutputPairImages(interpolated, _textures.Get(interpolated), real, _textures.Get(real));
    }

    /// <summary>Retires a pair's images on the frame timeline; the present thread's reads of them already completed.</summary>
    private void RetireOutputPair(OutputPair pair)
    {
        if (pair.Images.InterpolatedTextureId != 0) ReleaseTexture(pair.Images.InterpolatedTextureId);
        if (pair.Images.RealTextureId != 0) ReleaseTexture(pair.Images.RealTextureId);
    }

    /// <summary>The double-present writer: the composed frame into both halves, unflipped, texel for texel.</summary>
    private void CopyIntoPair(CommandBuffer commandBuffer, VulkanTexture source, OutputPair pair)
    {
        VulkanTexture? interpolated = pair.Interpolated;
        VulkanTexture? real = pair.Real;
        if (interpolated == null || real == null) return;

        _textures.Require(_barriers, commandBuffer, source, ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commandBuffer, interpolated, ResourceUsage.TransferDst);
        _textures.Require(_barriers, commandBuffer, real, ResourceUsage.TransferDst);
        _barriers.Flush(commandBuffer);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
        blit.SrcOffsets.Element1 = new Offset3D((int)source.Width, (int)source.Height, 1);
        blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
        blit.DstOffsets.Element1 = new Offset3D((int)interpolated.Width, (int)interpolated.Height, 1);
        _context.Api.CmdBlitImage(commandBuffer, source.Image, ImageLayout.TransferSrcOptimal,
            interpolated.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);
        blit.DstOffsets.Element1 = new Offset3D((int)real.Width, (int)real.Height, 1);
        _context.Api.CmdBlitImage(commandBuffer, source.Image, ImageLayout.TransferSrcOptimal,
            real.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);
    }
}

/// <summary>
/// Writes one frame into an output pair on the render thread (the DLSS-G evaluate).
/// Returns false to fall back to the duplicate copy.
/// </summary>
internal delegate bool PacedPairWriter(CommandBuffer commandBuffer, VulkanTexture source, OutputPair pair);

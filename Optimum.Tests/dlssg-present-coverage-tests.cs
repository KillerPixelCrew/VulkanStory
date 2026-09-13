using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The frame-generation present path (docs/ROADMAP.md, "The paced present: the
/// design"): the per-call present source of step 1, and the present thread that
/// replaced step 0's synchronous double present.
///
/// These pin the seams a GPU test cannot see: that the switch is off by default and
/// off means the single synchronous present; that step 0's second acquire and its
/// two-image submission are gone; that the present thread stamps the out-of-band
/// markers and the render thread keeps PresentStart/End and reserves the ids at the
/// handoff; that the swapchain's rebuild request is a locked record; that frame
/// generation never presents MAILBOX; and that no teardown can run under a live
/// present thread.
/// </summary>
public class DlssgPresentCoverageTests
{
    private const string DevicePath = "Optimum.Render.Vulkan/VulkanDevice.cs";
    private const string PacedPath = "Optimum.Render.Vulkan/VulkanDevice.PacedPresent.cs";
    private const string DlssPath = "Optimum.Render.Vulkan/VulkanDevice.Dlss.cs";
    private const string PresentPathPath = "Optimum.Render.Vulkan/Present/IPresentPath.cs";
    private const string ThreadPath = "Optimum.Render.Vulkan/Present/PresentThread.cs";
    private const string PoolPath = "Optimum.Render.Vulkan/Present/OutputPairPool.cs";
    private const string RingPath = "Optimum.Render.Vulkan/Core/FrameRing.cs";
    private const string SwapchainPath = "Optimum.Render.Vulkan/Present/Swapchain.cs";
    private const string RetirementPath = "Optimum.Render.Vulkan/Present/SwapchainRetirement.cs";
    private const string NvBackendPath = "Optimum.Render.Vulkan/Latency/NvLowLatency2Backend.cs";

    [Fact]
    public void PacingIsOffByDefaultAndOffIsTheSingleSynchronousPresent()
    {
        string paced = Read(PacedPath);
        string device = Read(DevicePath);

        // An env override (or the wiring stage) turns it on, and nothing else.
        Assert.Contains("internal const string GeneratedPresentVariable = \"OPTIMUM_DLSSG_DOUBLE_PRESENT\";", paced);
        Assert.Contains("Environment.GetEnvironmentVariable(GeneratedPresentVariable)?.Trim() == \"1\";", paced);

        // Present decides once, stops a thread the switch no longer wants, and the paced
        // branch returns before the synchronous acquire.
        string present = Body(device, "    public void Present()");
        Assert.Contains("bool paced = PacedPresentEnabled && _swapchain != null && _presentPath != null;", present);
        Assert.Contains("if (!paced && _presentThread != null) ShutDownPacedPresent();", present);
        Assert.Contains("OutputPair? pacedPair = paced ? BeginPacedHandoff() : null;", present);
        int handoff = present.IndexOf("HandOffPacedPair(pacedPair, renderValue, presentEntry, frameSubmitted);", StringComparison.Ordinal);
        int acquire = present.IndexOf("_swapchain.TryAcquire(out PresentTarget target);", StringComparison.Ordinal);
        Assert.True(handoff > 0 && acquire > handoff, "the paced handoff must return before the synchronous acquire");

        // The pair is recorded into the frame before Submit A, and handed off after it.
        int begin = present.IndexOf("BeginPacedHandoff()", StringComparison.Ordinal);
        int submit = present.IndexOf("ulong renderValue = _frames.EndFrame();", StringComparison.Ordinal);
        Assert.True(begin > 0 && submit > begin && handoff > submit);
    }

    /// <summary>
    /// Step 0 is gone: no second acquire on the render thread, no two-image submission,
    /// no rebuild-forbidding acquire. The present thread presents one image before it
    /// acquires the next, so the window step 0 had to guard does not exist.
    /// </summary>
    [Fact]
    public void TheSynchronousDoublePresentIsGone()
    {
        string device = Read(DevicePath);
        string swapchain = Read(SwapchainPath);
        string ring = Read(RingPath);

        Assert.DoesNotContain("allowRebuild", device);
        Assert.DoesNotContain("allowRebuild", swapchain.Replace("<c>allowRebuild: false</c>", ""));
        Assert.DoesNotContain("BetweenPresentAcquiresForTests", device);
        Assert.DoesNotContain("PresentGeneratedFrame", device);
        Assert.DoesNotContain("secondWaitSemaphore", ring);
        Assert.DoesNotContain("secondSignalSemaphore", ring);
        Assert.Contains("public bool TryAcquire(out PresentTarget target)", swapchain);

        // One synchronous present submission, at the one acquire stage the rule allows.
        Assert.Single(Regex.Matches(device, Regex.Escape("_frames.BeginPresentCommands()")));
        Assert.Single(Regex.Matches(device, Regex.Escape("_frames.SubmitPresent(")));
        Assert.Contains("PresentWaitStages.RequireAcquireStage(acquireStage);", ring);
        Assert.DoesNotContain("PipelineStageFlags.AllCommandsBit", ring);
    }

    /// <summary>
    /// vkQueueNotifyOutOfBandNV marks the QUEUE and there is no call that marks it back,
    /// so marking the shared graphics queue would take every later in-band render
    /// submission out of Reflex's accounting. The backend refuses its own device's
    /// graphics queue; the device notifies once, for the present thread's queue.
    /// </summary>
    [Fact]
    public void TheOutOfBandNotificationRefusesTheSharedGraphicsQueue()
    {
        string backend = Read(NvBackendPath);
        string paced = Read(PacedPath);

        Assert.Contains("if (queue.Handle == _context.GraphicsQueue.Handle)", backend);
        Assert.Contains("OutOfBandNotifiesRefused++;", backend);
        Assert.Contains("OutOfBandNotifiesSent++;", backend);

        int refusal = backend.IndexOf("OutOfBandNotifiesRefused++;", StringComparison.Ordinal);
        int call = backend.IndexOf("_functions.QueueNotifyOutOfBand(queue, ref info);", StringComparison.Ordinal);
        Assert.True(refusal > 0 && call > refusal,
            "the graphics-queue refusal must come before vkQueueNotifyOutOfBandNV");

        Assert.Single(Regex.Matches(paced, Regex.Escape("NotifyOutOfBandPresent(queue.Queue);")));
    }

    /// <summary>
    /// The render thread keeps the frame's PresentStart/End around the handoff and
    /// reserves both present ids there, generated first; the present thread stamps the
    /// out-of-band markers around each real vkQueuePresentKHR and presents with the
    /// reserved ids - through a sink that never calls NV's per-frame Marker from the
    /// present thread.
    /// </summary>
    [Fact]
    public void TheRenderThreadReservesTheIdsAndThePresentThreadStampsTheOutOfBandMarkers()
    {
        string paced = Read(PacedPath);
        string thread = Read(ThreadPath);
        string device = Read(DevicePath);
        string backend = Read(NvBackendPath);

        string handoff = Body(paced, "    private void HandOffPacedPair(");
        int start = handoff.IndexOf("Latency.Marker(_latencyFrameId, LatencyMarker.PresentStart);", StringComparison.Ordinal);
        int generatedId = handoff.IndexOf("ulong generatedPresentId = PresentIdCounter.Next();", StringComparison.Ordinal);
        int realId = handoff.IndexOf("ulong realPresentId = PresentIdCounter.Next();", StringComparison.Ordinal);
        int queued = handoff.IndexOf("_pairPool.MarkQueued(pair, renderValue, generatedPresentId, realPresentId, _latencyFrameId);", StringComparison.Ordinal);
        int end = handoff.IndexOf("Latency.Marker(_latencyFrameId, LatencyMarker.PresentEnd);", StringComparison.Ordinal);
        int onPresent = handoff.IndexOf("Latency.OnPresent(_latencyFrameId, realPresentId);", StringComparison.Ordinal);
        Assert.True(start >= 0 && generatedId > start && realId > generatedId && queued > realId && end > queued &&
                    onPresent > end, "the handoff order is PresentStart, generated id, real id, handoff, PresentEnd, OnPresent");

        string presentOne = Body(thread, "    private bool PresentOne(");
        int oobStart = presentOne.IndexOf("_markers.Mark(presentId, pair.LatencyFrameId, LatencyMarker.OutOfBandPresentStart);", StringComparison.Ordinal);
        int queuePresent = presentOne.IndexOf("_swapchain.Present(target, pair.LatencyFrameId, presentId);", StringComparison.Ordinal);
        int oobEnd = presentOne.IndexOf("_markers.Mark(presentId, pair.LatencyFrameId, LatencyMarker.OutOfBandPresentEnd);", StringComparison.Ordinal);
        Assert.True(oobStart > 0 && queuePresent > oobStart && oobEnd > queuePresent);

        // Generated first, then the hold, then real.
        string pair = Body(thread, "    private void PresentPair(");
        int generated = pair.IndexOf("PacedPresentKind.Generated", StringComparison.Ordinal);
        int hold = pair.IndexOf("LatencyHold.Until(target);", StringComparison.Ordinal);
        int real = pair.IndexOf("PacedPresentKind.Real", StringComparison.Ordinal);
        int release = pair.IndexOf("_pool.ReleasePresented(pair, lastPresentValue);", StringComparison.Ordinal);
        Assert.True(generated > 0 && hold > generated && real > hold && release > real);

        // The device no longer stamps out-of-band markers itself.
        Assert.DoesNotContain("LatencyMarker.OutOfBandPresent", device);
        Assert.Contains("nv.OutOfBandPresentMarker(presentId, marker);", thread);
        Assert.Contains("PresentID = presentId,", Body(backend, "    public void OutOfBandPresentMarker("));
    }

    /// <summary>
    /// The lifetime rule without a second retire model: the write into a pair waits,
    /// inside its submission, on the present thread's last read of it; the present
    /// submission waits on the Frame value that wrote the pair.
    /// </summary>
    [Fact]
    public void PairWritesAndPresentReadsWaitOnEachOthersTimelinesInsideTheSubmit()
    {
        string paced = Read(PacedPath);
        string thread = Read(ThreadPath);
        string ring = Read(RingPath);

        string begin = Body(paced, "    private OutputPair? BeginPacedHandoff()");
        int wait = begin.IndexOf("_frames.WaitOnTimelineInNextSubmit(_presentThread!.PresentTimeline, pair.LastPresentValue);", StringComparison.Ordinal);
        int write = begin.IndexOf("CopyIntoPair(commandBuffer, source, pair);", StringComparison.Ordinal);
        Assert.True(wait > 0 && write > wait, "the present-timeline wait must be registered before the pair write");
        Assert.Contains("waits[waitCount] = _nextSubmitWait;", ring);

        string submit = Body(thread, "    private void Submit(");
        Assert.Contains("waits[waitCount] = _frameTimeline;", submit);
        Assert.Contains("waitValues[waitCount] = frameValue;", submit);
        Assert.Contains("signals[1] = _clock.Semaphore;", submit);
        Assert.Contains("lock (_queue.Lock)", submit);

        // The pool's states only change in one place, which throws on a wrong one.
        string pool = Read(PoolPath);
        Assert.Single(Regex.Matches(pool, Regex.Escape("pair.State = next;")));
    }

    [Fact]
    public void TheSwapchainTakesRebuildsFromALockedRecordAndPresentsOnItsOwnersQueue()
    {
        string swapchain = Read(SwapchainPath);

        Assert.Contains("public void RequestRebuild(uint width, uint height, bool vsync) => _requests.Post(width, height, vsync);", swapchain);
        Assert.Contains("TakePostedRequest();", Body(swapchain, "    public bool TryAcquire(out PresentTarget target)"));
        // No plain-field write of the request survives outside the owner's take.
        Assert.Single(Regex.Matches(swapchain, Regex.Escape("_width = request.Width;")));

        Assert.Contains("lock (_presentQueueLock)", swapchain);
        Assert.DoesNotContain("lock (_context.QueueLock)", swapchain);
        Assert.Contains("internal void HandOver(ITimelineClock clock, Queue presentQueue, object presentQueueLock,", swapchain);
    }

    [Fact]
    public void FrameGenerationNeverPresentsMailbox()
    {
        string policy = Read(RetirementPath);
        string swapchain = Read(SwapchainPath);

        string rule = Body(policy, "    public static PresentModeKHR ChoosePresentMode(bool vsync, bool relaxedPromoted,\n        IReadOnlyList<PresentModeKHR> supported, bool frameGeneration)");
        int frameGeneration = rule.IndexOf("if (frameGeneration)", StringComparison.Ordinal);
        int mailbox = rule.IndexOf("PresentModeKHR.MailboxKhr", StringComparison.Ordinal);
        Assert.True(frameGeneration > 0 && mailbox > frameGeneration,
            "the frame-generation branch must return before MAILBOX is considered");
        Assert.Contains("_vsync, _relaxedPromoted && _relaxedAllowed, supportedModes, _frameGeneration);", swapchain);
        Assert.Contains("|| _frameGeneration) return false;", Body(swapchain, "    public bool PromoteToRelaxedFifo()"));
    }

    /// <summary>
    /// NGX shutdown drains the frame ring, and device teardown waits for the device to go
    /// idle; both assume nobody else submits. So both stop the present thread first.
    /// </summary>
    [Fact]
    public void NoTeardownRunsUnderALivePresentThread()
    {
        string device = Read(DevicePath);
        string dlss = Read(DlssPath);
        string paced = Read(PacedPath);

        string dispose = Body(device, "    public void Dispose()");
        int stop = dispose.IndexOf("ShutDownPacedPresent();", StringComparison.Ordinal);
        int idle = dispose.IndexOf("VulkanStats.WaitDeviceIdle(", StringComparison.Ordinal);
        Assert.True(stop > 0 && idle > stop, "Dispose must stop the present thread before the idle wait");

        string drain = Body(dlss, "    internal int DrainDeferredDeletions()");
        int drainStop = drain.IndexOf("ShutDownPacedPresent();", StringComparison.Ordinal);
        int drained = drain.IndexOf("_frames.DrainRetirements();", StringComparison.Ordinal);
        Assert.True(drainStop > 0 && drained > drainStop, "the drain must stop the present thread first");

        // The thread's own Vulkan objects retire on the frame timeline, after the join
        // and the swapchain's hand-back, never under a submission that waits on them.
        string shutDown = Body(paced, "    internal void ShutDownPacedPresent()");
        int join = shutDown.IndexOf("thread.StopAndJoin(PresentThreadJoinTimeout);", StringComparison.Ordinal);
        int handBack = shutDown.IndexOf("_swapchain?.HandOver(_frames.Timeline, _context.GraphicsQueue, _context.QueueLock,", StringComparison.Ordinal);
        int retire = shutDown.IndexOf("_frames.DeferDeletion(thread);", StringComparison.Ordinal);
        Assert.True(join > 0 && handBack > join && retire > handBack);
    }

    [Fact]
    public void ThePresentSourceIsPerCallAndNoPathCapturesIt()
    {
        string present = Read(PresentPathPath);
        string device = Read(DevicePath);
        string thread = Read(ThreadPath);

        Assert.Contains("void Record(CommandBuffer commandBuffer, in PresentTarget target, VulkanTexture? source);", present);
        Assert.Contains("public void Record(CommandBuffer commandBuffer, in PresentTarget target, VulkanTexture? source)", present);

        // The constructor-captured delegate is gone.
        Assert.DoesNotContain("Func<VulkanTexture?>", present);
        Assert.Contains("public BlitPresentPath(VulkanContext context, TextureManager textures)", present);
        Assert.Contains("_presentPath = new BlitPresentPath(_context, _textures);", device);

        // The synchronous present carries the composed frame; the present thread flips
        // its pair images through the very same blit.
        Assert.Contains("VulkanTexture? presentSource = DefaultColorTexture();", device);
        Assert.Contains("_presentPath.Record(presentCommands, target, presentSource);", device);
        Assert.Contains("BlitPresentPath.RecordFlippedBlit(api, commandBuffer, source.Image, source.Width, source.Height,", thread);
        Assert.Single(Regex.Matches(present, Regex.Escape("// Source Y runs backwards: this is the flip.")));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath)).Replace("\r\n", "\n");

    /// <summary>The text from <paramref name="signature" /> to the end of its brace-balanced body.</summary>
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "member not found: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }
        return source.Substring(start);
    }
}

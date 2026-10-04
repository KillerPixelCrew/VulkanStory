using System;
using System.Threading;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

internal readonly record struct XessSourceImages(
    VulkanTexture Color, VulkanTexture Depth, VulkanTexture Motion,
    VulkanTexture Hudless, VulkanTexture Ui);

/// <summary>
/// Owns the Intel proxy and three sets of shared images. Vulkan copies one set
/// while DX12 may still be reading a different set. One D3D12 fence timeline
/// carries the release/acquire values for all sets.
/// </summary>
internal sealed unsafe class XessFgPresenter : IDisposable
{
    private readonly record struct SourceImageLayout(uint Width, uint Height, Format Format)
    {
        internal static SourceImageLayout Of(VulkanTexture image) => new(image.Width, image.Height, image.Format);
    }
    private readonly record struct SourceLayout(SourceImageLayout Color, SourceImageLayout Depth,
        SourceImageLayout Motion, SourceImageLayout Hudless, SourceImageLayout Ui)
    {
        internal static SourceLayout Of(in XessSourceImages images) => new(
            SourceImageLayout.Of(images.Color), SourceImageLayout.Of(images.Depth),
            SourceImageLayout.Of(images.Motion), SourceImageLayout.Of(images.Hudless), SourceImageLayout.Of(images.Ui));
    }
    private readonly SourceLayout _sourceLayout;
    private sealed class ImageSet : IDisposable
    {
        public required VulkanSharedImage Color { get; init; }
        public required VulkanSharedImage Depth { get; init; }
        public required VulkanSharedImage Motion { get; init; }
        public required VulkanSharedImage Hudless { get; init; }
        public required VulkanSharedImage Ui { get; init; }
        public ulong LastDx12Done;

        public void Dispose()
        {
            Ui.Dispose();
            Hudless.Dispose();
            Motion.Dispose();
            Depth.Dispose();
            Color.Dispose();
        }
    }

    private readonly VulkanContext _context;
    private readonly XessFgRuntime _runtime;
    private readonly Action<ulong, nint, LatencyMarker>? _pclPresentMarker;
    private readonly XessSharedFence _fence;
    private readonly ImageSet[] _images;
    private ulong _nextFenceValue = 1;
    private int _nextImageSet;
    private bool _disposed;
    private Exception? _releaseFailure;

    // The proxy's Present blocks while the SDK paces presentation. Called on the
    // render thread, it kept the next frame from being recorded, so the GPU ran
    // dry every frame (docs/performance-profile-2026-09-26.md). One present runs
    // here at a time; the render thread takes its outcome before handing over
    // the next, which bounds the added latency to one frame.
    private readonly Thread _presentThread;
    private readonly object _presentGate = new();
    private bool _presentPending;
    private bool _presentStopping;
    private PreparedFrame _queuedFrame;
    private XessPresentationFrame _queuedConstants;
    private ulong _queuedLatencyFrame;
    private nint _queuedPclToken;
    private PresentOutcome? _outcome;
    private ulong _lastQueuedDone;

    private XessFgPresenter(VulkanContext context, XessFgRuntime runtime,
        XessSharedFence fence, ImageSet[] images, uint width, uint height,
        Action<ulong, nint, LatencyMarker>? pclPresentMarker, in XessSourceImages sources,
        Exception? setupReleaseFailure = null)
    {
        _context = context;
        _runtime = runtime;
        _pclPresentMarker = pclPresentMarker;
        _fence = fence;
        _images = images;
        _sourceLayout = SourceLayout.Of(sources);
        Width = width;
        Height = height;
        if (setupReleaseFailure != null)
        {
            _releaseFailure = setupReleaseFailure;
            _presentThread = null!;
            return;
        }
        _presentThread = new Thread(PresentLoop)
        {
            IsBackground = true,
            Name = "XeSS-FG present",
            Priority = ThreadPriority.AboveNormal,
        };
        _presentThread.Start();
    }

    /// <summary>The result of one proxy present, taken by the render thread a frame later.</summary>
    public readonly record struct PresentOutcome(int Code, uint FramesPresented, int FrameGenResult,
        bool FrameGenEnabled, Exception? Error, long StartTicks, long EndTicks);

    /// <summary>
    /// Hands one prepared frame to the present thread. The previous present must
    /// have been taken with <see cref="TakePresentOutcome" />.
    /// </summary>
    public void QueuePresent(in PreparedFrame prepared, in XessPresentationFrame constants,
        ulong latencyFrameId, nint pclToken)
    {
        RequireLive();
        _lastQueuedDone = prepared.DoneByDx12;
        if (!PresentThreadEnabled)
        {
            // A/B and diagnostics: present on the render thread, as before the worker.
            PresentOutcome inline = RunPresent(prepared, constants, latencyFrameId, pclToken);
            lock (_presentGate) _outcome = inline;
            return;
        }
        lock (_presentGate)
        {
            while (_presentPending) Monitor.Wait(_presentGate);
            _queuedFrame = prepared;
            _queuedConstants = constants;
            _queuedLatencyFrame = latencyFrameId;
            _queuedPclToken = pclToken;
            _presentPending = true;
            Monitor.PulseAll(_presentGate);
        }
    }

    /// <summary>Waits for the in-flight present, if any, and returns its outcome once.</summary>
    public PresentOutcome? TakePresentOutcome()
    {
        lock (_presentGate)
        {
            while (_presentPending) Monitor.Wait(_presentGate);
            PresentOutcome? outcome = _outcome;
            _outcome = null;
            return outcome;
        }
    }

    /// <summary>Waits for the in-flight present without taking its outcome.</summary>
    public void WaitForPresentIdle()
    {
        RequireLive();
        lock (_presentGate)
        {
            while (_presentPending) Monitor.Wait(_presentGate);
        }
    }

    private void PresentLoop()
    {
        while (true)
        {
            PreparedFrame prepared;
            XessPresentationFrame constants;
            ulong latencyFrame;
            nint pclToken;
            lock (_presentGate)
            {
                while (!_presentPending && !_presentStopping) Monitor.Wait(_presentGate);
                if (!_presentPending) return;
                prepared = _queuedFrame;
                constants = _queuedConstants;
                latencyFrame = _queuedLatencyFrame;
                pclToken = _queuedPclToken;
            }

            PresentOutcome outcome = RunPresent(prepared, constants, latencyFrame, pclToken);

            lock (_presentGate)
            {
                _outcome = outcome;
                _presentPending = false;
                Monitor.PulseAll(_presentGate);
            }
        }
    }

    private static readonly bool PresentThreadEnabled =
        Environment.GetEnvironmentVariable("VULKANSTORY_XESS_PRESENT_THREAD") != "0";

    private PresentOutcome RunPresent(in PreparedFrame prepared, in XessPresentationFrame constants,
        ulong latencyFrame, nint pclToken)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            // XeLL's present markers bracket the real Present.
            _runtime.Marker(latencyFrame, LatencyMarker.PresentStart);
            if (pclToken != 0) _pclPresentMarker?.Invoke(latencyFrame, pclToken, LatencyMarker.PresentStart);
            int code;
            uint presented;
            int fgResult;
            bool fgEnabled;
            try { code = Present(prepared, constants, out presented, out fgResult, out fgEnabled); }
            finally
            {
                _runtime.Marker(latencyFrame, LatencyMarker.PresentEnd);
                if (pclToken != 0) _pclPresentMarker?.Invoke(latencyFrame, pclToken, LatencyMarker.PresentEnd);
            }
            return new PresentOutcome(code, presented, fgResult, fgEnabled, null, start,
                System.Diagnostics.Stopwatch.GetTimestamp());
        }
        catch (Exception error)
        {
            return new PresentOutcome(-1, 0, 0, false, error, start, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// The graphics queue may be gated on a DX12 "done" value (VulkanDevice's XeSS GPU
    /// gate), which is only ever the "done" value of a frame handed to
    /// <see cref="QueuePresent" />. The bridge signals every value it is handed; should
    /// one still be missing, WaitDeviceIdle would stall forever, so the fence is advanced
    /// to it from the host. Every "ready" value still pending behind the gate is higher,
    /// so the timeline stays monotonic.
    /// </summary>
    private unsafe void ReleaseGatedQueue()
    {
        ulong issued = _lastQueuedDone;
        if (issued == 0) return;
        Vk api = _context.Api;
        VulkanResult.Check(api.GetSemaphoreCounterValue(_context.Device, _fence.Semaphore, out ulong current),
            "reading XeSS-FG queue gate before release");
        if (current >= issued) return;
        var signal = new SemaphoreSignalInfo
        {
            SType = StructureType.SemaphoreSignalInfo,
            Semaphore = _fence.Semaphore,
            Value = issued,
        };
        VulkanResult.Check(api.SignalSemaphore(_context.Device, &signal),
            "releasing XeSS-FG queue gate");
    }

    private void StopPresentThread()
    {
        lock (_presentGate)
        {
            _presentStopping = true;
            Monitor.PulseAll(_presentGate);
        }
        _presentThread.Join();
    }

    public uint Width { get; }
    public uint Height { get; }
    internal bool MatchesSources(in XessSourceImages sources) => !_disposed && _releaseFailure == null &&
        _sourceLayout == SourceLayout.Of(sources);
    public Silk.NET.Vulkan.Semaphore SharedSemaphore { get { RequireLive(); return _fence.Semaphore; } }
    public XessFgRuntime Runtime { get { RequireLive(); return _runtime; } }

    public static bool TryCreate(VulkanContext context, nint window, uint width,
        uint height, bool vsync, in XessSourceImages sources,
        Action<ulong, nint, LatencyMarker>? pclPresentMarker,
        out XessFgPresenter? presenter, out string reason)
    {
        presenter = null;
        if (!XessFgRuntime.TryCreate(context, out XessFgRuntime? runtime, out reason))
            return false;
        XessSharedFence? fence = null;
        var sets = new ImageSet[3];
        try
        {
            if (!XessSharedFence.TryCreate(context, runtime!, out fence, out reason))
                return false;
            for (int i = 0; i < sets.Length; i++)
            {
                VulkanSharedImage? color = null, depth = null, motion = null, hudless = null, ui = null;
                try
                {
                    color = Create(context, runtime!, sources.Color, Format.B8G8R8A8Unorm);
                    depth = Create(context, runtime!, sources.Depth, Format.D32Sfloat);
                    motion = Create(context, runtime!, sources.Motion, Format.R16G16Sfloat);
                    hudless = Create(context, runtime!, sources.Hudless, Format.B8G8R8A8Unorm);
                    ui = Create(context, runtime!, sources.Ui, Format.B8G8R8A8Unorm);
                    sets[i] = new ImageSet
                    {
                        Color = color, Depth = depth, Motion = motion,
                        Hudless = hudless, Ui = ui,
                    };
                }
                catch
                {
                    ui?.Dispose(); hudless?.Dispose(); motion?.Dispose();
                    depth?.Dispose(); color?.Dispose();
                    throw;
                }
            }
            int code = runtime!.Start(window, width, height, vsync);
            if (code != 0) throw new InvalidOperationException("Intel proxy initialization failed (" + code + ")");
            code = runtime.SetGeneratedFrames(1, out _, out _);
            if (code < 0) throw new InvalidOperationException("Intel generated-frame count setup failed (" + code + ")");
            code = runtime.SetEnabled(true);
            if (code < 0) throw new InvalidOperationException("Intel proxy enable failed (" + code + ")");
            presenter = new XessFgPresenter(context, runtime, fence!, sets, width, height,
                pclPresentMarker, sources);
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            reason = "XeSS-FG presentation failed: " + error.Message;
            return false;
        }
        finally
        {
            if (presenter == null)
            {
                try
                {
                    runtime!.PrepareRelease();
                    for (int i = sets.Length - 1; i >= 0; i--) sets[i]?.Dispose();
                    fence?.Dispose();
                    runtime.Dispose();
                }
                catch (Exception cleanup)
                {
                    // Publish an inert failed owner through the out parameter;
                    // the device retains it and must reject fallback/teardown.
                    presenter = new XessFgPresenter(context, runtime!, fence!, sets,
                        width, height, pclPresentMarker, sources, cleanup);
                    throw new InvalidOperationException("XeSS-FG setup cleanup failed; remaining owners retained.", cleanup);
                }
            }
        }
    }

    private static VulkanSharedImage Create(VulkanContext context, XessFgRuntime runtime,
        VulkanTexture source, Format sharedFormat)
    {
        if (!VulkanSharedImage.TryCreate(context, runtime, source.Width, source.Height,
            sharedFormat, out VulkanSharedImage? image, out string reason))
            throw new InvalidOperationException(reason);
        if (!image!.CanBlitFrom(source, out reason))
        {
            image.Dispose();
            throw new InvalidOperationException(reason);
        }
        return image;
    }

    public readonly record struct PreparedFrame(int SetIndex, ulong WaitForDx12,
        ulong ReadyForDx12, ulong DoneByDx12);

    public PreparedFrame RecordCopies(CommandBuffer commands, TextureManager textures,
        BarrierBatcher barriers, in XessSourceImages sources)
    {
        RequireLive();
        ImageSet set = _images[_nextImageSet];
        if (!set.Color.CanBlitFrom(sources.Color, out string reason) ||
            !set.Depth.CanBlitFrom(sources.Depth, out reason) ||
            !set.Motion.CanBlitFrom(sources.Motion, out reason) ||
            !set.Hudless.CanBlitFrom(sources.Hudless, out reason) ||
            !set.Ui.CanBlitFrom(sources.Ui, out reason))
            throw new InvalidOperationException(reason);
        textures.Require(barriers, commands, sources.Color, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, sources.Depth, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, sources.Motion, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, sources.Hudless, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, sources.Ui, ResourceUsage.TransferSrc);
        barriers.Flush(commands);
        set.Color.RecordFlippedCopy(commands, sources.Color);
        set.Depth.RecordFlippedCopy(commands, sources.Depth);
        set.Motion.RecordFlippedCopy(commands, sources.Motion);
        set.Hudless.RecordFlippedCopy(commands, sources.Hudless);
        set.Ui.RecordFlippedCopy(commands, sources.Ui);
        ulong ready = _nextFenceValue++;
        ulong done = _nextFenceValue++;
        PreparedFrame frame = new(_nextImageSet, set.LastDx12Done, ready, done);
        _nextImageSet = (_nextImageSet + 1) % _images.Length;
        return frame;
    }

    public int Present(in PreparedFrame prepared, in XessPresentationFrame constants,
        out uint framesPresented, out int frameGenResult, out bool frameGenEnabled)
    {
        ImageSet set = _images[prepared.SetIndex];
        XessPresentationFrame frame = constants;
        frame.Color = set.Color.D3D12Resource;
        frame.Depth = set.Depth.D3D12Resource;
        frame.Motion = set.Motion.D3D12Resource;
        frame.Hudless = set.Hudless.D3D12Resource;
        frame.Ui = set.Ui.D3D12Resource;
        frame.ReadyFenceValue = prepared.ReadyForDx12;
        frame.DoneFenceValue = prepared.DoneByDx12;
        int code = _runtime.Present(frame, out framesPresented,
            out frameGenResult, out frameGenEnabled);
        if (code == 0) set.LastDx12Done = prepared.DoneByDx12;
        return code;
    }

    internal void RequireResourceLifetime()
    {
        if (_releaseFailure != null)
            throw new InvalidOperationException("XeSS-FG release failed; presenter cannot be reused.", _releaseFailure);
    }
    private void RequireLive()
    {
        RequireResourceLifetime();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    public void Dispose()
    {
        if (_releaseFailure != null)
            throw new InvalidOperationException("XeSS-FG release failed; cleanup is terminal and remaining owners are retained.", _releaseFailure);
        if (_disposed) return;
        try
        {
            // The in-flight present finishes before anything it uses goes away.
            StopPresentThread();
            ReleaseGatedQueue();
            VulkanResult.Check(_context.WaitDeviceIdle(), "draining Vulkan before XeSS-FG release");
            int disable = _runtime.SetEnabled(false);
            if (disable < 0)
                throw new InvalidOperationException("Disabling XeSS-FG failed (" + disable + "); inputs retained.");
            if (_runtime.WaitIdle() != 0)
                throw new InvalidOperationException("XeSS-FG DX12 queue did not become idle");
            _runtime.PrepareRelease();
            for (int i = _images.Length - 1; i >= 0; i--) _images[i].Dispose();
            _fence.Dispose();
            _runtime.Dispose();
            _disposed = true;
        }
        catch (Exception failure) { _releaseFailure = failure; throw; }
    }
}

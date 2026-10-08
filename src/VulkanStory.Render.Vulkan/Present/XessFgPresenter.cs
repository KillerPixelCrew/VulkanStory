using System;
using System.Threading;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Borrowed Vulkan scene, depth, motion, HUD-free color and UI sources for one XeSS-FG frame.</summary>
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
    /// <summary>Extent and format fingerprint used to detect incompatible source-image replacement.</summary>
    private readonly record struct SourceImageLayout(uint Width, uint Height, Format Format)
    {
        internal static SourceImageLayout Of(VulkanTexture image) => new(image.Width, image.Height, image.Format);
    }
    /// <summary>Source-image layout fingerprint that determines whether a presenter can be reused.</summary>
    private readonly record struct SourceLayout(SourceImageLayout Color, SourceImageLayout Depth,
        SourceImageLayout Motion, SourceImageLayout Hudless, SourceImageLayout Ui)
    {
        internal static SourceLayout Of(in XessSourceImages images) => new(
            SourceImageLayout.Of(images.Color), SourceImageLayout.Of(images.Depth),
            SourceImageLayout.Of(images.Motion), SourceImageLayout.Of(images.Hudless), SourceImageLayout.Of(images.Ui));
    }
    private readonly SourceLayout _sourceLayout;
    /// <summary>One independent set of shared XeSS inputs and its latest DX12 completion value.</summary>
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

    /// <summary>Joins CPU presentation and finishes both GPU queues before XeLL options change.</summary>
    /// <remarks>DX12 completes shared done signals before Vulkan waits on them; failures retain resource ownership.</remarks>
    public void WaitForGpuIdle()
    {
        WaitForPresentIdle();
        if (_runtime.WaitIdle() != 0)
            throw new InvalidOperationException("XeSS-FG DX12 queue did not become idle before changing latency options.");
        VulkanResult.Check(_context.WaitDeviceIdle(), "draining Vulkan before XeLL options change");
    }

    /// <summary>Services the bounded asynchronous present slot on the dedicated presenter thread.</summary>
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

    /// <summary>Executes native presentation and optional PCL boundaries without synthesizing GPU fence completion.</summary>
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

    /// <summary>Requests presenter-thread shutdown and waits up to five seconds for it to leave native work.</summary>
    /// <exception cref="InvalidOperationException">The presentation thread did not stop within the bounded join.</exception>
    private void StopPresentThread()
    {
        lock (_presentGate)
        {
            _presentStopping = true;
            Monitor.PulseAll(_presentGate);
        }
        _presentThread.Join();
    }

    /// <summary>Presenter display width in pixels.</summary>
    public uint Width { get; }
    /// <summary>Presenter display height in pixels.</summary>
    public uint Height { get; }
    /// <summary>Reports whether live presenter resources match every source image extent and format.</summary>
    internal bool MatchesSources(in XessSourceImages sources) => !_disposed && _releaseFailure == null &&
        _sourceLayout == SourceLayout.Of(sources);
    /// <summary>Shared timeline semaphore used by Vulkan submissions to coordinate DX12 image reuse.</summary>
    public Silk.NET.Vulkan.Semaphore SharedSemaphore { get { RequireLive(); return _fence.Semaphore; } }
    /// <summary>Borrowed native presenter runtime owned by this presenter.</summary>
    public XessFgRuntime Runtime { get { RequireLive(); return _runtime; } }

    /// <summary>Creates the XeSS presenter, imported fence and three shared input sets for a Win32 window.</summary>
    /// <remarks>Partial setup is released on failure; a failed cleanup can return a retained owner to keep remaining resources reachable.</remarks>
    /// <returns>Whether complete presenter setup succeeded.</returns>
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

    /// <summary>Shared image-set index and fence values reserved for one prepared XeSS presentation.</summary>
    public readonly record struct PreparedFrame(int SetIndex, ulong WaitForDx12,
        ulong ReadyForDx12, ulong DoneByDx12);

    /// <summary>Rotates the shared image set, transitions sources and records vertically flipped copies for DX12.</summary>
    /// <remarks>The submitting owner must wait for WaitForDx12 and signal ReadyForDx12; the recorded commands alone do not transfer completed work.</remarks>
    /// <returns>The image-set index and fence values for the corresponding presentation.</returns>
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

    /// <summary>Synchronously presents a prepared frame through the native bridge and returns its reported outcome.</summary>
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

    /// <summary>Rejects further resource release after a retained presenter cleanup failure.</summary>
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
            // DX12 owns the shared done signals. Finish its queued work before
            // waiting for Vulkan submissions gated on those values.
            if (_runtime.WaitIdle() != 0)
                throw new InvalidOperationException("XeSS-FG DX12 queue did not become idle; inputs retained.");
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

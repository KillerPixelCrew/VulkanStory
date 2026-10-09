using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.Present;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

/// <summary>Frame-generation tagging, presentation selection and resource-retirement portion of the renderer.</summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>Whether device support, bound Streamline functions and an active swapchain are all present.</summary>
    internal bool StreamlineFrameGenerationAvailable => _streamlineReflexReady &&
        _streamlineFrameGenerationReady && _streamlineFrameGenerationSupported &&
        _context.Streamline != null && _swapchain != null;

    /// <summary>Whether the available Streamline path can use the current nonparked, unrecreated swapchain.</summary>
    internal bool StreamlineFrameGenerationReady => StreamlineFrameGenerationAvailable &&
        !_swapchain!.NeedsRecreation && !_swapchain.Parked && !_swapchain.Fsr3ProxyActive;

    /// <summary>Whether the active swapchain is owned by the FidelityFX presentation proxy.</summary>
    internal bool Fsr3ProxyReady => _swapchain is { Fsr3ProxyActive: true };
    /// <summary>Whether FidelityFX must use the direct path because distinct proxy queues are unavailable.</summary>
    internal bool Fsr3DirectPresentation => !_context.Fsr3SwapchainQueuesAvailable;
    /// <summary>Borrowed active FidelityFX swapchain context, or zero.</summary>
    internal nint Fsr3ProxyContext => _swapchain?.Fsr3ProxyContext ?? 0;
    /// <summary>Output format selected for direct or proxy FidelityFX frame generation.</summary>
    internal Format Fsr3ProxyFormat => Fsr3DirectPresentation ? Format.R8G8B8A8Unorm :
        _swapchain?.Format ?? Format.Undefined;
    /// <summary>Latest active FidelityFX swapchain-proxy failure.</summary>
    internal string? Fsr3ProxyFailure => _swapchain?.Fsr3ProxyFailure;

    /// <summary>Flips matching scene resources into presentation orientation and tags them with camera/reset constants.</summary>
    /// <returns>Native bridge result, or a negative local readiness/resource/conversion failure code.</returns>
    internal int TagStreamlineFrame(int depthId, int motionId, int hudlessId, int uiId,
        int uprightDepthId, int uprightMotionId, int uprightHudlessId, int uprightUiId,
        in NgxFrameGenerationCamera camera, bool reset)
    {
        using GpuSection gpuSection = BeginGpuSection("fg_dlss_tag_flips");
        if (!StreamlineFrameGenerationReady || !_frameActive) return -1;
        VulkanTexture? depth = _textures.Get(depthId);
        VulkanTexture? motion = _textures.Get(motionId);
        VulkanTexture? hudless = _textures.Get(hudlessId);
        VulkanTexture? ui = _textures.Get(uiId);
        VulkanTexture? uprightDepth = _textures.Get(uprightDepthId);
        VulkanTexture? uprightMotion = _textures.Get(uprightMotionId);
        VulkanTexture? uprightHudless = _textures.Get(uprightHudlessId);
        VulkanTexture? uprightUi = _textures.Get(uprightUiId);
        if (depth == null || motion == null || hudless == null || ui == null ||
            uprightDepth == null || uprightMotion == null || uprightHudless == null ||
            uprightUi == null) return -2;
        CommandBuffer commands = Commands;
        _targets.FlushAllPendingClears(commands);
        _targets.EndRendering(commands);
        // Present flips the engine's offscreen image. Streamline tags must be in
        // the same upright space as the intercepted swapchain image.
        if (!FlipFrameGenerationInput(commands, depth, uprightDepth) ||
            !FlipFrameGenerationInput(commands, motion, uprightMotion) ||
            !FlipFrameGenerationInput(commands, hudless, uprightHudless) ||
            !FlipFrameGenerationInput(commands, ui, uprightUi)) return -3;
        _textures.Require(_barriers, commands, uprightDepth, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, uprightMotion, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, uprightHudless, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, uprightUi, ResourceUsage.SampleExternal);
        _barriers.Flush(commands);

        StreamlineTaggedImage d = StreamlineTaggedImage.From(uprightDepth);
        StreamlineTaggedImage m = StreamlineTaggedImage.From(uprightMotion);
        StreamlineTaggedImage h = StreamlineTaggedImage.From(uprightHudless);
        StreamlineTaggedImage u = StreamlineTaggedImage.From(uprightUi);
        fixed (float* viewToClip = camera.ViewToClip)
        fixed (float* clipToView = camera.ClipToView)
        fixed (float* clipToPrevious = camera.ClipToPreviousClip)
        fixed (float* previousToClip = camera.PreviousClipToClip)
        {
            var frame = new StreamlineFrameCamera
            {
                ViewToClip = viewToClip, ClipToView = clipToView,
                ClipToPrevClip = clipToPrevious, PrevClipToClip = previousToClip,
                Near = camera.Near, Far = camera.Far, Fov = camera.FovRadians,
                Aspect = camera.AspectRatio, JitterX = camera.JitterX, JitterY = -camera.JitterY,
                MotionScaleX = 1f / motion.Width, MotionScaleY = -1f / motion.Height,
                Reset = reset ? 1u : 0u,
            };
            frame.Position[0] = camera.PositionX; frame.Position[1] = camera.PositionY; frame.Position[2] = camera.PositionZ;
            frame.Up[0] = camera.UpX; frame.Up[1] = camera.UpY; frame.Up[2] = camera.UpZ;
            frame.Right[0] = camera.RightX; frame.Right[1] = camera.RightY; frame.Right[2] = camera.RightZ;
            frame.Forward[0] = camera.ForwardX; frame.Forward[1] = camera.ForwardY; frame.Forward[2] = camera.ForwardZ;
            // The native call can replace earlier tags even when it fails part-way.
            _streamlineTagsClearedFrame = 0;
            return _context.Streamline!.TagFrame(commands, &d, &m, &h, &u, &frame);
        }
    }

    /// <summary>Applies DLSS-G enablement and interpolation count using the active swapchain dimensions and format.</summary>
    internal int SetStreamlineFrameGeneration(bool enabled, uint generatedFrames = 1) =>
        _context.Streamline == null || _swapchain == null ? -1 :
        _context.Streamline.SetFrameGeneration(enabled, generatedFrames, _swapchain.Extent.Width,
            _swapchain.Extent.Height, _swapchain.Format, _swapchain.ImageCount);

    /// <summary>Consumes a recorded Streamline presentation error, or returns zero without a runtime.</summary>
    /// <remarks>
    /// OUT_OF_DATE and SUBOPTIMAL are not reported here: the Streamline swapchain dispatch
    /// turns them into its present result, so the swapchain rebuilds them on any frame.
    /// </remarks>
    internal int TakeStreamlinePresentError() => _context.Streamline?.TakePresentError() ?? 0;
    /// <summary>Vertical-synchronization state requested by the renderer host.</summary>
    internal bool PresentationVsyncEnabled => _vsync;
    /// <summary>Queries the full DLSS-G capability/state snapshot, defaulting it when no runtime exists.</summary>
    internal int GetStreamlineFrameGenerationStateDetails(out StreamlineFrameGenerationState state)
    {
        state = default;
        return _context.Streamline?.GetFrameGenerationStateDetails(out state) ?? -1;
    }

    /// <summary>Invalidates current frame tags and disables DLSS-G before the consuming resources change.</summary>
    /// <remarks>Tags the current frame token already cleared, and has not replaced since, are not cleared again.</remarks>
    internal void SuspendStreamlineFrameGeneration()
    {
        if (_context.Streamline == null) return;
        if (!StreamlineTagsClearedThisFrame)
        {
            int tags = _context.Streamline.InvalidateFrameTags();
            if (tags != 0) throw new InvalidOperationException("Invalidating DLSS-G frame tags failed (" + tags + ").");
            if (_streamlineFrameTokenReady) _streamlineTagsClearedFrame = _streamlineTokenFrameId;
        }
        if (_swapchain != null)
        {
            int options = SetStreamlineFrameGeneration(false);
            CheckStreamlineDisable(options);
        }
    }

    private void CheckStreamlineDisable(int result)
    {
        StreamlineRuntime.CheckDisableResult(_context, result);
    }

    /// <summary>Disables, drains and frees DLSS-G resources before NGX/device teardown.</summary>
    /// <exception cref="InvalidOperationException">The checked disable, GPU drain or resource release fails.</exception>
    internal void ReleaseStreamlineFrameGenerationResources()
    {
        RequireFrameRelease();
        if (_context == null || _context.Streamline == null || !_streamlineFrameGenerationReady) return;
        int options = _context.Streamline.DisableFrameGenerationForRelease();
        CheckStreamlineDisable(options);
        var wait = _context.WaitDeviceIdle();
        if (wait != Result.Success) throw new InvalidOperationException("Draining Streamline presentation failed: " + wait);
        int result = _context.Streamline.FreeFrameGenerationResources();
        if (result != 0) throw new InvalidOperationException("Releasing DLSS-G resources before NGX shutdown failed (" + result + ").");
    }

    /// <summary>Latency frame whose Streamline token has cleared scene tags and not tagged since; zero when none.</summary>
    private ulong _streamlineTagsClearedFrame;

    private bool StreamlineTagsClearedThisFrame => _streamlineFrameTokenReady &&
        _streamlineTokenFrameId != 0 && _streamlineTagsClearedFrame == _streamlineTokenFrameId;

    private void BeginStreamlineFrameTags()
    {
        if (!_streamlineFrameGenerationReady || !_streamlineFrameTokenReady ||
            _context.Streamline == null || _swapchain == null) return;
        // Default every new token to a non-scene frame before any game draws.
        // Generate may supply fresh scene inputs later. The SDK resolves the
        // full backbuffer size after any deferred recreation during acquire.
        int result = _context.Streamline.InvalidateFrameTags();
        RequireLatencyProtocol("Streamline", result, "current-frame tag initialization");
        _streamlineTagsClearedFrame = _streamlineTokenFrameId;
    }

    private int _generatedFrameForPresent;
    private readonly GeneratedFramePacer _generatedFramePacer = new();
    private ulong _generatedPresentId;
    private ulong _realPresentId;
    private bool _generatedPresentLogged;
    private int _fsr3CountedSwapchainCreation;
    private ulong _fsr3CountedPresents;
    private ulong _fsr3CountedRenderedFrames;
    private bool _fsr3GeneratedPresentLogged;

    private void CountFsr3SdkPresents()
    {
        if (_swapchain is not { Fsr3ProxyActive: true }) return;
        if (_fsr3CountedSwapchainCreation != _swapchain.Creations)
        {
            _fsr3CountedSwapchainCreation = _swapchain.Creations;
            _fsr3CountedPresents = 0;
            _fsr3CountedRenderedFrames = 0;
            _fsr3GeneratedPresentLogged = false;
        }
        _fsr3CountedRenderedFrames++;
        ulong count = _swapchain.Fsr3ProxyPresentCount;
        if (count < _fsr3CountedPresents) _fsr3CountedPresents = 0;
        ulong added = count - _fsr3CountedPresents;
        _fsr3CountedPresents = count;
        VulkanStats.NoteSdkActualPresents((uint)System.Math.Min(added, uint.MaxValue));
        if (!_fsr3GeneratedPresentLogged && count > _fsr3CountedRenderedFrames)
        {
            _fsr3GeneratedPresentLogged = true;
            RenderLogger?.Notification(
                "VulkanStory: FidelityFX SDK reports {0} frames actually presented over {1} rendered frames",
                count, _fsr3CountedRenderedFrames);
        }
    }

    /// <summary>Whether the current active frame and swapchain permit the direct generated-present path.</summary>
    internal bool CanPresentGeneratedFrame => _frameActive && _swapchain != null &&
        !_swapchain.NeedsRecreation && !_swapchain.Parked &&
        _swapchain.PresentMode != PresentModeKHR.MailboxKhr;

    /// <summary>Changes presentation-provider ownership and disables competing vendor pacing for XeSS.</summary>
    internal void SetFrameGenerationPresentation(string provider)
    {
        if (provider != XessProvider) StopXessPresenter();
        _frameGenerationProvider = provider;
        _xessFailure = null;
        if (XessSelected)
        {
            _vendorLatency?.SetMode(0);
            if (_streamlineReflexReady && _context.Streamline is { } streamline)
                RequireLatencyProtocol("Streamline", streamline.SetReflex(0, 0), "Reflex off for XeSS pacing handoff");
        }
        _appliedLatencyMode = -1;
        _swapchain?.SetFrameGenerationProvider(SwapchainFrameGenerationProvider);
    }

    /// <summary>
    /// The provider a Vulkan swapchain presents for. The Vulkan fallback never
    /// generates XeSS frames: only the Intel DXGI proxy owns that provider's
    /// present mode and frame cadence.
    /// </summary>
    private string SwapchainFrameGenerationProvider => XessSelected ? "off" : _frameGenerationProvider;

    /// <summary>Reserves the real-present ID and, when requested, an earlier generated-present ID.</summary>
    internal void ReserveFramePresentIds(bool mayGenerate)
    {
        _generatedPresentId = mayGenerate ? PresentIdCounter.Next() : 0;
        _realPresentId = PresentIdCounter.Next();
    }

    /// <summary>Present ID reserved for the current rendered frame.</summary>
    internal ulong RealPresentId => _realPresentId;

    /// <summary>Clears a queued generated image and resets its CPU pacing history.</summary>
    internal void ResetGeneratedFramePresent()
    {
        _generatedFrameForPresent = 0;
        _generatedFramePacer.Reset();
    }

    /// <summary>
    /// Schedules exactly one generated image ahead of the rendered image. FIFO
    /// presentation provides the two display intervals; callers keep the source
    /// image alive through the frame timeline.
    /// </summary>
    internal bool QueueGeneratedFrameForPresent(int textureId)
    {
        if (!CanPresentGeneratedFrame || _textures.Get(textureId) == null) return false;
        _generatedFrameForPresent = textureId;
        return true;
    }

    private bool PresentGeneratedFrame(ulong renderValue)
    {
        int textureId = _generatedFrameForPresent;
        _generatedFrameForPresent = 0;
        if (textureId <= 0 || _swapchain == null || _presentPath == null) return false;
        VulkanTexture? source = _textures.Get(textureId);
        if (source == null || !_swapchain.TryAcquire(out PresentTarget target)) return false;

        CommandBuffer commands = _frames.BeginPresentCommands();
        _presentPath.Record(commands, target, source);
        ulong presentValue;
        _vendorLatency?.GeneratedMarker(rendering: true, start: true);
        try { presentValue = _frames.SubmitPresent(target.AcquireSemaphore,
            _presentPath.AcquireWaitStage, renderValue, target.PresentSemaphore); }
        finally { _vendorLatency?.GeneratedMarker(rendering: true, start: false); }
        _swapchain.NotePresentSubmitted(target, presentValue);
        // A generated image has no input/simulation phase of its own. Its
        // present id is still allocated so the following real image remains
        // monotonically ordered across swapchain recreation.
        ulong generatedId;
        _vendorLatency?.GeneratedMarker(rendering: false, start: true);
        try { generatedId = _swapchain.Present(target, 0, _generatedPresentId); }
        finally { _vendorLatency?.GeneratedMarker(rendering: false, start: false); }
        if (generatedId == 0) return false;
        VulkanStats.NotePresent(generated: true);
        _generatedFramePacer.NoteGeneratedPresent();
        if (!_generatedPresentLogged)
        {
            RenderLogger?.Notification("VulkanStory: first generated frame submitted for presentation");
            _generatedPresentLogged = true;
        }
        return true;
    }

}

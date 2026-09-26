using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Optimum.Render.Vulkan.Present;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal bool StreamlineFrameGenerationAvailable => _streamlineFeaturesReady && _streamlineFrameGenerationSupported &&
        _context.Streamline != null && _swapchain != null;

    internal bool StreamlineFrameGenerationReady => StreamlineFrameGenerationAvailable &&
        !_swapchain!.NeedsRecreation && !_swapchain.Parked && !_swapchain.Fsr3ProxyActive;

    internal bool Fsr3ProxyReady => _swapchain is { Fsr3ProxyActive: true };
    internal nint Fsr3ProxyContext => _swapchain?.Fsr3ProxyContext ?? 0;
    internal Format Fsr3ProxyFormat => _swapchain?.Format ?? Format.Undefined;
    internal string? Fsr3ProxyFailure => _swapchain?.Fsr3ProxyFailure;

    internal int TagStreamlineFrame(int depthId, int motionId, int hudlessId, int uiId,
        int uprightDepthId, int uprightMotionId, int uprightHudlessId, int uprightUiId,
        in NgxFrameGenerationCamera camera, bool reset)
    {
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
            return _context.Streamline!.TagFrame(commands, &d, &m, &h, &u, &frame,
                _swapchain!.Extent.Width, _swapchain.Extent.Height);
        }
    }

    internal int SetStreamlineFrameGeneration(bool enabled) =>
        _context.Streamline == null || _swapchain == null ? -1 :
        _context.Streamline.SetFrameGeneration(enabled, _swapchain.Extent.Width,
            _swapchain.Extent.Height, _swapchain.Format, _swapchain.ImageCount);

    internal int GetStreamlineFrameGenerationState(out uint status, out uint presented)
    {
        status = 0; presented = 0;
        return _context.Streamline?.GetFrameGenerationState(out status, out presented) ?? -1;
    }

    internal int TakeStreamlinePresentError() => _context.Streamline?.TakePresentError() ?? 0;

    internal void RebuildStreamlineSwapchain() =>
        _swapchain?.RequestRebuild(_windowWidth, _windowHeight, _vsync);

    internal void SuspendStreamlineFrameGeneration()
    {
        if (_context.Streamline == null) return;
        _context.Streamline.InvalidateFrameTags();
        if (_swapchain != null) SetStreamlineFrameGeneration(false);
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
            OwnerPlatform?.Logger.Notification(
                "Optimum: FidelityFX SDK reports {0} frames actually presented over {1} rendered frames",
                count, _fsr3CountedRenderedFrames);
        }
    }

    internal bool CanPresentGeneratedFrame => _frameActive && _swapchain != null &&
        !_swapchain.NeedsRecreation && !_swapchain.Parked &&
        _swapchain.PresentMode != PresentModeKHR.MailboxKhr;

    internal void SetFrameGenerationPresentation(string provider)
    {
        if (provider != "xess") StopXessPresenter();
        _frameGenerationProvider = provider;
        _xessFailure = null;
        if (provider == "xess")
        {
            _vendorLatency?.SetMode(0);
            if (_streamlineFeaturesReady)
                _context.Streamline?.SetReflex(0, _vendorFrameCap);
        }
        _appliedLatencyMode = -1;
        _swapchain?.SetFrameGenerationProvider(provider == "xess" ? "off" : provider);
    }

    internal void ReserveFramePresentIds(bool mayGenerate)
    {
        _generatedPresentId = mayGenerate ? PresentIdCounter.Next() : 0;
        _realPresentId = PresentIdCounter.Next();
    }

    internal ulong RealPresentId => _realPresentId;

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
        ulong presentValue = _frames.SubmitPresent(target.AcquireSemaphore,
            _presentPath.AcquireWaitStage, renderValue, target.PresentSemaphore);
        _swapchain.NotePresentSubmitted(target, presentValue);
        // A generated image has no input/simulation phase of its own. Its
        // present id is still allocated so the following real image remains
        // monotonically ordered across swapchain recreation.
        _swapchain.Present(target, 0, _generatedPresentId);
        VulkanStats.NotePresent(generated: true);
        _generatedFramePacer.NoteGeneratedPresent();
        if (!_generatedPresentLogged)
        {
            OwnerPlatform?.Logger.Notification("Optimum: first generated frame submitted for presentation");
            _generatedPresentLogged = true;
        }
        return true;
    }

    /// <summary>Creates a DLSS-FG feature on the current recording buffer.</summary>
    internal NgxResult CreateDlssFrameGeneration(uint width, uint height, Format format,
        out NgxFrameGenerationFeature? feature)
    {
        feature = null;
        if (!_frameActive) return NgxResult.FailNotInitialized;
        _targets.FlushAllPendingClears(Commands);
        _targets.EndRendering(Commands);
        NgxResult result = NgxFrameGenerationFeature.Create(Commands, width, height, format, out feature);
        _dynamicState.Invalidate();
        return result;
    }

    internal void RetireDlssFrameGeneration(NgxFrameGenerationFeature feature) =>
        _frames.DeferDeletion(feature);

    /// <summary>
    /// Transitions all seven images before asking NGX to record interpolation.
    /// The outputs are storage images and remain owned by the frame timeline.
    /// </summary>
    internal NgxResult EvaluateDlssFrameGeneration(NgxFrameGenerationFeature feature,
        int backbufferId, int depthId, int motionId, int hudlessId, int uiId,
        int interpolatedId, int realId, in NgxFrameGenerationCamera camera, bool reset)
    {
        if (feature == null || !feature.IsValid) return NgxResult.FailFeatureNotFound;
        if (!_frameActive) return NgxResult.FailNotInitialized;

        VulkanTexture? backbuffer = _textures.Get(backbufferId);
        VulkanTexture? depth = _textures.Get(depthId);
        VulkanTexture? motion = _textures.Get(motionId);
        VulkanTexture? hudless = hudlessId > 0 ? _textures.Get(hudlessId) : null;
        VulkanTexture? ui = uiId > 0 ? _textures.Get(uiId) : null;
        VulkanTexture? interpolated = _textures.Get(interpolatedId);
        VulkanTexture? real = realId > 0 ? _textures.Get(realId) : null;
        if (backbuffer == null || depth == null || motion == null || interpolated == null ||
            (hudlessId > 0 && hudless == null) || (uiId > 0 && ui == null))
            return NgxResult.FailMissingInput;
        if (backbufferId == interpolatedId || (realId > 0 &&
            (backbufferId == realId || interpolatedId == realId)))
            return NgxResult.FailInvalidParameter;

        CommandBuffer commands = Commands;
        _targets.FlushAllPendingClears(commands);
        _targets.EndRendering(commands);

        _textures.Require(_barriers, commands, backbuffer, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, depth, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, motion, ResourceUsage.SampleExternal);
        if (hudless != null) _textures.Require(_barriers, commands, hudless, ResourceUsage.SampleExternal);
        if (ui != null) _textures.Require(_barriers, commands, ui, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commands, interpolated, ResourceUsage.StorageWriteExternal);
        if (real != null) _textures.Require(_barriers, commands, real, ResourceUsage.StorageWriteExternal);
        _barriers.Flush(commands);

        NgxResult result = feature.Evaluate(commands,
            NgxResourceVk.Texture(backbuffer, readWrite: false),
            NgxResourceVk.Texture(depth, readWrite: false),
            NgxResourceVk.Texture(motion, readWrite: false),
            hudless == null ? default : NgxResourceVk.Texture(hudless, readWrite: false),
            ui == null ? default : NgxResourceVk.Texture(ui, readWrite: false),
            NgxResourceVk.Texture(interpolated, readWrite: true),
            real == null ? default : NgxResourceVk.Texture(real, readWrite: true), camera, reset);
        _dynamicState.Invalidate();
        return result;
    }
}

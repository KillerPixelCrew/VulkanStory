using System;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

/// <summary>XeSS-FG presentation handoff and Vulkan swapchain restoration portion of the renderer.</summary>
public sealed unsafe partial class VulkanDevice
{
    private IntPtr _presentationWindow;
    private IVulkanWindowSurface? _presentationSurfaceSource;
    private string _frameGenerationProvider = "off";
    private int _frameGenerationMultiplier = 2;
    /// <summary>Host-selected total frame multiplier; the provider retains SDK clamping.</summary>
    public int FrameGenerationMultiplier
    {
        get => System.Threading.Volatile.Read(ref _frameGenerationMultiplier);
        set => System.Threading.Volatile.Write(ref _frameGenerationMultiplier, value);
    }
    private XessFgPresenter? _xessPresenter;
    private XessSourceImages? _xessSources;
    private XessPresentationFrame _xessConstants;
    private string? _xessFailure;
    private ulong _xessRenderedFrames;
    private ulong _xessPresentedFrames;
    private uint _xessEffectiveGeneratedFrames;
    /// <summary>Whether the active presenter's XeLL options were applied at a pre-input sleep boundary.</summary>
    private bool _xessProtocolReady;
    private int _xellAppliedFrameCap = -1;
    /// <summary>Whether interpolation is currently enabled in the active presenter.</summary>
    private bool _xessGenerationEnabled;
    /// <summary>Set by <see cref="PauseXessFrameGeneration" />; cleared once a frame prepares complete inputs again.</summary>
    private bool _xessGenerationPaused;
    /// <summary>Generation was (re-)enabled; the next generating present resets XeSS history.</summary>
    private bool _xessHistoryResetPending;
    /// <summary>Whether the active XeSS presenter has enabled its required XeLL latency dependency, independently of the host's generic preference.</summary>
    internal bool XessRequiresLowLatency => _xessPresenter != null && _xessProtocolReady;
    /// <summary>Interpolation count most recently configured by the active XeSS presenter.</summary>
    internal uint XessConfiguredGeneratedFrames => _xessEffectiveGeneratedFrames;
    // Historical SDK report for this device owner, including while FG is suspended.
    /// <summary>Last SDK-reported interpolation limit for this device, retained while XeSS is suspended.</summary>
    internal uint? XessLastReportedGeneratedLimit { get; private set; }
    private long _lastSwapchainRestoreAttempt;
    private string? _lastSwapchainRestoreFailure;

    /// <summary>Latest XeSS presentation/setup failure recorded by the renderer.</summary>
    internal string? XessProxyFailure => _xessFailure;
    /// <summary>Whether a XeSS presenter owner is currently retained.</summary>
    internal bool XessProxyReady => _xessPresenter != null;
    /// <summary>Stops the XeSS presentation owner and restores ordinary Vulkan presentation when possible.</summary>
    /// <remarks>
    /// This replaces the DX12 presenter and the Vulkan swapchain. Use it for a real disable,
    /// a provider change or input teardown; <see cref="PauseXessFrameGeneration" /> covers
    /// frames that only cannot generate.
    /// </remarks>
    internal void SuspendXessFrameGeneration() => StopXessPresenter();

    /// <summary>
    /// Pauses interpolation for frames without complete inputs (pause, menus, a camera
    /// rebase) while the DX12 presenter keeps the window.
    /// </summary>
    /// <remarks>
    /// Frames that prepare no inputs present the current image through the presenter
    /// with generation disabled. The first pre-input sleep boundary after a frame that
    /// prepared complete inputs again re-enables generation, and that frame's present
    /// resets XeSS history. A changed output extent, or a presenter whose next shared
    /// input set was never written, still falls back to Vulkan presentation.
    /// </remarks>
    internal void PauseXessFrameGeneration()
    {
        _xessSources = null;
        _xessGenerationPaused = true;
        if (_xessGenerationEnabled && _xessPresenter is { } presenter)
            SetXessGenerationEnabled(presenter, false);
    }

    /// <summary>Applies interpolation enablement once the presenter thread has finished its in-flight present.</summary>
    private void SetXessGenerationEnabled(XessFgPresenter presenter, bool enabled)
    {
        // Native presenter calls are serialized with the present thread's proxy Present.
        presenter.WaitForPresentIdle();
        RequireLatencyProtocol("XeLL", presenter.Runtime.SetEnabled(enabled),
            enabled ? "enabling a complete XeSS frame" : "pausing XeSS frame generation");
        _xessGenerationEnabled = enabled;
        // The first enabled present after a disabled pass-through has no usable history.
        if (enabled) _xessHistoryResetPending = true;
    }

    /// <summary>Validates current scene resources and converts motion before retaining sources/constants for XeSS presentation.</summary>
    /// <returns>Zero on success, or a negative readiness/resource/conversion code.</returns>
    internal int PrepareXessFrame(int depthId, int motionId, int motionRgId,
        int hudlessId, int uiId, in XessPresentationFrame constants)
    {
        using GpuSection gpuSection = BeginGpuSection("fg_xess_prepare");
        if (!_frameActive || !XessSelected) return -1;
        VulkanTexture? color = DefaultColorTexture();
        VulkanTexture? depth = _textures.Get(depthId);
        VulkanTexture? sourceMotion = _textures.Get(motionId);
        VulkanTexture? motion = _textures.Get(motionRgId);
        VulkanTexture? hudless = _textures.Get(hudlessId);
        VulkanTexture? ui = _textures.Get(uiId);
        if (color == null || depth == null || sourceMotion == null ||
            motion == null || hudless == null || ui == null) return -2;
        if (!PrepareUpscalerMotion(sourceMotion, motion, depth.Width, depth.Height, Commands))
            return -3;
        _xessSources = new XessSourceImages(color, depth, motion, hudless, ui);
        _xessConstants = constants;
        _xessGenerationPaused = false;
        return 0;
    }

    /// <summary>Reuses compatible presenter resources or replaces Vulkan presentation with the XeSS DXGI owner.</summary>
    private bool EnsureXessPresenter(in XessSourceImages sources)
    {
        if (_xessPresenter is { } active)
        {
            if (active.MatchesSources(sources))
                return true;
            _frames.ClearSubmitGate();
            active.Dispose();
            _xessPresenter = null;
        }
        if (_presentationWindow == IntPtr.Zero) return false;
        // Intel's DXGI proxy must be the only swapchain on this HWND.
        _swapchain?.Dispose();
        _swapchain = null;
        nint hwnd;
        try { hwnd = _presentationSurfaceSource?.Win32Handle ?? 0; }
        catch (Exception error)
        {
            _xessFailure = "Win32 window lookup failed: " + error.Message;
            RestoreVulkanSwapchain();
            return false;
        }
        string reason = "no Win32 window for XeSS-FG";
        if (hwnd == 0 || !XessFgPresenter.TryCreate(_context, hwnd,
                sources.Color.Width, sources.Color.Height, _vsync, sources,
                MarkAsyncPclPresent,
                out _xessPresenter, out reason))
        {
            _xessFailure = hwnd == 0 ? "no Win32 window for XeSS-FG" : reason;
            _xessPresenter?.RequireResourceLifetime();
            RestoreVulkanSwapchain();
            return false;
        }
        _xessFailure = null;
        _xessRenderedFrames = 0;
        _xessPresentedFrames = 0;
        _xessEffectiveGeneratedFrames = 0;
        _xessProtocolReady = false;
        _xellAppliedFrameCap = -1;
        _xessGenerationEnabled = false;
        _xessHistoryResetPending = false;
        RequireLatencyProtocol("XeLL", _xessPresenter!.Runtime.SetLatencyMode(0, false), "initial pass-through options");
        _appliedLatencyMode = -1;
        MirrorValidationMessage("--- XeSS-FG DX12 proxy active on the Vulkan window");
        return true;
    }

    /// <summary>Recreates the Vulkan surface/swapchain after XeSS relinquishes the window.</summary>
    private void RestoreVulkanSwapchain()
    {
        if (_swapchain != null || _presentationSurfaceSource == null) return;
        _lastSwapchainRestoreAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_presentationSurfaceSource.TryCreate(_context,
                out SurfaceKHR surface, out string? failure))
        {
            ReportSwapchainRestoreFailure("Vulkan surface restore failed: " + failure);
            return;
        }
        if (!Swapchain.TryCreate(_context, surface, _windowWidth, _windowHeight,
                _vsync, _frames.Timeline, out Swapchain? swapchain,
                out failure, _vendorLatency))
        {
            ReportSwapchainRestoreFailure("Vulkan swapchain restore failed: " + failure);
            return;
        }
        _swapchain = swapchain;
        _lastSwapchainRestoreFailure = null;
        _swapchain.SetFrameGenerationProvider(SwapchainFrameGenerationProvider);
    }

    private void ReportSwapchainRestoreFailure(string message)
    {
        if (_lastSwapchainRestoreFailure == message) return;
        _lastSwapchainRestoreFailure = message;
        AddDiagnostic(message);
    }

    private void RetrySwapchainRestoreIfNeeded()
    {
        if (_presentationWindow == IntPtr.Zero || _swapchain != null ||
            _xessPresenter != null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastSwapchainRestoreAttempt != 0 &&
            now - _lastSwapchainRestoreAttempt < System.Diagnostics.Stopwatch.Frequency)
            return;
        RestoreVulkanSwapchain();
    }

    private static readonly bool XessGpuGate =
        Environment.GetEnvironmentVariable("VULKANSTORY_XESS_GPU_GATE") != "0";

    /// <summary>Clears pending submit gates, disposes the XeSS presenter and attempts Vulkan swapchain restoration.</summary>
    private void StopXessPresenter()
    {
        _xessSources = null;
        _frames.ClearSubmitGate();
        XessFgPresenter? active = _xessPresenter;
        if (active != null)
        {
            active.Dispose();
            _xessPresenter = null;
            _xessGenerationEnabled = false;
            _xessHistoryResetPending = false;
            _appliedLatencyMode = -1;
            RestoreVulkanSwapchain();
        }
    }

    private bool PresentXessFrame(ulong renderValue, long presentEntry, long frameSubmitted)
    {
        if (!XessSelected) return false;
        XessSourceImages? staged = _xessSources;
        _xessSources = null;
        XessFgPresenter presenter;
        VulkanTexture? passThroughColor = null;
        if (staged is { } sources)
        {
            if (!EnsureXessPresenter(sources)) return false;
            presenter = _xessPresenter!;
        }
        else
        {
            // A paused presenter keeps the window: this frame's image is presented
            // through it with generation disabled.
            if (!_xessGenerationPaused || _xessGenerationEnabled || _xessPresenter is not { } paused)
                return false;
            passThroughColor = DefaultColorTexture();
            if (passThroughColor == null || !paused.CanPassThrough(passThroughColor))
            {
                // A new extent needs a new DXGI swapchain; Vulkan presents this frame.
                StopXessPresenter();
                return false;
            }
            presenter = paused;
        }
        try
        {
            // The previous frame's proxy Present ran on the present thread while
            // this frame was recorded; its outcome is settled before the next hand-off.
            if (presenter.TakePresentOutcome() is { } previous) ConsumeXessPresentOutcome(previous);
            uint requested = (uint)System.Math.Clamp(
                FrameGenerationMultiplier - 1, 1, 5);
            int countResult = presenter.Runtime.SetGeneratedFrames(requested,
                out uint effective, out uint maximum);
            if (countResult < 0)
                throw new InvalidOperationException("XeSS-FG generated-frame count failed (" +
                    countResult + ")");
            XessLastReportedGeneratedLimit = maximum;
            if (effective != _xessEffectiveGeneratedFrames)
            {
                RenderLogger?.Notification("VulkanStory: XeSS-FG requested {0}×, effective {1}× (SDK maximum {2}×)",
                    requested + 1, effective + 1, maximum + 1);
                _xessEffectiveGeneratedFrames = effective;
            }
            CommandBuffer commands = _frames.BeginPresentCommands();
            _gpuTimestamps?.Mark(commands, "fg_xess_handoff_copies");
            XessFgPresenter.PreparedFrame prepared = staged is { } frameSources
                ? presenter.RecordCopies(commands, _textures, _barriers, frameSources)
                : presenter.RecordColorCopy(commands, _textures, _barriers, passThroughColor!);
            ulong presentValue = _frames.SubmitExternalPresent(renderValue,
                presenter.SharedSemaphore, prepared.WaitForDx12, prepared.ReadyForDx12);
            // XeLL's sleep/markers and XeSS-FG's DXGI present must carry the
            // same frame key, even if the constants were staged earlier.
            uint presentFrameId = (uint)_latencyFrameId;
            if (_xessConstants.FrameId != presentFrameId)
                TraceLatency("XeSS-FG frame key corrected: staged=" +
                    _xessConstants.FrameId + " current=" + presentFrameId);
            _xessConstants.FrameId = presentFrameId;
            _xessConstants.Vsync = _vsync ? 1u : 0u;
            // XeLL and PCL present markers come from the present thread around
            // the real Present; the CPU timing recorder marks this hand-off.
            Latency.Marker(_latencyFrameId, LatencyMarker.PresentStart);
            nint pclToken = _streamlinePclReady && _streamlineTokenFrameId == _latencyFrameId
                ? _streamlineTokenPointer : 0;
            // Disabled pass-through retains no interpolation history; seed the
            // first enabled present before XeSS uses a previous frame's inputs.
            if (passThroughColor != null) _xessConstants.Reset = 1;
            else if (_xessHistoryResetPending && _xessGenerationEnabled)
            {
                _xessConstants.Reset = 1;
                _xessHistoryResetPending = false;
            }
            presenter.QueuePresent(prepared, _xessConstants, _latencyFrameId, pclToken);
            // The DX12 and Vulkan contexts time-slice the GPU: run concurrently, XeSS-FG's
            // ~2 ms of interpolation took ~5.4 ms and both queues idled at the switches.
            // The next frame's GPU work waits for this frame's DX12 work instead; the CPU
            // still records ahead (docs/performance-profile-2026-09-26.md).
            if (XessGpuGate) _frames.GateNextFrameSubmit(presenter.SharedSemaphore, prepared.DoneByDx12);
            Latency.Marker(_latencyFrameId, LatencyMarker.PresentEnd);
            VulkanStats.NotePresent(generated: false);
            Latency.OnPresent(_latencyFrameId, _realPresentId);
            _realPresentId = 0;
            _generatedPresentId = 0;
            LastPresentTimingsForTests = new PresentTimings(presentEntry, frameSubmitted,
                frameSubmitted, System.Diagnostics.Stopwatch.GetTimestamp(), renderValue,
                presentValue, false, true);
            return true;
        }
        catch (Exception error)
        {
            _xessFailure = error.Message;
            AddDiagnostic(_xessFailure);
            StopXessPresenter();
            return true;
        }
    }

    private void ConsumeXessPresentOutcome(in XessFgPresenter.PresentOutcome outcome)
    {
        if (outcome.Error != null)
            throw new InvalidOperationException("XeSS-FG proxy Present threw: " + outcome.Error.Message, outcome.Error);
        if (outcome.Code != 0)
            throw new InvalidOperationException("XeSS-FG proxy Present failed (" + outcome.Code + ")");
        if (outcome.FrameGenResult < 0)
            throw new InvalidOperationException("XeSS-FG SDK reported " + outcome.FrameGenResult);
        VulkanStats.NoteSdkActualPresents(outcome.FramesPresented);
        _xessRenderedFrames++;
        _xessPresentedFrames += outcome.FramesPresented;
        if (_xessRenderedFrames == 120 &&
            _xessPresentedFrames <= _xessRenderedFrames)
            RenderLogger?.Warning(
                "VulkanStory: XeSS-FG has not reported extra presented frames after 120 rendered frames; enabled={0}, SDK result={1}",
                outcome.FrameGenEnabled, outcome.FrameGenResult);
        if (_xessRenderedFrames % 120 == 0)
            RenderLogger?.Notification("VulkanStory: XeSS-FG SDK reports {0} frames presented over {1} rendered frames; enabled={2}",
                _xessPresentedFrames, _xessRenderedFrames, outcome.FrameGenEnabled);
    }
}

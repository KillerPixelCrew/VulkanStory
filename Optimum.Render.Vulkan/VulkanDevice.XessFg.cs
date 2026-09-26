using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private IntPtr _presentationWindow;
    private string _frameGenerationProvider = "off";
    private XessFgPresenter? _xessPresenter;
    private XessSourceImages? _xessSources;
    private XessPresentationFrame _xessConstants;
    private string? _xessFailure;
    private ulong _xessRenderedFrames;
    private ulong _xessPresentedFrames;
    private long _lastSwapchainRestoreAttempt;
    private string? _lastSwapchainRestoreFailure;

    internal string? XessProxyFailure => _xessFailure;
    internal bool XessProxyReady => _xessPresenter != null;
    internal void SuspendXessFrameGeneration() => StopXessPresenter();

    internal int PrepareXessFrame(int depthId, int motionId, int motionRgId,
        int hudlessId, int uiId, in XessPresentationFrame constants)
    {
        if (!_frameActive || _frameGenerationProvider != "xess") return -1;
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
        return 0;
    }

    private bool EnsureXessPresenter(in XessSourceImages sources)
    {
        if (_xessPresenter is { } active)
        {
            if (active.Width == sources.Color.Width && active.Height == sources.Color.Height)
                return true;
            active.Dispose();
            _xessPresenter = null;
        }
        if (_presentationWindow == IntPtr.Zero) return false;
        // Intel's DXGI proxy must be the only swapchain on this HWND.
        _swapchain?.Dispose();
        _swapchain = null;
        nint hwnd;
        try { hwnd = WindowSurface.Win32Handle(_presentationWindow); }
        catch (Exception error)
        {
            _xessFailure = "Win32 window lookup failed: " + error.Message;
            RestoreVulkanSwapchain();
            return false;
        }
        string reason = "no Win32 window for XeSS-FG";
        if (hwnd == 0 || !XessFgPresenter.TryCreate(_context, hwnd,
                sources.Color.Width, sources.Color.Height, _vsync, sources,
                out _xessPresenter, out reason))
        {
            _xessFailure = hwnd == 0 ? "no Win32 window for XeSS-FG" : reason;
            RestoreVulkanSwapchain();
            return false;
        }
        _xessFailure = null;
        _xessRenderedFrames = 0;
        _xessPresentedFrames = 0;
        int latencyResult = _xessPresenter!.Runtime.SetLatencyMode(_vendorFrameCap,
            DesiredLatencyMode != 0);
        if (latencyResult != 0)
            AddDiagnostic("XeLL mode returned " + latencyResult);
        _appliedLatencyMode = -1;
        MirrorValidationMessage("--- XeSS-FG DX12 proxy active on the Vulkan window");
        return true;
    }

    private void RestoreVulkanSwapchain()
    {
        if (_swapchain != null || _presentationWindow == IntPtr.Zero) return;
        _lastSwapchainRestoreAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!WindowSurface.TryCreate(_context, _presentationWindow,
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
        _swapchain.PresentIdEnabled = _context.Capabilities.PresentIdEnabled;
        // The Vulkan fallback never generates XeSS frames. Only the Intel DXGI
        // proxy owns that provider's present mode and frame cadence.
        if (_frameGenerationProvider != "xess")
            _swapchain.SetFrameGenerationProvider(_frameGenerationProvider);
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

    private void StopXessPresenter()
    {
        _xessSources = null;
        XessFgPresenter? active = _xessPresenter;
        _xessPresenter = null;
        if (active != null)
        {
            active.Dispose();
            _appliedLatencyMode = -1;
            RestoreVulkanSwapchain();
        }
    }

    private bool PresentXessFrame(ulong renderValue, long presentEntry, long frameSubmitted)
    {
        if (_frameGenerationProvider != "xess" || _xessSources is not { } sources)
            return false;
        _xessSources = null;
        if (!EnsureXessPresenter(sources)) return false;
        XessFgPresenter presenter = _xessPresenter!;
        try
        {
            CommandBuffer commands = _frames.BeginPresentCommands();
            XessFgPresenter.PreparedFrame prepared = presenter.RecordCopies(commands,
                _textures, _barriers, sources);
            ulong presentValue = _frames.SubmitExternalPresent(renderValue,
                presenter.SharedSemaphore, prepared.WaitForDx12, prepared.ReadyForDx12);
            _xessConstants.Vsync = _vsync ? 1u : 0u;
            MarkLatency(_latencyFrameId, LatencyMarker.PresentStart);
            int result = presenter.Present(prepared, _xessConstants,
                out uint presented, out int fgResult, out bool fgEnabled);
            MarkLatency(_latencyFrameId, LatencyMarker.PresentEnd);
            if (result != 0)
                throw new InvalidOperationException("XeSS-FG proxy Present failed (" + result + ")");
            if (fgResult < 0)
                throw new InvalidOperationException("XeSS-FG SDK reported " + fgResult);
            VulkanStats.NoteSdkActualPresents(presented);
            VulkanStats.NotePresent(generated: false);
            _xessRenderedFrames++;
            _xessPresentedFrames += presented;
            if (_xessRenderedFrames == 120 &&
                _xessPresentedFrames <= _xessRenderedFrames)
                OwnerPlatform?.Logger.Warning(
                    "Optimum: XeSS-FG has not reported extra presented frames after 120 rendered frames; enabled={0}, SDK result={1}",
                    fgEnabled, fgResult);
            if (_xessRenderedFrames % 120 == 0)
                OwnerPlatform?.Logger.Notification("Optimum: XeSS-FG SDK reports {0} frames presented over {1} rendered frames; enabled={2}",
                    _xessPresentedFrames, _xessRenderedFrames, fgEnabled);
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
}

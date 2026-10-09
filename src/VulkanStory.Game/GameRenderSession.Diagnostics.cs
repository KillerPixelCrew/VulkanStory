using System.Diagnostics;
using System.Text.Json;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private readonly string? diagnosticDirectory = Environment.GetEnvironmentVariable("VULKANSTORY_RUNTIME_DIAGNOSTICS");
    private readonly Stopwatch diagnosticClock = Stopwatch.StartNew();
    private bool diagnosticsDisabled, diagnosticWorldReady, diagnosticCaptureAttempted;
    private long diagnosticNextSample, diagnosticWorldFrames, diagnosticUpscaleFrames, diagnosticPreparedFrames;
    private string? diagnosticCapture;
    private ulong diagnosticCaptureFrame;

    private bool DiagnosticsEnabled()
    {
        if (diagnosticsDisabled || string.IsNullOrWhiteSpace(diagnosticDirectory)) return false;
        if (Path.IsPathFullyQualified(diagnosticDirectory)) return true;
        diagnosticsDisabled = true;
        platform.Logger.Warning("VulkanStory: runtime diagnostics require an absolute output directory.");
        return false;
    }

    private void CaptureDiagnosticWorldFrame()
    {
        if (!DiagnosticsEnabled() || !diagnosticWorldReady || Temporal.CurrentClient == null || diagnosticCaptureAttempted) return;
        var frame = Temporal.Snapshot();
        if (!frame.WorldCaptured || frame.FrameId != Device.LatencyFrameId ||
            (services.RendererSettings.UpscalerReplacesTaa && !Graphics.UpscaledCompositeReady))
        { diagnosticWorldFrames = 0; return; }
        if (++diagnosticWorldFrames < 60) return;
        diagnosticCaptureAttempted = true;
        try
        {
            Directory.CreateDirectory(diagnosticDirectory!);
            // Use the existing composed-frame screenshot route before FG tags are
            // supplied: synchronous readback must not split tagged command work.
            Graphics.LoadFramebuffer(EnumFrameBuffer.Default);
            string filename = Path.Combine(diagnosticDirectory!, "world-rendered-" + Environment.ProcessId + ".png");
            Graphics.SaveScreenshot(diagnosticDirectory, filename, false, true, null);
            diagnosticCapture = filename;
            diagnosticCaptureFrame = Device.LatencyFrameId;
            diagnosticNextSample = 0;
            platform.Logger.Notification("VulkanStory: composed world framebuffer captured to {0}", filename);
        }
        catch (Exception error)
        {
            platform.Logger.Warning("VulkanStory: diagnostic framebuffer capture failed: {0}", error.Message);
        }
    }

    /// <summary>Writes the explicitly enabled diagnostic frame status from current session state after presentation.</summary>
    private void PublishDiagnosticFrame()
    {
        if (!DiagnosticsEnabled()) return;
        if (Graphics.UpscaledThisFrame) diagnosticUpscaleFrames++;
        if (frameGeneration!.PreparedThisFrame) diagnosticPreparedFrames++;
        if (diagnosticClock.ElapsedMilliseconds < diagnosticNextSample) return;
        diagnosticNextSample = diagnosticClock.ElapsedMilliseconds + 1000;
        try
        {
            Directory.CreateDirectory(diagnosticDirectory!);
            var frame = Temporal.Snapshot();
            var settings = services.RendererSettings.Settings;
            FrameBufferRef? primary = PrimaryTarget;
            var display = Window.PixelSize;
            string json = JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, pid = Environment.ProcessId, frameId = Device.LatencyFrameId,
                worldReady = diagnosticWorldReady, worldAttached = Temporal.CurrentClient != null,
                windowVisible = Window.IsVisible, windowFocused = Window.IsFocused,
                renderWidth = primary?.Width, renderHeight = primary?.Height,
                displayWidth = display.Width, displayHeight = display.Height,
                jitterX = frame.Provider.JitterX, jitterY = frame.Provider.JitterY,
                temporalReset = frame.Provider.Reset,
                renderedDeltaTimeMs = frame.Provider.DeltaTimeMs,
                gameDitherSeed = platform.ShaderUniforms.DitherSeed,
                gameFrameWidth = platform.ShaderUniforms.FrameWidth,
                frame.WorldCaptured, frame.MotionValid, frame.CanGenerate, frame.HasCamera,
                motionReadiness = Temporal.MotionReadiness,
                compiledMotionStatus = Graphics.CompiledMotionStatus,
                requestedUpscaler = settings.Upscaler, effectiveUpscaler = services.RendererSettings.EffectiveUpscaler,
                upscalerQuality = settings.UpscalerQuality,
                upscaleEvaluatedThisFrame = Graphics.UpscaledThisFrame, successfulUpscaleFrames = diagnosticUpscaleFrames,
                requestedFrameGeneration = settings.FrameGeneration, effectiveFrameGeneration = frameGeneration.EffectiveProvider,
                inputsPreparedThisFrame = frameGeneration.PreparedThisFrame, preparedFrames = diagnosticPreparedFrames,
                preparationStatus = frameGeneration.PreparationStatus,
                configuredDlssGeneratedFrames = frameGeneration.ConfiguredDlssGeneratedFrames,
                sdkReportedDlssPresents = frameGeneration.ActualDlssPresents,
                dlssStateQueryResult = frameGeneration.LastDlssStateResult,
                dlssStatus = frameGeneration.LastDlssState?.Status,
                dlssMaximumGenerated = frameGeneration.LastDlssState?.MaximumGenerated,
                dlssMinimumDimension = frameGeneration.LastDlssState?.MinimumSize,
                dlssVsyncSupport = frameGeneration.LastDlssState?.VsyncSupport,
                dlssDynamicMfgSupport = frameGeneration.LastDlssState?.DynamicMfgSupport,
                realPresents = Interlocked.Read(ref realPresents), sdkReportedPresents = Interlocked.Read(ref sdkPresents),
                hostGeneratedPresents = Interlocked.Read(ref hostGeneratedPresents),
                sdkPresentReports = Interlocked.Read(ref sdkReports),
                fps = FpsText, presentation = Presentation,
                composedFramebufferCapture = diagnosticCapture, captureFrameId = diagnosticCaptureFrame
            });
            File.AppendAllText(Path.Combine(diagnosticDirectory!, "runtime-" + Environment.ProcessId + ".jsonl"), json + Environment.NewLine);
        }
        catch (Exception error)
        {
            diagnosticsDisabled = true;
            platform.Logger.Warning("VulkanStory: runtime diagnostic output disabled: {0}", error.Message);
        }
    }
}

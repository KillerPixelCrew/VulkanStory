using System.Diagnostics;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private string presentation = "Waiting for the first Vulkan frame.";
    private long nextPresentationPublish;
    /// <summary>Atomically published last-rendered-frame status for ordinary Options; provider requests and actual evaluation/readiness remain distinct.</summary>
    internal string Presentation => Volatile.Read(ref presentation);
    /// <summary>Primary scene framebuffer when allocated and not disposed; otherwise null.</summary>
    private FrameBufferRef? PrimaryTarget => platform.FrameBuffers is { Count: > 0 } targets &&
        targets[0] is { Disposed: false } target ? target : null;
    /// <summary>Publishes last-frame requested/effective provider, motion, AO and controller status for the ordinary settings UI.</summary>
    /// <remarks>Ordinary play publishes about four times per second. Harness and runtime-diagnostic runs keep per-frame text for evidence.</remarks>
    private void PublishPresentation()
    {
        // The text is read by Options, the status command and diagnostics; it
        // does not need rebuilding (string concatenation, P/Invoke) every frame.
        if (!HeadlessHarnessOptions.Active && !DiagnosticsEnabled())
        {
            long now = Stopwatch.GetTimestamp();
            if (now < nextPresentationPublish) return;
            nextPresentationPublish = now + Stopwatch.Frequency / 4;
        }
        RendererSettings settings = services.RendererSettings.Settings;
        string sr = Graphics.UpscaledThisFrame ? services.RendererSettings.EffectiveUpscaler : "off";
        string fg = frameGeneration!.EffectiveProvider + ": " + frameGeneration.PreparationStatus;
        FrameBufferRef? primary = PrimaryTarget;
        var display = Window.PixelSize;
        string text = "Last rendered frame\n" +
            "Upscaler requested: " + settings.Upscaler + " / " + settings.UpscalerQuality + "; evaluated: " + sr + "\n" +
            "Render resolution: " + (Graphics.AllocatedUpscalerPlan is { } plan
                ? plan.RenderWidth + "x" + plan.RenderHeight + " -> " + plan.DisplayWidth + "x" + plan.DisplayHeight
                : primary != null ? primary.Width + "x" + primary.Height + " -> " + display.Width + "x" + display.Height
                    : "targets not allocated; output " + display.Width + "x" + display.Height) + "\n" +
            "Frame generation requested: " + settings.FrameGeneration + "; " + fg + "\n" +
            "Low latency requested: " + settings.LowLatencyMode +
                (Device.XessRequiresLowLatency ? "; XeLL On (required by XeSS-FG presenter)" : "") + "\n" +
            "TAA resolve: " + (Graphics.TaaResolvedThisFrame ? "completed" : "off") + "; motion: " +
            (Temporal.Snapshot().MotionValid ? "current" : "unavailable") + "\n" +
            "AO: " + (Graphics.AmbientOcclusionShadersUseGtao ? "GTAO" : "SSAO / off") + "\n" +
            "Controller: " + (settings.ControllerEnabled ? controllers?.Status ?? "not initialized" : "disabled");
        if (settings.Upscaler != "off" && upscalers?.Unavailable(settings.Upscaler) is string refusal)
            text += "\nUpscaler unavailable: " + refusal;
        if (settings.FrameGeneration != "off")
        {
            uint configured = frameGeneration.ConfiguredGeneratedFrames;
            text += "\nFG multiplier requested: " + settings.FrameGenerationMultiplier +
                "x; configured: " + (configured > 0 ? (configured + 1) + "x" : "inactive");
            if (settings.FrameGeneration == "fsr3")
                text += "\nFSR3 multiplier limit: 2x";
            else if (frameGeneration.LastReportedSdkGeneratedLimit is uint limit)
                text += "\nLast reported FG multiplier limit: " + (limit + 1) + "x";
            else
                text += "\nFG multiplier limit: not reported yet";
        }
        if (settings.FrameGeneration == "dlss")
        {
            text += "\nDLSS-G reported presents: " + frameGeneration.ActualDlssPresents;
            if (frameGeneration.LastDlssState is { } state)
                text += "\nSDK generated-frame limit: " + state.MaximumGenerated +
                    "; minimum dimension: " + state.MinimumSize +
                    "; VSync: " + (state.VsyncSupport == 1 ? "supported" : state.VsyncSupport == 0 ? "unsupported" : "unknown");
        }
        Volatile.Write(ref presentation, text);
    }
    private void ClearPresentation()
    {
        Volatile.Write(ref presentation, "No active world output. Frame generation is off.");
        nextPresentationPublish = 0; // The next rendered frame republishes, as before throttling.
    }
}

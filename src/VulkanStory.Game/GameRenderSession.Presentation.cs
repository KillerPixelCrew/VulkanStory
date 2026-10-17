using System.Diagnostics;
using System.Text.Json;
using Vintagestory.API.Client;
using VulkanStory.Settings;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private string presentation = "Waiting for the first Vulkan frame.";
    private long nextPresentationPublish;
    private string settingsAvailability = "{}";
    private bool? xessRuntimesInstalled;
    internal string SettingsAvailability => Volatile.Read(ref settingsAvailability);
    private void PublishSettingsAvailability()
    {
        var unavailable = new Dictionary<string, string>();
        foreach (var (provider, _) in RendererChoices.Get("Upscaler"))
            if (provider != "off")
            {
                string? reason = services.RendererSettings.StereoRendering ? "Unavailable in VR" : upscalers?.Unavailable(provider);
                if (reason != null) unavailable["Upscaler." + provider] = reason;
            }
        foreach (var (provider, _) in RendererChoices.Get("FrameGeneration"))
        {
            if (provider == "off") continue;
            string? reason = services.RendererSettings.StereoRendering ? "Unavailable in VR" : frameGeneration?.Unavailable(provider);
            if (reason == null && provider == "dlss" && !Device.StreamlineFrameGenerationAvailable)
                reason = "Streamline DLSS-G and Reflex are unavailable";
            if (reason == null && provider == "fsr3" && Fsr3Native.TryLoad(out string? fsrFailure) == null)
                reason = fsrFailure ?? "FidelityFX frame generation is unavailable";
            if (reason == null && provider == "xess")
            {
                if (upscalers?.XessInteropAvailable != true) reason = "Vulkan/DX12 image sharing is unavailable";
                else if (!(xessRuntimesInstalled ??= new[] { "VulkanStoryXessFg.dll", "libxess_fg.dll", "libxell.dll" }.All(file =>
                    File.Exists(Path.Combine(NativeRuntimePaths.DirectoryContaining(file), file)))))
                    reason = "XeSS-FG runtimes are not installed";
                else reason = Device.XessProxyFailure;
            }
            if (reason != null) unavailable["FrameGeneration." + provider] = reason;
            if (frameGeneration?.MaximumMultiplier(provider) is uint maximum)
                unavailable["FrameGenerationMaximum." + provider] = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        Volatile.Write(ref settingsAvailability, JsonSerializer.Serialize(unavailable));
    }
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
        PublishSettingsAvailability();
        string sr = Graphics.UpscaledThisFrame ? services.RendererSettings.EffectiveUpscaler : "off";
        string fg = frameGeneration!.EffectiveProvider + ": " + frameGeneration.PreparationStatus;
        FrameBufferRef? primary = PrimaryTarget;
        var display = RenderExtent;
        string text = "Last rendered frame\n" +
            "Upscaler requested: " + settings.Upscaler + " / " + settings.UpscalerQuality +
                (settings.Upscaler == "dlss" ? "; model preset: " + settings.DlssPreset.ToUpperInvariant() : "") + "; evaluated: " + sr + "\n" +
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
        if (Device.OpenXrEnabled) Volatile.Write(ref presentation, text + "\n" + Device.OpenXrStatus + "\nVR: raster stereo; temporal providers inactive.");
    }
    private void ClearPresentation()
    {
        Volatile.Write(ref presentation, "No active world output. Frame generation is off.");
        nextPresentationPublish = 0; // The next rendered frame republishes, as before throttling.
    }
}

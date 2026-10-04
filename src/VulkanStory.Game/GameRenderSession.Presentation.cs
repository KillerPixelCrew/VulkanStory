namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private string presentation = "Waiting for the first Vulkan frame.";
    internal string Presentation => Volatile.Read(ref presentation);
    private void PublishPresentation()
    {
        RendererSettings settings = services.RendererSettings.Settings;
        string sr = Graphics.UpscaledThisFrame ? services.RendererSettings.EffectiveUpscaler : "off";
        string fg = frameGeneration!.EffectiveProvider + ": " + frameGeneration.PreparationStatus;
        string text = "Last rendered frame\n" +
            "Upscaler requested: " + settings.Upscaler + " / " + settings.UpscalerQuality + "; evaluated: " + sr + "\n" +
            "Render resolution: " + (Graphics.AllocatedUpscalerPlan is { } plan
                ? plan.RenderWidth + "x" + plan.RenderHeight + " -> " + plan.DisplayWidth + "x" + plan.DisplayHeight
                : "no active upscaler plan") + "\n" +
            "Frame generation requested: " + settings.FrameGeneration + "; " + fg + "\n" +
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
    private void ClearPresentation() => Volatile.Write(ref presentation, "No active world output. Frame generation is off.");
}

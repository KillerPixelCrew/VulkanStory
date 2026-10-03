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
            "Upscaler requested: " + settings.Upscaler + "; evaluated: " + sr + "\n" +
            "Frame generation requested: " + settings.FrameGeneration + "; " + fg + "\n" +
            "TAA resolve: " + (Graphics.TaaResolvedThisFrame ? "completed" : "off") + "; motion: " +
            (Temporal.Snapshot().MotionValid ? "current" : "unavailable") + "\n" +
            "AO: " + (Graphics.AmbientOcclusionShadersUseGtao ? "GTAO" : "SSAO / off");
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

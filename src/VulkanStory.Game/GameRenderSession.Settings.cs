using Vintagestory.Client.NoObf;
using Vintagestory.API.Config;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private ShaderOverridePolicy? shaderOverrides;
    private void ConfigureDevice()
    {
        RequireOwner();
        Device.ShaderCacheDirectory = Path.Combine(GamePaths.Cache, "vulkanstory-vulkan");
        string managed = Path.GetDirectoryName(typeof(GameRenderSession).Assembly.Location) ??
            throw new InvalidOperationException("The game integration assembly has no package directory.");
        Device.NativeShaderPackageDirectory = Path.GetFullPath(Path.Combine(managed, ".."));
        Device.DebugMode = ClientSettings.GlDebugMode;
        if (HeadlessHarnessOptions.Enabled && Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_ASYNC_PIPELINES") == "1")
            Device.SynchronousPipelines = false;
        shaderOverrides = new ShaderOverridePolicy(() => platform.AssetManager,
            message => platform.Logger.Warning("{0}", message));
        Device.ShaderProgramOverriddenByMods = shaderOverrides.IsOverridden;
    }
    private void NotifyDeviceCreated()
    {
        platform.Logger.Notification("VulkanStory device: {0}; {1}; Vulkan {2}",
            Device.VendorString, Device.RendererString, Device.VersionString);
    }
    private GameShaderCallbacks CreateShaderCallbacks() => new(
        HandheldShadowTier: () => services.RendererSettings.Settings.HandheldShadowTier,
        LinkError: (pass, error) => platform.Logger.Error("VulkanStory shader '{0}' failed: {1}", pass, error ?? "unknown link error"),
        ProgramLoaded: pass => platform.Logger.Debug("VulkanStory linked shader: {0}", pass));
    /// <summary>Captures ordinary game post-processing and pacing policy before input, suppressing FXAA when the effective temporal pipeline owns reconstruction.</summary>
    /// <returns>Immutable options used for this frame input/pacing and original render dispatch.</returns>
    private GameFrameSettings CaptureFrameSettings()
    {
        RequireOwner();
        bool post = platform.DoPostProcessingEffects;
        return new GameFrameSettings(
            Bloom: ClientSettings.Bloom && post,
            GodRays: ClientSettings.GodRayQuality > 0 && post,
            Fxaa: ClientSettings.FXAA && post && !services.RendererSettings.EffectiveTemporalPipeline &&
                services.RendererSettings.Settings.RenderScale >= 1f,
            Ssao: ClientSettings.SSAOQuality > 0 && post,
            ShadowQuality: ClientSettings.ShadowMapQuality,
            // Original semantics: any nonzero mode enables the swap interval, and
            // every mode except 1 (VSync only) applies the frame-sleep limiter.
            Vsync: ClientSettings.VsyncMode != 0,
            FrameSleep: ClientSettings.VsyncMode != 1,
            MaxFps: platform.MaxFps);
    }
}

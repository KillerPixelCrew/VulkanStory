using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

/// <summary>Supplies game scene inputs to ReScaleFrame and applies renderer-specific output handling.</summary>
internal sealed class RuntimeUpscalers(RendererSettingsState state, string dataPath, Action<string> log,
    Action requestTargetRebuild, Action<float> applyTerrainLodBias) : IDisposable
{
    private readonly UpscalerSession session = new(state,
        Path.Combine(dataPath, "ModConfig", "vulkanstory-ngx"), log, requestTargetRebuild);
    private VulkanDevice? device;
    private readonly XessFgInteropRequirements xessInterop = new();
    internal bool XessInteropAvailable => xessInterop.Available;
    private bool depthRefusalLogged;
    internal string EvaluationReadiness { get; private set; } = "not evaluated";
    /// <summary>Registers SR backends and contributes their Vulkan device requirements before device initialization.</summary>
    /// <param name="target">Session-owned device receiving requirements and later provider execution.</param>
    /// <remarks>Can run once per registry; the registry borrows the device and owns its provider contexts.</remarks>
    internal void Prepare(VulkanDevice target)
    {
        if (device != null) throw new InvalidOperationException("Upscaler adapter is already prepared.");
        device = target;
        RendererNativeLibrary.EnsureRegistered();
        session.Prepare(target);
        Action<VulkanContextOptions>? previous = target.ConfigureContextOptions;
        target.ConfigureContextOptions = options =>
        {
            previous?.Invoke(options);
            options.RequirementContributors.Add(xessInterop);
            foreach (IDeviceRequirementContributor requirements in session.Requirements)
                options.RequirementContributors.Add(requirements);
        };
    }
    internal void BringUp()
    {
        VulkanDevice target = device ?? throw new InvalidOperationException("Upscaler adapter was not prepared.");
        target.UpscalerHandles(out nint instance, out nint physical, out nint logical);
        session.BringUp(instance, physical, logical);
    }
    internal string? Unavailable(string provider) => session.Unavailable(provider);
    internal UpscalerPlan? Plan(int width, int height) => session.Plan(width, height);
    internal void Disable(string reason) => session.Disable(reason);
    internal void ApplySettings()
    {
        session.ApplySettings();
        applyTerrainLodBias(0);
    }
    /// <summary>Evaluates the selected provider using matching primary color/depth/motion and planned output targets.</summary>
    /// <param name="graphics">Borrowed adapter whose per-frame SR outcome is published.</param>
    /// <param name="buffers">Current session target list using retained framebuffer slot identities.</param>
    /// <param name="temporal">Camera, jitter and timing inputs for this rendered frame.</param>
    /// <returns>True after provider output succeeds; false when prerequisites or evaluation fail.</returns>
    /// <remarks>Evaluation refusal disables the provider. Unsupported late-overlay depth blit clears output depth to far and logs the limitation once.</remarks>
    internal bool Evaluate(GameGraphicsAdapter graphics, IReadOnlyList<Vintagestory.API.Client.FrameBufferRef> buffers,
        in GameTemporalFrame temporal)
    {
        graphics.UpscaledThisFrame = false;
        int motion = graphics.FrameState.MotionAttachment;
        if (!session.Active)
        { EvaluationReadiness = Unavailable(state.Settings.Upscaler) ?? "upscaler not selected"; return false; }
        if (buffers.Count <= GameGraphicsAdapter.UpscaledSceneIndex || motion < 0)
        { EvaluationReadiness = "upscaler output or motion target unavailable"; return false; }
        var primary = buffers[0]; var output = buffers[GameGraphicsAdapter.UpscaledSceneIndex];
        if (primary?.ColorTextureIds is not { } colors || output?.ColorTextureIds is not { Length: > 0 } || colors.Length <= motion)
        { EvaluationReadiness = "upscaler colour, motion or output target unavailable"; return false; }
        int composition = graphics.UsesFsr3Inputs && graphics.Fsr3HasComposition &&
            buffers.Count > 1 && buffers[1]?.ColorTextureIds is { Length: > 1 } transparency
            ? transparency[1] : 0;
        try
        {
            if (!graphics.PrepareSdkMotion(temporal, buffers))
            { EvaluationReadiness = graphics.SdkMotionReadiness; return false; }
        }
        catch (Exception failure)
        { EvaluationReadiness = "upscaler input preparation threw: " + failure.Message; Disable(EvaluationReadiness); return false; }
        var frame = new UpscalerFrame(colors[0], primary.DepthTextureId, graphics.PreparedSdkMotionTexture,
            output.ColorTextureIds[0], temporal.Provider, composition, graphics.Fsr3HasComposition,
            MaterialMotionSource: colors[motion]);
        if (!session.Evaluate(frame))
        { EvaluationReadiness = session.EvaluationReadiness; return false; }
        applyTerrainLodBias(state.ActiveLodBias);
        if (output.DepthTextureId > 0 && !device!.UpscaleDepthNearest(primary.DepthTextureId, output.DepthTextureId))
        {
            device.ClearDepthImageToFar(output.DepthTextureId);
            if (!depthRefusalLogged) { depthRefusalLogged = true; log("VulkanStory: depth blit unsupported; late overlays have no scene occlusion."); }
        }
        graphics.UpscaledThisFrame = true; EvaluationReadiness = "ready"; return true;
    }
    /// <summary>Releases the ReScaleFrame session before renderer-device teardown.</summary>
    public void Dispose()
    {
        session.Dispose();
        device = null;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

public partial class VulkanClientPlatform
{
    private readonly Dictionary<string, IUpscalerBackend> upscalers = new(StringComparer.OrdinalIgnoreCase);
    private UpscalerPlan allocatedUpscalePlan;
    private bool upscaledThisFrame;
    private bool upscaledCompositeReady;
    private bool depthUpscaleRefusalLogged;
    private bool rebuildUpscalerTargetsPending;
    private UpscalerPlan lastEvaluatedUpscalePlan;

    internal bool UpscaledThisFrame => upscaledThisFrame;
    internal UpscalerPlan AllocatedUpscalePlan => allocatedUpscalePlan;

    private IUpscalerBackend? SelectedUpscaler =>
        upscalers.TryGetValue(OptimumConfig.EffectiveUpscaler, out IUpscalerBackend? backend)
            ? backend : null;

    public override string OptimumUpscalerUnavailableFor(string provider)
    {
        if (string.Equals(provider, "off", StringComparison.OrdinalIgnoreCase)) return null;
        if (OptimumConfig.IsUpscalerDisabled(provider)) return "this provider was disabled for this session";
        if (!upscalers.TryGetValue(provider, out IUpscalerBackend? backend))
            return "this provider is unavailable on this Vulkan device";
        return backend.Active ? null : backend.Unavailable;
    }

    public override void ApplyOptimumUpscalerSettings()
    {
        if (activeFrameGenerationProvider == "dlss") device?.SuspendStreamlineFrameGeneration();
        ResetFrameGeneration();
        foreach (IUpscalerBackend backend in upscalers.Values) backend.RetireFeature();
        OptimumConfig.ClearUpscalerPlan();
        upscaledThisFrame = false;
        lastEvaluatedUpscalePlan = default;
        base.ApplyOptimumUpscalerSettings();
        ShaderRegistry.ApplyOptimumTerrainSamplerLodBias(OptimumConfig.EffectiveTerrainLodBias);
    }
    public override bool TaaTargetsReady => base.TaaTargetsReady ||
        (MotionAttachmentIndex >= 0 &&
         ((allocatedUpscalePlan.IsValid && OptimumConfig.UpscalerReplacesTaa) ||
          OptimumConfig.EffectiveFrameGeneration != "off"));

    public override int OptimumPostSceneTexture() =>
        UpscaledSceneTarget?.ColorTextureIds?[0] ?? base.OptimumPostSceneTexture();

    private void PrepareUpscaler(VulkanDevice target)
    {
        // NGX's extensions must be enabled at device creation. Prepare the optional
        // backend even when it is currently off so a settings change can select it
        // without replacing the Vulkan device. Other vendors can join this registry.
        DlssUpscaler? dlss = DlssUpscaler.TryPrepare(UpscalerDataPath(), LogUpscaler,
            prepareForSwitching: true);
        if (dlss != null) upscalers.Add("dlss", new DlssBackend(dlss));
        upscalers.Add("xess", new XessBackend(LogUpscaler));
        upscalers.Add("fsr3", new Fsr3Backend(LogUpscaler));
        Action<VulkanContextOptions>? previous = target.ConfigureContextOptions;
        target.ConfigureContextOptions = options =>
        {
            previous?.Invoke(options);
            options.RequirementContributors.Add(new XessFgInteropRequirements());
            foreach (IUpscalerBackend backend in upscalers.Values)
                if (backend.Requirements != null)
                    options.RequirementContributors.Add(backend.Requirements);
        };
    }

    private void BringUpUpscaler(VulkanDevice target)
    {
        target.UpscalerHandles(out IntPtr instance, out IntPtr physical, out IntPtr logical);
        foreach (IUpscalerBackend backend in new List<IUpscalerBackend>(upscalers.Values))
        {
            try
            {
                if (backend.BringUp(target, instance, physical, logical)) continue;
            }
            catch (Exception error)
            {
                LogUpscaler("[Optimum] " + backend.Id + " bring-up: " + error.Message);
            }
            try { backend.Shutdown(); }
            catch (Exception error) { LogUpscaler("[Optimum] " + backend.Id + " teardown: " + error.Message); }
            upscalers.Remove(backend.Id);
        }
    }

    private void ShutDownUpscaler()
    {
        foreach (IUpscalerBackend backend in upscalers.Values)
        {
            try { backend.Shutdown(); }
            catch (Exception error) { LogUpscaler("[Optimum] " + backend.Id + " teardown: " + error.Message); }
        }
        upscalers.Clear();
        allocatedUpscalePlan = default;
        upscaledThisFrame = false;
        lastEvaluatedUpscalePlan = default;
        OptimumConfig.ClearUpscalerPlan();
    }

    private bool TryPlanUpscale(int displayWidth, int displayHeight,
        out int renderWidth, out int renderHeight)
    {
        allocatedUpscalePlan = default;
        renderWidth = displayWidth;
        renderHeight = displayHeight;
        IUpscalerBackend? selected = SelectedUpscaler;
        if (selected == null)
        {
            if (OptimumConfig.UpscalerReplacesTaa)
                DisableUpscaler("the selected provider is unavailable on this device");
            return false;
        }
        bool planned = selected.TryPlan(displayWidth, displayHeight,
            OptimumConfig.UpscalerQuality, out UpscalerPlan plan);
        if (planned) allocatedUpscalePlan = plan;
        else if (OptimumConfig.UpscalerReplacesTaa)
            DisableUpscaler("the selected upscaler cannot plan a frame on this device");
        if (planned)
        {
            renderWidth = plan.RenderWidth;
            renderHeight = plan.RenderHeight;
            OptimumConfig.SetUpscalerPlan(plan.RenderScale, plan.LodBias);
        }
        return planned;
    }

    private void DisableUpscaler(string reason)
    {
        IUpscalerBackend? selected = SelectedUpscaler;
        if (OptimumConfig.DisableUpscalerAtRuntime())
            LogUpscaler("[Optimum] " + OptimumConfig.Upscaler + " unavailable: " + reason + "; using the ordinary render path.");
        try { selected?.RetireFeature(); }
        catch (Exception error) { LogUpscaler("[Optimum] " + selected!.Id + " teardown: " + error.Message); }
        allocatedUpscalePlan = default;
        upscaledThisFrame = false;
        OptimumConfig.ClearUpscalerPlan();
        rebuildUpscalerTargetsPending = true;
    }

    /// <summary>Returns the scene target only when this frame actually wrote it.</summary>
    private FrameBufferRef? UpscaledSceneTarget
    {
        get
        {
            List<FrameBufferRef> buffers = FrameBuffers;
            if (!upscaledThisFrame || buffers == null || buffers.Count <= OptimumUpscaledSceneIndex) return null;
            return buffers[OptimumUpscaledSceneIndex];
        }
    }

    private bool RenderUpscaler()
    {
        upscaledThisFrame = false;
        IUpscalerBackend? selected = SelectedUpscaler;
        if (selected?.Active != true) return false;
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || buffers.Count <= OptimumUpscaledSceneIndex || MotionAttachmentIndex < 0)
            return false;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef output = buffers[OptimumUpscaledSceneIndex];
        if (primary?.ColorTextureIds == null || output?.ColorTextureIds == null ||
            primary.ColorTextureIds.Length <= MotionAttachmentIndex || output.ColorTextureIds.Length == 0)
            return false;

        UpscalerPlan plan = allocatedUpscalePlan;
        var frame = new UpscalerFrame(primary.ColorTextureIds[0], primary.DepthTextureId,
            primary.ColorTextureIds[MotionAttachmentIndex], output.ColorTextureIds[0], OptimumTemporal.Context);
        if (!TryEvaluateUpscaler(selected, plan, frame, DisableUpscaler)) return false;
        if (lastEvaluatedUpscalePlan != plan)
        {
            lastEvaluatedUpscalePlan = plan;
            LogUpscaler("[Optimum] " + selected.Id + " " + plan.Quality +
                ": first successful upscale from " + plan.RenderWidth + "x" + plan.RenderHeight +
                " to " + plan.DisplayWidth + "x" + plan.DisplayHeight + ".");
        }
        ShaderRegistry.ApplyOptimumTerrainSamplerLodBias(OptimumConfig.EffectiveTerrainLodBias);

        // Late 3D overlays use the same depth as the upscaled scene. Depth formats
        // may lack blit-destination support, in which case clear to far rather than
        // testing overlays against stale geometry from a previous frame.
        if (output.DepthTextureId > 0 &&
            !device.UpscaleDepthNearest(primary.DepthTextureId, output.DepthTextureId))
        {
            device.ClearDepthImageToFar(output.DepthTextureId);
            if (!depthUpscaleRefusalLogged)
            {
                depthUpscaleRefusalLogged = true;
                LogUpscaler("[Optimum] Upscaler: depth blit unsupported; late 3D overlays have no scene occlusion.");
            }
        }
        upscaledThisFrame = true;
        return true;
    }

    internal static bool TryEvaluateUpscaler(IUpscalerBackend selected, in UpscalerPlan plan,
        in UpscalerFrame frame, Action<string> disable)
    {
        string? error;
        try
        {
            if (selected.Evaluate(plan, frame, out error)) return true;
        }
        catch (Exception exception)
        {
            error = selected.Id + " evaluation threw: " + exception.Message;
        }
        disable(error ?? "provider evaluation failed");
        return false;
    }

    private string UpscalerDataPath()
    {
        string path = CrashMarkerDataPath ?? GamePaths.DataPath;
        if (string.IsNullOrEmpty(path)) path = Path.GetTempPath();
        return Path.Combine(path, "ModConfig", "optimum-ngx");
    }

    private void LogUpscaler(string message) => Logger?.Notification(message);
}

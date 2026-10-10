using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

/// <summary>Retained provider registry/planning/evaluation under the new process owner.</summary>
internal sealed class RuntimeUpscalers(RendererSettingsState state, string dataPath, Action<string> log,
    Action requestTargetRebuild, Action<float> applyTerrainLodBias) : IDisposable
{
    private readonly Dictionary<string, IUpscalerBackend> providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> failures = new(StringComparer.OrdinalIgnoreCase);
    private VulkanDevice? device;
    private UpscalerPlan allocated, lastEvaluated;
    private bool depthRefusalLogged;
    internal string EvaluationReadiness { get; private set; } = "not evaluated";
    private Exception? lifetimeFailure;
    private void RequireLifetime()
    {
        if (lifetimeFailure != null)
            throw new InvalidOperationException("Upscaler release failed; cleanup is terminal and owners remain retained.", lifetimeFailure);
    }
    private void ReleaseBackend(IUpscalerBackend backend, bool shutdown)
    {
        RequireLifetime();
        try
        {
            if (shutdown) backend.Shutdown();
            else backend.RetireFeature();
        }
        catch (Exception error)
        {
            lifetimeFailure = error;
            throw new InvalidOperationException("VulkanStory " + backend.Id + " release failed; dependent cleanup stopped.", error);
        }
    }
    /// <summary>Registers SR backends and contributes their Vulkan device requirements before device initialization.</summary>
    /// <param name="target">Session-owned device receiving requirements and later provider execution.</param>
    /// <remarks>Can run once per registry; the registry borrows the device and owns its provider contexts.</remarks>
    internal void Prepare(VulkanDevice target)
    {
        RequireLifetime();
        if (device != null) throw new InvalidOperationException("Upscaler registry is already prepared.");
        device = target;
        DlssUpscaler? dlss = DlssUpscaler.TryPrepare(state, Path.Combine(dataPath, "ModConfig", "vulkanstory-ngx"), log, prepareForSwitching: true);
        if (dlss != null) providers.Add("dlss", new DlssBackend(dlss));
        providers.Add("xess", new XessBackend(log));
        providers.Add("fsr3", new Fsr3Backend(log));
        providers.Add("fsr4", new Fsr4Backend(log));
        Action<VulkanContextOptions>? previous = target.ConfigureContextOptions;
        target.ConfigureContextOptions = options =>
        {
            previous?.Invoke(options);
            options.RequirementContributors.Add(new XessFgInteropRequirements());
            foreach (IUpscalerBackend backend in providers.Values)
                if (backend.Requirements != null) options.RequirementContributors.Add(backend.Requirements);
        };
    }
    /// <summary>Initializes registered providers against the created Vulkan device and removes unsupported backends.</summary>
    /// <remarks>Initialization failures become provider refusal reasons. Checked shutdown failures propagate and retain ownership.</remarks>
    internal void BringUp()
    {
        RequireLifetime();
        var target = device ?? throw new InvalidOperationException("Upscaler registry was not prepared before device creation.");
        target.UpscalerHandles(out IntPtr instance, out IntPtr physical, out IntPtr logical);
        foreach (var backend in providers.Values.ToArray())
        {
            try
            {
                if (backend.BringUp(target, instance, physical, logical)) continue;
                failures[backend.Id] = backend.Unavailable ?? "provider initialization failed on this Vulkan device";
            }
            catch (Exception error)
            { failures[backend.Id] = error.Message; log("VulkanStory " + backend.Id + " bring-up: " + error.Message); }
            ReleaseBackend(backend, shutdown: true);
            providers.Remove(backend.Id);
        }
    }
    private IUpscalerBackend? Selected => providers.GetValueOrDefault(state.EffectiveUpscaler);
    internal string? Unavailable(string provider) => provider == "off" ? null : failures.TryGetValue(provider, out var reason)
        ? reason : state.IsDisabled(provider)
        ? "this provider was disabled for this session" : !providers.TryGetValue(provider, out var backend)
        ? "this provider is unavailable on this Vulkan device" : backend.Active ? null : backend.Unavailable;
    /// <summary>Plans render dimensions and LOD bias for the selected effective SR provider.</summary>
    /// <param name="width">Display width in pixels.</param>
    /// <param name="height">Display height in pixels.</param>
    /// <returns>The selected plan, or null after falling back when no eligible provider can plan.</returns>
    /// <remarks>Clears the prior active plan first; refusal retires the selected feature and requests target rebuild.</remarks>
    internal UpscalerPlan? Plan(int width, int height)
    {
        RequireLifetime();
        allocated = default;
        state.ClearActivePlan();
        var selected = Selected;
        if (selected == null)
        {
            if (state.UpscalerReplacesTaa) Disable(Unavailable(state.Settings.Upscaler) ?? "the selected provider is unavailable on this device");
            return null;
        }
        if (!selected.TryPlan(width, height, state.Quality, state.LodBiasOffset, out allocated))
        { if (state.UpscalerReplacesTaa) Disable(selected.Unavailable ?? "the selected upscaler cannot plan a frame on this device"); return null; }
        state.SetActivePlan(allocated.RenderScale, allocated.LodBias);
        return allocated;
    }
    /// <summary>Records a session refusal, retires the selected provider feature and requests ordinary target rebuilding.</summary>
    /// <param name="reason">Concrete provider failure displayed and logged for this session.</param>
    internal void Disable(string reason)
    {
        RequireLifetime();
        var selected = Selected;
        failures[state.Settings.Upscaler] = reason;
        if (state.DisableAtRuntime()) log("VulkanStory " + state.Settings.Upscaler + " unavailable: " + reason + "; using the ordinary render path.");
        if (selected != null) ReleaseBackend(selected, shutdown: false);
        allocated = default; state.ClearActivePlan(); requestTargetRebuild();
    }
    /// <summary>Retires current SR features and clears plans before the next settings-driven target allocation.</summary>
    /// <remarks>Provider drain/release failures are terminal for this owner and propagate before dependent cleanup.</remarks>
    internal void ApplySettings()
    {
        RequireLifetime();
        foreach (var backend in providers.Values) ReleaseBackend(backend, shutdown: false);
        allocated = lastEvaluated = default; state.ClearActivePlan();
        requestTargetRebuild(); applyTerrainLodBias(0);
    }
    /// <summary>Evaluates the selected provider using matching primary color/depth/motion and planned output targets.</summary>
    /// <param name="graphics">Borrowed adapter whose per-frame SR outcome is published.</param>
    /// <param name="buffers">Current session target list using retained framebuffer slot identities.</param>
    /// <param name="temporal">Camera, jitter and timing inputs for this rendered frame.</param>
    /// <returns>True after provider output succeeds; false when prerequisites or evaluation fail.</returns>
    /// <remarks>Evaluation refusal disables the provider. Unsupported late-overlay depth blit clears output depth to far and logs the limitation once.</remarks>
    internal bool Evaluate(GameGraphicsAdapter graphics, IReadOnlyList<Vintagestory.API.Client.FrameBufferRef> buffers,
        in TemporalProviderFrame temporal)
    {
        RequireLifetime();
        graphics.UpscaledThisFrame = false;
        var selected = Selected;
        int motion = graphics.FrameState.MotionAttachment;
        if (selected?.Active != true)
        { EvaluationReadiness = Unavailable(state.Settings.Upscaler) ?? "upscaler not selected"; return false; }
        if (buffers.Count <= GameGraphicsAdapter.UpscaledSceneIndex || motion < 0)
        { EvaluationReadiness = "upscaler output or motion target unavailable"; return false; }
        var primary = buffers[0]; var output = buffers[GameGraphicsAdapter.UpscaledSceneIndex];
        if (primary?.ColorTextureIds is not { } colors || output?.ColorTextureIds is not { Length: > 0 } || colors.Length <= motion)
        { EvaluationReadiness = "upscaler colour, motion or output target unavailable"; return false; }
        int composition = graphics.UsesFsr3Inputs && graphics.Fsr3HasComposition &&
            buffers.Count > 1 && buffers[1]?.ColorTextureIds is { Length: > 1 } transparency
            ? transparency[1] : 0;
        var frame = new UpscalerFrame(colors[0], primary.DepthTextureId, colors[motion], output.ColorTextureIds[0], temporal,
            composition, graphics.Fsr3HasComposition);
        string? error;
        try
        {
            if (!selected.Evaluate(allocated, frame, out error))
            { EvaluationReadiness = error ?? "provider evaluation failed"; Disable(EvaluationReadiness); return false; }
        }
        catch (Exception failure)
        { EvaluationReadiness = selected.Id + " evaluation threw: " + failure.Message; Disable(EvaluationReadiness); return false; }
        if (lastEvaluated != allocated)
        {
            lastEvaluated = allocated;
            log("VulkanStory " + selected.Id + " " + allocated.Quality + ": first successful upscale from " +
                allocated.RenderWidth + "x" + allocated.RenderHeight + " to " + allocated.DisplayWidth + "x" + allocated.DisplayHeight + ".");
        }
        applyTerrainLodBias(state.ActiveLodBias);
        if (output.DepthTextureId > 0 && !device!.UpscaleDepthNearest(primary.DepthTextureId, output.DepthTextureId))
        {
            device.ClearDepthImageToFar(output.DepthTextureId);
            if (!depthRefusalLogged) { depthRefusalLogged = true; log("VulkanStory: depth blit unsupported; late overlays have no scene occlusion."); }
        }
        graphics.UpscaledThisFrame = true; EvaluationReadiness = "ready"; return true;
    }
    /// <summary>Shuts down owned SR providers with checked release and clears the active plan.</summary>
    /// <remarks>The borrowed device is not disposed here; release failure retains the remaining provider owners.</remarks>
    public void Dispose()
    {
        RequireLifetime();
        foreach (var backend in providers.Values.ToArray())
        {
            ReleaseBackend(backend, shutdown: true);
            providers.Remove(backend.Id);
        }
        allocated = lastEvaluated = default; state.ClearActivePlan(); device = null;
    }
}

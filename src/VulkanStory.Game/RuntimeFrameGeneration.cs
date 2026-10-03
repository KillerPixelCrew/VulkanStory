using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal readonly record struct DlssQueryObservation(ulong QueryFrameId, int Result,
    StreamlineFrameGenerationState? State);

// Direct host migration from porting/old-platform/VulkanClientPlatform.FrameGeneration.cs.
// Baseline: 386e0d05386d0b228b439d09aeca851428f7bbf3. SDK wrappers,
// upright image formats, camera conversion and presentation ownership are retained.
internal sealed class RuntimeFrameGeneration(VulkanDevice device, RendererSettingsState settings,
    Action<string> notification, Action<string> error,
    Action requestTemporalReset) : IDisposable
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private string activeProvider = "off";
    private Fsr3FrameGeneration? fsr3;
    private ulong renderedFrames;
    private uint actualDlssPresents, configuredDlssCount;
    private int motionRg, motionWidth, motionHeight;
    private int fsrScene, fsrUi, fsrDepth, fsrMotion;
    private int dlssScene, dlssUi, dlssDepth, dlssMotion;
    private int dlssWidth, dlssHeight, dlssRenderWidth, dlssRenderHeight;
    private readonly Dictionary<string, string> failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> waits = new(StringComparer.Ordinal);
    private bool disposed;
    private string preparationStatus = "off";

    internal string RequestedProvider => settings.Settings.FrameGeneration;
    internal string EffectiveProvider => failed.ContainsKey(activeProvider) ? "off" : activeProvider;
    // Prepared describes current inputs, not actual SDK interpolation/presentation.
    internal bool PreparedThisFrame { get; private set; }
    internal string PreparationStatus => PreparedThisFrame ? "current inputs prepared" : preparationStatus;
    internal uint ConfiguredDlssGeneratedFrames => configuredDlssCount;
    internal uint ActualDlssPresents => actualDlssPresents;
    internal StreamlineFrameGenerationState? LastDlssState { get; private set; }
    internal int? LastDlssStateResult { get; private set; }
    internal DlssQueryObservation? HeadlessDlssQueryObservation { get; private set; }
    internal string? Unavailable(string provider) => failed.GetValueOrDefault(provider);

    internal void Generate(GameGraphicsAdapter graphics, IReadOnlyList<FrameBufferRef> buffers,
        in GameTemporalFrame frame)
    {
        RequireOwner();
        HeadlessDlssQueryObservation = null;
        PreparedThisFrame = false;
        string provider = RequestedProvider;
        if (provider != activeProvider)
        {
            Reset();
            activeProvider = provider;
            LastDlssState = null;
            LastDlssStateResult = null;
            preparationStatus = provider == "off" ? "off" : "selected " + provider;
            device.SetFrameGenerationPresentation(provider);
            failed.Remove(provider);
            waits.Clear();
            requestTemporalReset();
            notification("VulkanStory: frame generation provider selected: " + provider);
            if (provider == "fsr3" && settings.Settings.FrameGenerationMultiplier > 2)
                notification("VulkanStory: FSR 3 Vulkan supports 2×; the requested multiplier applies to multi-frame providers.");
        }
        device.FrameGenerationMultiplier = settings.Settings.FrameGenerationMultiplier;
        if (provider == "off" || failed.ContainsKey(provider)) return;
        // Menu/loading/pause and missing current producers never reuse old tags.
        if (!frame.CanGenerate || !frame.MotionValid || !frame.HasCamera ||
            frame.FrameId != device.LatencyFrameId)
        { Wait("complete current world camera and motion inputs"); return; }
        if (provider == "dlss" && !device.StreamlineFrameGenerationAvailable)
        { Disable(provider, "Streamline DLSS-G and Reflex are unavailable"); return; }
        if (provider == "dlss" && !device.StreamlineFrameGenerationReady)
        { Wait("the Streamline swapchain rebuild"); return; }
        if (provider == "fsr3" && device.Fsr3ProxyFailure is string fsrFailure)
        { Disable(provider, fsrFailure); return; }
        if (provider == "fsr3" && !device.Fsr3ProxyReady)
        { Wait("the FidelityFX swapchain rebuild"); return; }
        if (provider == "xess" && device.XessProxyFailure is string xessFailure)
        { Disable(provider, xessFailure); return; }
        if (provider is not ("dlss" or "xess") && !device.CanPresentGeneratedFrame)
        { Wait("a presentable frame-generation-compatible swapchain"); return; }
        if (provider is not ("fsr3" or "dlss" or "xess"))
        { Disable(provider, "unsupported presentation path"); return; }

        int motion = graphics.FrameState.MotionAttachment;
        FrameBufferRef? primary = Target(buffers, 0);
        FrameBufferRef? output = Target(buffers, GameGraphicsAdapter.GeneratedFrameIndex);
        if (motion < 0 || primary?.ColorTextureIds is not { } colors || colors.Length <= motion ||
            output?.ColorTextureIds is not { Length: > 0 } || primary.DepthTextureId <= 0)
        { Wait("primary depth, motion and output images"); Reset(); return; }
        FrameBufferRef? scene = graphics.SceneNoHudCaptured
            ? Target(buffers, graphics.SceneNoHudFramebufferIndex) : null;
        FrameBufferRef? ui = Target(buffers, graphics.UiFramebufferIndex);
        if (scene?.ColorTextureIds is not { Length: > 0 } || ui?.ColorTextureIds is not { Length: > 0 } ||
            scene.Width != output.Width || scene.Height != output.Height ||
            ui.Width != output.Width || ui.Height != output.Height)
        { Wait("matching HUD-less scene and UI images"); return; }
        if (!TryGetCamera(frame, output.Width, output.Height, out NgxFrameGenerationCamera camera))
        { Wait("valid world camera matrices"); return; }

        try
        {
            if (provider == "dlss") GenerateDlss(primary, scene, ui, output, motion, frame, camera);
            else if (provider == "xess") GenerateXess(primary, scene, ui, motion, frame);
            else GenerateFsr3(primary, scene, ui, output, motion, frame);
        }
        catch (Exception failure) { Disable(provider, "frame preparation threw: " + failure.Message); }
    }

    private void GenerateFsr3(FrameBufferRef primary, FrameBufferRef scene, FrameBufferRef ui,
        FrameBufferRef output, int motion, in GameTemporalFrame frame)
    {
        if (fsr3 == null || fsr3.Width != (uint)output.Width || fsr3.Height != (uint)output.Height ||
            motionWidth != primary.Width || motionHeight != primary.Height)
        {
            Reset();
            int create = Fsr3FrameGeneration.Create(device, (uint)output.Width, (uint)output.Height, out fsr3);
            if (create != 0 || fsr3 == null)
            { Disable("fsr3", "FidelityFX context creation failed (" + create + ")"); return; }
            motionRg = device.CreateUpscaleTexture(primary.Width, primary.Height, Format.R16G16Sfloat, storage: false);
            motionWidth = primary.Width; motionHeight = primary.Height;
            fsrScene = device.CreateUpscaleTexture(output.Width, output.Height, Format.R8G8B8A8Unorm, storage: false);
            fsrUi = device.CreateUpscaleTexture(output.Width, output.Height, Format.R8G8B8A8Unorm, storage: false);
            fsrDepth = device.CreateUpscaleTexture(primary.Width, primary.Height, Format.D32Sfloat, storage: false);
            fsrMotion = device.CreateUpscaleTexture(primary.Width, primary.Height, Format.R16G16Sfloat, storage: false);
        }
        int hudless = scene.ColorTextureIds[0];
        int result = fsr3.Evaluate(device, hudless, primary.DepthTextureId, primary.ColorTextureIds[motion],
            motionRg, hudless, ui.ColorTextureIds[0], fsrScene, fsrUi, fsrDepth, fsrMotion, frame.Provider);
        if (result != 0)
        { Disable("fsr3", "FidelityFX frame interpolation preparation failed (" + result + ")"); return; }
        // The SDK proxy owns interpolation and UI composition at the real Present.
        PreparedThisFrame = true;
    }

    private void GenerateDlss(FrameBufferRef primary, FrameBufferRef scene, FrameBufferRef ui,
        FrameBufferRef output, int motion, in GameTemporalFrame frame, in NgxFrameGenerationCamera camera)
    {
        if (dlssScene == 0 || dlssWidth != output.Width || dlssHeight != output.Height ||
            dlssRenderWidth != primary.Width || dlssRenderHeight != primary.Height)
        {
            Reset();
            dlssWidth = output.Width; dlssHeight = output.Height;
            dlssRenderWidth = primary.Width; dlssRenderHeight = primary.Height;
            dlssScene = device.CreateUpscaleTexture(output.Width, output.Height, Format.R8G8B8A8Unorm, storage: false);
            dlssUi = device.CreateUpscaleTexture(output.Width, output.Height, Format.R8G8B8A8Unorm, storage: false);
            dlssDepth = device.CreateUpscaleTexture(primary.Width, primary.Height, Format.D32Sfloat, storage: false);
            dlssMotion = device.CreateUpscaleTexture(primary.Width, primary.Height, Format.R16G16B16A16Sfloat, storage: false);
        }
        int presentError = device.TakeStreamlinePresentError();
        if (presentError is (int)Result.ErrorOutOfDateKhr or (int)Result.SuboptimalKhr)
        { device.RebuildStreamlineSwapchain(); Wait("a swapchain resize"); return; }
        if (presentError != 0)
        { Disable("dlss", "Streamline present failed with Vulkan result " + presentError); return; }
        // Query after a complete tagged frame; the first pre-tag state can be invalid.
        uint maxGenerated = 1;
        if (renderedFrames > 0)
        {
            int stateResult = device.GetStreamlineFrameGenerationStateDetails(out var state);
            HeadlessDlssQueryObservation = new DlssQueryObservation(frame.FrameId, stateResult,
                stateResult == 0 ? state : null);
            LastDlssStateResult = stateResult;
            LastDlssState = stateResult == 0 ? state : null;
            if (stateResult != 0 || state.Status != 0)
            { Disable("dlss", "Streamline state query/status: " + stateResult + "/" + state.Status); return; }
            maxGenerated = state.MaximumGenerated;
            uint presented = state.Presents;
            actualDlssPresents += presented;
            VulkanStats.NoteSdkActualPresents(presented);
            if (maxGenerated == 0) { Wait("a nonzero SDK generated-frame limit"); return; }
            if (output.Width < state.MinimumSize || output.Height < state.MinimumSize)
            { Wait("output dimensions at least " + state.MinimumSize + " pixels"); return; }
            if (device.PresentationVsyncEnabled && state.VsyncSupport != 1)
            { Wait("VSync to be disabled for this DLSS-G runtime"); return; }
            if (renderedFrames % 120 == 0)
                notification("VulkanStory: DLSS-G SDK reports " + actualDlssPresents + " frames actually presented over " + renderedFrames + " rendered frames.");
        }
        int result = device.TagStreamlineFrame(primary.DepthTextureId, primary.ColorTextureIds[motion],
            scene.ColorTextureIds[0], ui.ColorTextureIds[0], dlssDepth, dlssMotion, dlssScene, dlssUi,
            camera, frame.Provider.Reset || renderedFrames == 0);
        if (result != 0)
        { Disable("dlss", "Streamline frame tagging failed (" + result + ")"); return; }
        uint count = Math.Min((uint)Math.Clamp(settings.Settings.FrameGenerationMultiplier - 1, 1, 5), maxGenerated);
        if (count != configuredDlssCount)
            notification("VulkanStory: DLSS-G requested " + settings.Settings.FrameGenerationMultiplier + "×, effective " + (count + 1) + "× (SDK maximum " + (maxGenerated + 1) + "×).");
        // The bridge caches unchanged options; calling after tags also resumes a suspended frame.
        result = device.SetStreamlineFrameGeneration(true, count);
        if (result != 0)
        { Disable("dlss", "Streamline DLSS-G options failed (" + result + ")"); return; }
        configuredDlssCount = count;
        renderedFrames++;
        PreparedThisFrame = true;
    }

    private unsafe void GenerateXess(FrameBufferRef primary, FrameBufferRef scene, FrameBufferRef ui,
        int motion, in GameTemporalFrame frame)
    {
        if (motionRg == 0 || motionWidth != primary.Width || motionHeight != primary.Height)
        {
            Reset();
            motionRg = device.CreateUpscaleTexture(primary.Width, primary.Height, Format.R16G16Sfloat, storage: false);
            motionWidth = primary.Width; motionHeight = primary.Height;
        }
        var constants = new XessPresentationFrame
        {
            FrameId = (uint)frame.FrameId,
            Reset = frame.Provider.Reset || renderedFrames == 0 ? 1u : 0u,
            JitterX = frame.Provider.JitterX, JitterY = -frame.Provider.JitterY,
            MotionScaleX = 1f, MotionScaleY = -1f,
        };
        ReadOnlySpan<float> view = frame.View.Span, projection = frame.Projection.Span;
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
        {
            constants.ViewMatrix[row * 4 + column] = view[column * 4 + row];
            constants.ProjectionMatrix[row * 4 + column] = projection[column * 4 + row];
        }
        int result = device.PrepareXessFrame(primary.DepthTextureId, primary.ColorTextureIds[motion],
            motionRg, scene.ColorTextureIds[0], ui.ColorTextureIds[0], constants);
        if (result != 0)
        { Disable("xess", "XeSS-FG frame preparation failed (" + result + ")"); return; }
        renderedFrames++;
        PreparedThisFrame = true;
    }

    internal static bool TryGetCamera(in GameTemporalFrame frame, int width, int height,
        out NgxFrameGenerationCamera camera)
    {
        camera = default;
        if (!frame.HasCamera || width <= 0 || height <= 0) return false;
        // Same GL [-w,w] to Vulkan [0,w] clip-depth remap as the shader rewriter.
        float[] depthRemap = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, .5f, 0f, 0f, 0f, .5f, 1f];
        float[] projection = Mat4f.Mul(new float[16], depthRemap, frame.Projection.ToArray());
        float[] inverseProjection = Mat4f.Invert(new float[16], projection);
        float[] view = frame.View.ToArray();
        float[] inverseView = Mat4f.Invert(new float[16], view);
        if (inverseProjection == null || inverseView == null) return false;
        float[] currentViewProj = Mat4f.Mul(new float[16], projection, view);
        float[] inverseCurrent = Mat4f.Invert(new float[16], currentViewProj);
        if (inverseCurrent == null) return false;
        float[] previousProjection = Mat4f.Mul(new float[16], depthRemap, frame.PreviousProjection.ToArray());
        float[] previousViewProj = Mat4f.Mul(new float[16], previousProjection, frame.PreviousView.ToArray());
        float[] toPrevious = Mat4f.Mul(new float[16], previousViewProj, inverseCurrent);
        float[] fromPrevious = Mat4f.Invert(new float[16], toPrevious);
        if (fromPrevious == null) return false;
        var temporal = frame.Provider;
        camera = new NgxFrameGenerationCamera(projection, inverseProjection, toPrevious, fromPrevious,
            temporal.NearPlane, temporal.FarPlane, temporal.FovRadians, (float)width / height,
            temporal.JitterX, temporal.JitterY, temporal.PlayerPositionX, temporal.PlayerPositionY,
            temporal.PlayerPositionZ, inverseView[4], inverseView[5], inverseView[6],
            inverseView[0], inverseView[1], inverseView[2], -inverseView[8], -inverseView[9], -inverseView[10]);
        return camera.IsValid;
    }

    private static FrameBufferRef? Target(IReadOnlyList<FrameBufferRef> targets, int index) =>
        index >= 0 && index < targets.Count && targets[index] is { Disposed: false } target ? target : null;
    private void Disable(string provider, string reason)
    {
        if (failed.TryAdd(provider, reason)) error("VulkanStory: " + provider + " frame generation unavailable: " + reason);
        Reset();
        preparationStatus = "unavailable: " + reason;
        device.SetFrameGenerationPresentation("off");
    }
    private void Wait(string reason)
    {
        PreparedThisFrame = false;
        preparationStatus = "waiting for " + reason;
        if (activeProvider == "dlss") device.SuspendStreamlineFrameGeneration();
        if (activeProvider == "xess") device.SuspendXessFrameGeneration();
        if (activeProvider == "fsr3" && fsr3 != null)
        {
            int result = fsr3.Disable(device.Fsr3ProxyContext);
            if (result != 0)
                throw new InvalidOperationException("FidelityFX frame generation suspension/drain failed (" + result + ").");
        }
        if (waits.Add(reason)) notification("VulkanStory: frame generation waiting for " + reason);
    }
    internal void Reset()
    {
        RequireOwner();
        // Stop consumers before retiring their input images on the Vulkan timeline.
        if (activeProvider == "dlss") device.SuspendStreamlineFrameGeneration();
        if (activeProvider == "xess") device.SuspendXessFrameGeneration();
        device.ResetGeneratedFramePresent();
        PreparedThisFrame = false;
        renderedFrames = 0; actualDlssPresents = configuredDlssCount = 0;
        LastDlssState = null;
        LastDlssStateResult = null;
        if (fsr3 != null)
        {
            int result = fsr3.Disable(device.Fsr3ProxyContext);
            if (result != 0)
                throw new InvalidOperationException("FidelityFX frame generation disable/drain failed (" + result + "); inputs retained.");
            device.RetireUpscalerResource(fsr3);
            fsr3 = null;
        }
        foreach (int texture in new[] { motionRg, fsrScene, fsrUi, fsrDepth, fsrMotion, dlssScene, dlssUi, dlssDepth, dlssMotion })
            if (texture > 0) device.DeleteTexture(texture);
        motionRg = fsrScene = fsrUi = fsrDepth = fsrMotion = dlssScene = dlssUi = dlssDepth = dlssMotion = 0;
        motionWidth = motionHeight = dlssWidth = dlssHeight = dlssRenderWidth = dlssRenderHeight = 0;
    }
    private void RequireOwner()
    {
        if (disposed) throw new ObjectDisposedException(nameof(RuntimeFrameGeneration));
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Frame generation requires the render session owner thread.");
    }
    public void Dispose()
    {
        if (disposed) return;
        Reset();
        device.SetFrameGenerationPresentation("off");
        disposed = true;
    }
}

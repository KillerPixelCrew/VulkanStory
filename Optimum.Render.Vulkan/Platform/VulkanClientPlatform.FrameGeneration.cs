using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

public partial class VulkanClientPlatform
{
    internal const int OptimumGeneratedFrameIndex = 25;
    private string activeFrameGenerationProvider = "off";
    private Fsr3FrameGeneration? fsr3FrameGeneration;
    private ulong dlssFrameCount;
    private uint dlssActuallyPresented;
    private int frameGenerationMotion;
    private int fsr3UprightScene;
    private int fsr3UprightUi;
    private int fsr3UprightDepth;
    private int fsr3UprightMotion;
    private int dlssUprightScene;
    private int dlssUprightUi;
    private int dlssUprightDepth;
    private int dlssUprightMotion;
    private int dlssUprightWidth, dlssUprightHeight;
    private int dlssUprightRenderWidth, dlssUprightRenderHeight;
    private readonly HashSet<string> failedFrameGenerationProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> frameGenerationWaitsLogged = new(StringComparer.Ordinal);

    private void AllocateFrameGenerationTarget(List<FrameBufferRef> buffers, int width, int height)
    {
        try
        {
            buffers[OptimumGeneratedFrameIndex] = CreateOptimumOwnedTarget(width, height,
                withDepth: false, storage: true, frameGenerationStorage: true);
        }
        catch (Exception error)
        {
            Logger.Error("Optimum: FSR 3 frame generation target: {0}", error.Message);
        }
    }

    private void TryGenerateFrame()
    {
        string provider = OptimumConfig.EffectiveFrameGeneration;
        if (provider != activeFrameGenerationProvider)
        {
            if (activeFrameGenerationProvider == "dlss") device?.SuspendStreamlineFrameGeneration();
            ResetFrameGeneration();
            activeFrameGenerationProvider = provider;
            device?.SetFrameGenerationPresentation(provider);
            failedFrameGenerationProviders.Remove(provider);
            frameGenerationWaitsLogged.Clear();
            OptimumTemporal.RequestReset(EnumTemporalResetReason.Toggle);
            Logger.Notification("Optimum: frame generation provider selected: {0}", provider);
        }
        if (provider == "off" || failedFrameGenerationProviders.Contains(provider) || device == null) return;
        if (provider == "dlss" && !device.StreamlineFrameGenerationAvailable)
        {
            DisableFrameGeneration(provider, "Streamline DLSS-G and Reflex are unavailable");
            return;
        }
        if (provider == "dlss" && !device.StreamlineFrameGenerationReady)
        {
            NoteFrameGenerationWait("the Streamline swapchain rebuild");
            return;
        }
        if (provider == "fsr3" && device.Fsr3ProxyFailure is string proxyFailure)
        {
            DisableFrameGeneration(provider, proxyFailure);
            return;
        }
        if (provider == "fsr3" && !device.Fsr3ProxyReady)
        {
            NoteFrameGenerationWait("the FidelityFX swapchain rebuild");
            return;
        }
        if (provider == "xess" && device.XessProxyFailure is string xessFailure)
        {
            DisableFrameGeneration(provider, xessFailure);
            return;
        }
        if (provider is not ("dlss" or "xess") && !device.CanPresentGeneratedFrame)
        {
            NoteFrameGenerationWait("a presentable swapchain with a frame-generation-compatible present mode");
            return;
        }
        if (provider is not ("fsr3" or "dlss" or "xess"))
        {
            DisableFrameGeneration(provider, provider + " frame generation is not ready on this presentation path");
            return;
        }
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || buffers.Count <= OptimumGeneratedFrameIndex ||
            MotionAttachmentIndex < 0)
        {
            NoteFrameGenerationWait("frame targets and motion vectors");
            return;
        }
        FrameBufferRef primary = buffers[0];
        FrameBufferRef output = buffers[OptimumGeneratedFrameIndex];
        if (primary?.ColorTextureIds == null || output?.ColorTextureIds == null ||
            primary.ColorTextureIds.Length <= MotionAttachmentIndex || output.ColorTextureIds.Length == 0 ||
            primary.DepthTextureId <= 0)
        {
            NoteFrameGenerationWait("primary depth, motion, and output images");
            ResetFrameGeneration();
            return;
        }

        FrameBufferRef? readyScene = SceneNoHudCaptured ? SceneNoHudFrameBuffer : null;
        FrameBufferRef? readyUi = UiTargetFrameBuffer;
        if (readyScene?.ColorTextureIds == null || readyUi?.ColorTextureIds == null ||
            readyScene.Width != output.Width || readyScene.Height != output.Height ||
            readyUi.Width != output.Width || readyUi.Height != output.Height)
        {
            NoteFrameGenerationWait("matching HUD-less scene and UI images");
            return;
        }
        if (!TryGetDlssCamera(OptimumTemporal.Context, output.Width, output.Height, out _))
        {
            NoteFrameGenerationWait("valid world camera matrices");
            return;
        }

        if (provider == "dlss")
        {
            TryGenerateStreamlineDlss(primary, readyScene, readyUi, output);
            return;
        }

        if (provider == "xess")
        {
            TryGenerateXess(primary, readyScene, readyUi, output);
            return;
        }

        if (fsr3FrameGeneration == null || fsr3FrameGeneration.Width != (uint)output.Width ||
            fsr3FrameGeneration.Height != (uint)output.Height)
        {
            ResetFrameGeneration();
            int create = Fsr3FrameGeneration.Create(device, (uint)output.Width, (uint)output.Height,
                out fsr3FrameGeneration);
            if (create != 0 || fsr3FrameGeneration == null)
            {
                DisableFrameGeneration(provider, "FidelityFX context creation failed (" + create + ")");
                return;
            }
            frameGenerationMotion = device.CreateUpscaleTexture(primary.Width, primary.Height,
                Format.R16G16Sfloat, storage: false);
            fsr3UprightScene = device.CreateUpscaleTexture(output.Width, output.Height,
                Format.R8G8B8A8Unorm, storage: false);
            fsr3UprightUi = device.CreateUpscaleTexture(output.Width, output.Height,
                Format.R8G8B8A8Unorm, storage: false);
            fsr3UprightDepth = device.CreateUpscaleTexture(primary.Width, primary.Height,
                Format.D32Sfloat, storage: false);
            fsr3UprightMotion = device.CreateUpscaleTexture(primary.Width, primary.Height,
                Format.R16G16Sfloat, storage: false);
        }

        int hudlessId = readyScene.ColorTextureIds[0];
        int result = fsr3FrameGeneration.Evaluate(device, hudlessId,
            primary.DepthTextureId, primary.ColorTextureIds[MotionAttachmentIndex],
            frameGenerationMotion, hudlessId, readyUi.ColorTextureIds[0],
            fsr3UprightScene, fsr3UprightUi, fsr3UprightDepth, fsr3UprightMotion,
            OptimumTemporal.Context);
        if (result != 0)
        {
            DisableFrameGeneration(provider, "FidelityFX frame interpolation failed (" + result + ")");
            return;
        }

        // FidelityFX's proxy uses the prepared inputs at the real Present call.
        // Its callback owns interpolation and UI composition on SDK queues.
    }

    private void TryGenerateStreamlineDlss(FrameBufferRef primary, FrameBufferRef hudless,
        FrameBufferRef ui, FrameBufferRef output)
    {
        if (dlssUprightScene == 0 || dlssUprightWidth != output.Width ||
            dlssUprightHeight != output.Height ||
            dlssUprightRenderWidth != primary.Width ||
            dlssUprightRenderHeight != primary.Height)
        {
            device.SuspendStreamlineFrameGeneration();
            ResetFrameGeneration();
            dlssUprightWidth = output.Width;
            dlssUprightHeight = output.Height;
            dlssUprightRenderWidth = primary.Width;
            dlssUprightRenderHeight = primary.Height;
            dlssUprightScene = device.CreateUpscaleTexture(output.Width, output.Height,
                Format.R8G8B8A8Unorm, storage: false);
            dlssUprightUi = device.CreateUpscaleTexture(output.Width, output.Height,
                Format.R8G8B8A8Unorm, storage: false);
            dlssUprightDepth = device.CreateUpscaleTexture(primary.Width, primary.Height,
                Format.D32Sfloat, storage: false);
            dlssUprightMotion = device.CreateUpscaleTexture(primary.Width, primary.Height,
                Format.R16G16B16A16Sfloat, storage: false);
        }
        if (!TryGetDlssCamera(OptimumTemporal.Context, output.Width, output.Height,
            out NgxFrameGenerationCamera camera))
        {
            NoteFrameGenerationWait("valid world camera matrices");
            return;
        }
        int presentError = device.TakeStreamlinePresentError();
        if (presentError is (int)Result.ErrorOutOfDateKhr or (int)Result.SuboptimalKhr)
        {
            // Streamline reports the actual asynchronous Vulkan Present result through
            // this callback. A resize is recoverable and must not blacklist DLSS-G.
            device.RebuildStreamlineSwapchain();
            NoteFrameGenerationWait("a swapchain resize");
            return;
        }
        if (presentError != 0)
        {
            DisableFrameGeneration("dlss", "Streamline present failed with Vulkan result " + presentError);
            return;
        }
        if (dlssFrameCount > 0)
        {
            int stateResult = device.GetStreamlineFrameGenerationState(
                out uint status, out uint presented);
            if (stateResult != 0)
            {
                DisableFrameGeneration("dlss", "Streamline state query failed (" + stateResult + ")");
                return;
            }
            if (status != 0)
            {
                DisableFrameGeneration("dlss", "Streamline DLSS-G status " + status);
                return;
            }
            dlssActuallyPresented += presented;
            VulkanStats.NoteSdkActualPresents(presented);
            if (dlssFrameCount % 120 == 0)
                Logger.Notification("Optimum: DLSS-G SDK reports {0} frames actually presented over {1} rendered frames",
                    dlssActuallyPresented, dlssFrameCount);
        }
        int result = device.TagStreamlineFrame(primary.DepthTextureId,
            primary.ColorTextureIds[MotionAttachmentIndex], hudless.ColorTextureIds[0],
            ui.ColorTextureIds[0], dlssUprightDepth, dlssUprightMotion,
            dlssUprightScene, dlssUprightUi,
            camera, OptimumTemporal.Context.Reset || dlssFrameCount == 0);
        if (result != 0)
        {
            DisableFrameGeneration("dlss", "Streamline frame tagging failed (" + result + ")");
            return;
        }
        result = device.SetStreamlineFrameGeneration(true);
        if (result != 0)
        {
            DisableFrameGeneration("dlss", "Streamline DLSS-G options failed (" + result + ")");
            return;
        }
        dlssFrameCount++;
    }

    private unsafe void TryGenerateXess(FrameBufferRef primary, FrameBufferRef hudless,
        FrameBufferRef ui, FrameBufferRef output)
    {
        if (frameGenerationMotion == 0)
            frameGenerationMotion = device.CreateUpscaleTexture(primary.Width,
                primary.Height, Format.R16G16Sfloat, storage: false);
        IOptimumTemporalContext temporal = OptimumTemporal.Context;
        var frame = new XessPresentationFrame
        {
            FrameId = (uint)device.LatencyFrameId,
            Reset = temporal.Reset || dlssFrameCount == 0 ? 1u : 0u,
            JitterX = temporal.JitterPx.X,
            JitterY = -temporal.JitterPx.Y,
            MotionScaleX = 1f,
            MotionScaleY = -1f,
        };
        float[] view = temporal.CameraMatrixOrigin;
        float[] projection = temporal.GetProjection(EnumTemporalView.World);
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
        {
            frame.ViewMatrix[row * 4 + column] = view[column * 4 + row];
            frame.ProjectionMatrix[row * 4 + column] = projection[column * 4 + row];
        }
        int result = device.PrepareXessFrame(primary.DepthTextureId,
            primary.ColorTextureIds[MotionAttachmentIndex], frameGenerationMotion,
            hudless.ColorTextureIds[0], ui.ColorTextureIds[0], frame);
        if (result != 0)
        {
            DisableFrameGeneration("xess", "XeSS-FG frame preparation failed (" + result + ")");
            return;
        }
        dlssFrameCount++;
    }

    private static bool TryGetDlssCamera(IOptimumTemporalContext frame, int width, int height,
        out NgxFrameGenerationCamera camera)
    {
        camera = default;
        if (!frame.IsViewCaptured(EnumTemporalView.World)) return false;
        // ShaderRewriter remaps GL clip depth to Vulkan [0, w] after the game's
        // projection. Streamline sees that Vulkan depth image, so its camera
        // matrices must describe the same clip space. The present blit already
        // makes the GL clip-Y convention upright in the final image.
        float[] depthRemap =
        {
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 0.5f, 0f,
            0f, 0f, 0.5f, 1f,
        };
        float[] projection = Mat4f.Mul(new float[16], depthRemap,
            frame.GetProjection(EnumTemporalView.World));
        float[] inverseProjection = Mat4f.Invert(new float[16], projection);
        float[] inverseView = Mat4f.Invert(new float[16], frame.CameraMatrixOrigin);
        if (inverseProjection == null || inverseView == null) return false;
        float[] currentViewProj = Mat4f.Mul(new float[16], projection, frame.CameraMatrixOrigin);
        float[] inverseCurrent = Mat4f.Invert(new float[16], currentViewProj);
        if (inverseCurrent == null) return false;
        float[] previousProjection = Mat4f.Mul(new float[16], depthRemap,
            frame.GetPrevProjection(EnumTemporalView.World));
        float[] previousViewProj = Mat4f.Mul(new float[16],
            previousProjection, frame.PrevCameraMatrixOrigin);
        float[] toPrevious = Mat4f.Mul(new float[16], previousViewProj, inverseCurrent);
        float[] fromPrevious = Mat4f.Invert(new float[16], toPrevious);
        if (fromPrevious == null) return false;
        camera = new NgxFrameGenerationCamera(projection, inverseProjection, toPrevious,
            fromPrevious, frame.ZNear, frame.ZFar, frame.Fov,
            (float)width / height, frame.JitterPx.X, frame.JitterPx.Y,
            frame.Playerpos.X, frame.Playerpos.Y, frame.Playerpos.Z,
            inverseView[4], inverseView[5], inverseView[6],
            inverseView[0], inverseView[1], inverseView[2],
            -inverseView[8], -inverseView[9], -inverseView[10]);
        return camera.IsValid;
    }

    private bool CompositeGeneratedUi(FrameBufferRef output, FrameBufferRef ui)
    {
        ShaderProgram compose = ShaderPrograms.UiCompose;
        if (compose == null || compose.LoadError || compose.ProgramId <= 0) return false;
        int uiColor = ui.ColorTextureIds[0];
        NativePipeline? pipeline = NativePipelineFor(nativeUiCompose, compose, output.FboId,
            PremultipliedColorZero());
        if (pipeline == null || !BeginNativeBlitPass("UiCompose/Generated", output.FboId,
            output.Width, output.Height, new[] { uiColor })) return false;
        bool drawn = device.DrawNativeFullscreen(pipeline,
            new[] { new NativeTexture(nativeUiCompose.Samplers[0], uiColor) });
        device.EndNativePass();
        return drawn;
    }

    private void DisableFrameGeneration(string provider, string reason)
    {
        if (failedFrameGenerationProviders.Add(provider))
            Logger.Error("Optimum: {0} frame generation unavailable: {1}", provider, reason);
        if (provider == "dlss") device?.SuspendStreamlineFrameGeneration();
        if (provider == "xess") device?.SetFrameGenerationPresentation("off");
        ResetFrameGeneration();
    }

    private void NoteFrameGenerationWait(string reason)
    {
        if (activeFrameGenerationProvider == "dlss") device?.SuspendStreamlineFrameGeneration();
        if (activeFrameGenerationProvider == "xess") device?.SuspendXessFrameGeneration();
        if (activeFrameGenerationProvider == "fsr3" && fsr3FrameGeneration != null)
        {
            int disabled = fsr3FrameGeneration.Disable(device?.Fsr3ProxyContext ?? 0);
            if (disabled != 0)
                Logger.Warning("Optimum: suspending FidelityFX frame generation returned {0}", disabled);
        }
        if (frameGenerationWaitsLogged.Add(reason))
            Logger.Notification("Optimum: frame generation waiting for {0}", reason);
    }

    private void ResetFrameGeneration()
    {
        device?.ResetGeneratedFramePresent();
        dlssFrameCount = 0;
        dlssActuallyPresented = 0;
        if (fsr3FrameGeneration != null)
        {
            int disabled = fsr3FrameGeneration.Disable(device?.Fsr3ProxyContext ?? 0);
            if (disabled != 0)
                Logger.Warning("Optimum: disabling FidelityFX frame generation returned {0}", disabled);
            if (device != null) device.RetireUpscalerResource(fsr3FrameGeneration);
            else fsr3FrameGeneration.Dispose();
            fsr3FrameGeneration = null;
        }
        if (frameGenerationMotion > 0)
        {
            device?.DeleteTexture(frameGenerationMotion);
            frameGenerationMotion = 0;
        }
        if (fsr3UprightScene > 0) device?.DeleteTexture(fsr3UprightScene);
        if (fsr3UprightUi > 0) device?.DeleteTexture(fsr3UprightUi);
        if (fsr3UprightDepth > 0) device?.DeleteTexture(fsr3UprightDepth);
        if (fsr3UprightMotion > 0) device?.DeleteTexture(fsr3UprightMotion);
        fsr3UprightScene = fsr3UprightUi = fsr3UprightDepth = fsr3UprightMotion = 0;
        if (dlssUprightScene > 0) device?.DeleteTexture(dlssUprightScene);
        if (dlssUprightUi > 0) device?.DeleteTexture(dlssUprightUi);
        if (dlssUprightDepth > 0) device?.DeleteTexture(dlssUprightDepth);
        if (dlssUprightMotion > 0) device?.DeleteTexture(dlssUprightMotion);
        dlssUprightScene = dlssUprightUi = dlssUprightDepth = dlssUprightMotion = 0;
        dlssUprightWidth = dlssUprightHeight = 0;
        dlssUprightRenderWidth = dlssUprightRenderHeight = 0;
    }
}

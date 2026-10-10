using System.Globalization;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Graph;
using EnumTextureInternalFormat = VulkanStory.Contracts.TextureInternalFormat;
using EnumTexturePixelFormat = VulkanStory.Contracts.TexturePixelFormat;
using EnumFramebufferAttachment = VulkanStory.Contracts.FramebufferAttachment;

namespace VulkanStory.Game;

// Direct allocation-body migration from the retained platform, including
// temporal/provider/UI/FG slots. Game callbacks replace injected helpers.
// Provenance: 386e0d05386d0b228b439d09aeca851428f7bbf3; see implementation record.
internal sealed partial class GameGraphicsAdapter
{
    internal const int FsrFramebufferIndex = 18, TaaHistoryIndexA = 19, TaaHistoryIndexB = 20;
    internal const int TaaSharpenIndex = 21, UpscaledSceneIndex = 22;
    internal const int SceneNoHudIndex = 23, UiTargetIndex = 24, GeneratedFrameIndex = 25;
    private const int GlR32f = 0x822E;
    private List<FrameBufferRef>? allocatedFramebuffers;
    private bool taaDisabled;
    private int sceneNoHudIndex = -1, uiTargetIndex = -1;
    internal bool TaaTargetsReady { get; private set; }
    /// <summary>Whether the previous native TAA history is eligible for this target set; rebuild/reset invalidates it.</summary>
    internal bool TaaHistoryValid { get; set; }
    /// <summary>Whether the current real frame copied its completed scene before UI composition.</summary>
    internal bool SceneNoHudCaptured { get; set; }
    internal int SceneNoHudFramebufferIndex => sceneNoHudIndex;
    internal int UiFramebufferIndex => uiTargetIndex;
    /// <summary>Plan used to allocate the current SR input/output targets, or null on ordinary rendering/fallback.</summary>
    internal UpscalerPlan? AllocatedUpscalerPlan { get; private set; }

    private void ResetFramebufferPublication()
    {
        bool hadSamples = taaSampleBanks != null;
        taaWorkspaceOwnedBytes = 0;
        commonWorkspaceOwnedBytes = 0;
        ReleaseTaaSampleBanks();
        if (!hadSamples && routingEnabled()) PublishCachedTaaWorkspaceReserve(RequireLifecycleDevice());
        ClearModPassPlans();
        allocatedFramebuffers = null;
        TaaTargetsReady = TaaHistoryValid = SceneNoHudCaptured = false;
        sceneNoHudIndex = uiTargetIndex = -1;
        UpscaledThisFrame = UpscaledCompositeReady = false;
        GodRaysInScene = false;
        ResetFsr3InputPublication();
        AllocatedUpscalerPlan = null;
        FrameState = FrameState with { MotionAttachment = -1, MotionWriteActive = false };
    }
    private void PublishMotionAttachment(int index) =>
        FrameState = FrameState with { MotionAttachment = index, MotionWriteActive = false };
    private void StateDrawBuffers(int target, int mask) => Stated.SetDrawBuffers(
        target == 0 ? PassDeclaration.DefaultFramebuffer : target, (uint)mask);
    private void CloseUiScope()
    {
        Stated.UiImageFramebuffer = 0;
        RequireDevice().RedirectDefaultFramebuffer(0);
    }
    private void DisableUpscaler(string reason)
    {
        RequireFramebufferHost().DisableUpscaler(reason);
        AllocatedUpscalerPlan = null;
        UpscaledThisFrame = UpscaledCompositeReady = false;
    }
    private void DisableTaa(string reason)
    {
        ReleaseTaaSampleBanks();
        taaDisabled = true;
        TaaTargetsReady = false;
        TaaHistoryValid = false;
        RequireFramebufferHost().DisableTaa(reason);
    }
    private void FramebufferError(string format, string reason) =>
        RequireFramebufferHost().Error(string.Format(CultureInfo.InvariantCulture, format, reason));
    private bool TryPlanUpscale(int width, int height, out int renderWidth, out int renderHeight)
    {
        renderWidth = width; renderHeight = height;
        AllocatedUpscalerPlan = RequireFramebufferHost().PlanUpscale(width, height);
        if (AllocatedUpscalerPlan is not UpscalerPlan plan) return false;
        if (!plan.IsValid || plan.DisplayWidth != width || plan.DisplayHeight != height)
            throw new InvalidOperationException("The selected provider returned an invalid framebuffer plan.");
        renderWidth = plan.RenderWidth; renderHeight = plan.RenderHeight;
        return true;
    }
    private void AdoptTaaTargets(List<FrameBufferRef> targets, bool requested) =>
        TaaTargetsReady = requested && !taaDisabled && FrameState.MotionAttachment >= 0 &&
            targets[TaaHistoryIndexA] != null && targets[TaaHistoryIndexB] != null;

    private void FinishFramebuffers(List<FrameBufferRef> targets)
    {
        var host = RequireFramebufferHost();
        PrepareTemporalImageTargets(targets);
        MeshData quad = QuadMeshUtil.GetCustomQuadModelData(-1f, -1f, 0f, 2f, 2f);
        quad.Normals = null; quad.Rgba = null; quad.Uv = null;
        MeshRef? previous = GameFramebufferBindings.ScreenQuad(platform!);
        if (previous != null) DeleteMesh(previous);
        GameFramebufferBindings.SetScreenQuad(platform!, UploadMesh(quad));
        // Preserve public array shape; game query/readback routes own native
        // work instead of creating or using GL pixel-pack buffers.
        platform!.PixelPackBuffer =
        [
            new Vintagestory.Client.NoObf.ClientPlatformWindows.GLBuffer(),
            new Vintagestory.Client.NoObf.ClientPlatformWindows.GLBuffer(),
            new Vintagestory.Client.NoObf.ClientPlatformWindows.GLBuffer(),
        ];
        SetFramebuffer(GameFramebufferBindings.OffscreenEnabled(platform) ? targets[0] : null, keepViewport: true);
        InvalidateTaaSampleWindow();
        host.TargetsBuilt(targets, TaaTargetsReady);
        host.RequestTemporalReset();
        host.Notification("(Re-)loaded frame buffers on the VulkanStory device");
    }
    /// <summary>Allocates and publishes the retained framebuffer slots for current display dimensions and effective reconstruction plan.</summary>
    /// <returns>The retained target-slot list published for this allocation.</returns>
    /// <remarks>Must run through active adapter routing with a configured framebuffer host. Rebuilding withdraws previous motion/TAA/SR publication and requests a temporal reset after target completion.</remarks>
    internal List<FrameBufferRef> SetupDefaultFramebuffers()
    {
        var renderer = RequireDevice();
        var host = RequireFramebufferHost();
        GameFramebufferSettings settings = host.Settings();
        if (!float.IsFinite(settings.SsaaLevel) || settings.SsaaLevel <= 0 ||
            !float.IsFinite(settings.RenderScale) || settings.RenderScale <= 0)
            throw new InvalidOperationException("Invalid framebuffer render scale.");
        var clientSize = host.PixelSize();
        int displayWidth = clientSize.Width;
        int displayHeight = clientSize.Height;
        if (displayWidth < 0 || displayHeight < 0) throw new InvalidOperationException("Invalid SDL pixel size.");
        float ssaaLevel = settings.SsaaLevel;
        float sceneScale = ssaaLevel * settings.RenderScale;
        int width = displayWidth == 0 ? 0 : Math.Max(1, (int)(displayWidth * sceneScale));
        int height = displayHeight == 0 ? 0 : Math.Max(1, (int)(displayHeight * sceneScale));
        int nativeWidth = displayWidth == 0 ? 0 : Math.Max(1, (int)(displayWidth * ssaaLevel));
        int nativeHeight = displayHeight == 0 ? 0 : Math.Max(1, (int)(displayHeight * ssaaLevel));
        UpdateTaaWorkspaceReserve(Math.Max(width, nativeWidth), Math.Max(height, nativeHeight), replacingTargets: true);
        // The official rebuild allocates the new set before disposing the old one.
        // Release and complete its GPU users first: repeated settings changes must
        // not retain several full-resolution/shadow sets while allocating another.
        bool hadSampleBanks = taaSampleBanks != null;
        ReleaseTaaSampleBanks();
        if (platform!.FrameBuffers is { Count: > 0 } previous &&
            previous.Any(target => target != null && !target.Disposed))
        {
            DisposeFramebuffers(previous);
            renderer.CompleteReleasedResources();
        }
        else if (hadSampleBanks) renderer.CompleteReleasedResources();
        GameFramebufferBindings.AdoptSettings(platform!, settings);
        ResetFramebufferPublication();
        bool setupSsao = settings.SsaoQuality > 0;
        List<FrameBufferRef> list = new List<FrameBufferRef>(31);
        for (int i = 0; i <= 25; i++)
        {
            list.Add(null!);
        }
        int shadowMapQuality = settings.ShadowMapQuality;
        allocatedFramebuffers = list;
        bool upscaling = TryPlanUpscale(displayWidth, displayHeight, out int plannedWidth, out int plannedHeight);
        if (upscaling)
        {
            width = plannedWidth;
            height = plannedHeight;
            host.Notification("VulkanStory upscale: world target " + width + "x" + height +
                ", display target " + displayWidth + "x" + displayHeight + ".");
        }
        UpdateTaaWorkspaceReserve(Math.Max(width, nativeWidth), Math.Max(height, nativeHeight), replacingTargets: true);
        if (width == 0 || height == 0)
        {
            return list;
        }

        bool taaRequested = settings.TaaRequested && !taaDisabled && !upscaling;
        bool temporalRequested = taaRequested || upscaling || settings.FrameGenerationRequested;
        int motionAttachmentIndex = -1;

        // Primary: depth, colour, glow, and the SSAO position/normal G-buffer.
        FrameBufferRef primary = new FrameBufferRef();
        primary.Width = width;
        primary.Height = height;
        primary.FboId = renderer.CreateFramebuffer(width, height);
        primary.DepthTextureId = renderer.CreateTexture2D(width, height,
            EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        SetupTextureSampler(primary.DepthTextureId, 9728, 33071);
        renderer.AttachTexture(primary.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);

        int primaryAttachments = (setupSsao ? 4 : 2);
        primary.ColorTextureIds = new int[primaryAttachments];
        primary.ColorTextureIds[0] = renderer.CreateTexture2D(width, height,
            upscaling ? EnumTextureInternalFormat.Rgba16f : EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        primary.ColorTextureIds[1] = renderer.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        if (setupSsao)
        {
            primary.ColorTextureIds[2] = renderer.CreateTexture2D(width, height,
                EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            primary.ColorTextureIds[3] = renderer.CreateTexture2D(width, height,
                EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        }
        // Match the GL Primary filters, including linear G-buffer sampling and
        // the white border used when SSAO projects a sample off screen.
        for (int attachment = 0; attachment < primaryAttachments; attachment++)
        {
            int textureId = primary.ColorTextureIds[attachment];
            SetupTextureSampler(textureId,
                attachment >= 2 || ssaaLevel > 1f ? 9729 : 9728, attachment >= 2 ? 33069 : 10497);
            if (attachment >= 2) renderer.SetTextureBorderColor(textureId, 1f, 1f, 1f, 1f);
        }
        if (temporalRequested)
        {
            // Optimum: TAA motion attachment, appended after the SSAO G-buffer
            // so every existing attachment index is unchanged. Deliberately not
            // folded into the draw-buffer mask below - it stays out of every
            // pass's output set until a writer opts in (P3+).
            try
            {
                int motionTextureId = renderer.CreateTexture2D(width, height,
                    EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int[] extendedColorIds = new int[primary.ColorTextureIds.Length + 1];
                Array.Copy(primary.ColorTextureIds, extendedColorIds, primary.ColorTextureIds.Length);
                motionAttachmentIndex = primary.ColorTextureIds.Length;
                extendedColorIds[motionAttachmentIndex] = motionTextureId;
                primary.ColorTextureIds = extendedColorIds;
            }
            catch (Exception error)
            {
                if (upscaling) DisableUpscaler("Primary motion attachment: " + error.Message);
                else DisableTaa("Primary motion attachment (device): " + error.Message);
                motionAttachmentIndex = -1;
            }
        }
        for (int attachment = 0; attachment < primary.ColorTextureIds.Length; attachment++)
        {
            renderer.AttachTexture(primary.FboId,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + attachment),
                primary.ColorTextureIds[attachment], 0);
        }
        StateDrawBuffers(primary.FboId, (1 << primaryAttachments) - 1);
        list[0] = RegisterFramebuffer(primary);
        PublishMotionAttachment(motionAttachmentIndex);

        // Transparent: OIT accumulation, revealage, glow. Shares Primary's depth.
        FrameBufferRef transparent = new FrameBufferRef();
        transparent.Width = width;
        transparent.Height = height;
        transparent.FboId = renderer.CreateFramebuffer(width, height);
        transparent.ColorTextureIds = new int[3];
        transparent.ColorTextureIds[0] = renderer.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        transparent.ColorTextureIds[1] = renderer.CreateTexture2D(width, height,
            EnumTextureInternalFormat.R16f, EnumTexturePixelFormat.Red, IntPtr.Zero, false);
        transparent.ColorTextureIds[2] = renderer.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        for (int attachment = 0; attachment < 3; attachment++)
        {
            SetupTextureSampler(transparent.ColorTextureIds[attachment], 9729, 10497);
            renderer.AttachTexture(transparent.FboId,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + attachment),
                transparent.ColorTextureIds[attachment], 0);
        }
        renderer.AttachTexture(transparent.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);
        StateDrawBuffers(transparent.FboId, 7);
        transparent.DepthTextureId = primary.DepthTextureId;
        list[1] = RegisterFramebuffer(transparent);

        if (setupSsao)
        {
            int ssaoWidth = (int)((float)width * 0.5f);
            int ssaoHeight = (int)((float)height * 0.5f);

            FrameBufferRef ssao = new FrameBufferRef();
            ssao.Width = ssaoWidth;
            ssao.Height = ssaoHeight;
            ssao.FboId = renderer.CreateFramebuffer(ssaoWidth, ssaoHeight);
            ssao.ColorTextureIds = new int[2];
            // GL_RGB in the vanilla path; the device promotes it, because RGB is
            // not a guaranteed colour-attachment format in Vulkan.
            // A post-chain transient (Transient pool class); see TransientAllocator.PostChainSlots.
            ssao.ColorTextureIds[0] = renderer.CreateTransientTexture2DRaw(ssaoWidth, ssaoHeight, 6407, 13);
            renderer.AttachTexture(ssao.FboId, EnumFramebufferAttachment.ColorAttachment0, ssao.ColorTextureIds[0], 0);
            StateDrawBuffers(ssao.FboId, 1);

            // Rotation noise, and the sample kernel that goes with it. Same seed
            // and draw order as the GL path, so the pattern matches exactly.
            Random random = new Random(5);
            int noiseSize = 16;
            float[] noise = BuildSsaoNoise(random, noiseSize);
            GCHandle noiseHandle = GCHandle.Alloc(noise, GCHandleType.Pinned);
            // GL_RGBA32F, the same internal format the GL path allocates; GL
            // uploads GL_RGB data into it and fills alpha with 1 (see
            // BuildSsaoNoise), the device copies all four channels as given.
            try
            {
                ssao.ColorTextureIds[1] = renderer.CreateTexture2DRaw(
                    noiseSize, noiseSize, 34836, noiseHandle.AddrOfPinnedObject(), 16);
            }
            finally { noiseHandle.Free(); }
            renderer.SetTextureParameter(ssao.ColorTextureIds[1],
                GameGlTextureTokens.TextureWrapS,
                10497);
            renderer.SetTextureParameter(ssao.ColorTextureIds[1],
                GameGlTextureTokens.TextureWrapT,
                10497);

            float[] ssaoKernel = GameFramebufferBindings.SsaoKernel(platform!);
            for (int sample = 0; sample < 64; sample++)
            {
                Vec3f kernel = new Vec3f((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble());
                kernel.Normalize();
                kernel *= (float)random.NextDouble();
                float scale = (float)sample / 64f;
                scale = GameMath.Lerp(0.1f, 1f, scale * scale);
                kernel *= scale;
                ssaoKernel[sample * 3] = kernel.X;
                ssaoKernel[sample * 3 + 1] = kernel.Y;
                ssaoKernel[sample * 3 + 2] = kernel.Z;
            }
            list[13] = RegisterFramebuffer(ssao);

            list[14] = CreateColorTarget(ssaoWidth, ssaoHeight, EnumTextureInternalFormat.Rgba8);
            list[15] = CreateColorTarget(ssaoWidth, ssaoHeight, EnumTextureInternalFormat.Rgba8);
        }

        int postWidth = upscaling ? displayWidth : width;
        int postHeight = upscaling ? displayHeight : height;
        list[2] = CreateColorTarget(postWidth / 2, postHeight / 2, EnumTextureInternalFormat.Rgba8);
        list[3] = CreateColorTarget(postWidth / 2, postHeight / 2, EnumTextureInternalFormat.Rgba8);
        list[9] = CreateColorTarget(postWidth / 4, postHeight / 4, EnumTextureInternalFormat.Rgba8);
        list[8] = CreateColorTarget(postWidth / 4, postHeight / 4, EnumTextureInternalFormat.Rgba8);
        list[4] = CreateColorTarget(postWidth, postHeight, EnumTextureInternalFormat.Rgba16f);
        list[7] = CreateColorTarget(postWidth / 2, postHeight / 2, EnumTextureInternalFormat.Rgba16f);
        list[10] = CreateColorTarget(postWidth, postHeight, EnumTextureInternalFormat.Rgba16f);

        // Optimum: TAA history, render-resolution like Primary. Two slots so the
        // resolve reads last frame's parity while writing this frame's; never
        // cleared per frame (ClearFrameBuffer(Primary) only touches Primary).
        if (taaRequested)
        {
            try
            {
                list[TaaHistoryIndexA] = CreateHistoryTarget(width, height);
                list[TaaHistoryIndexB] = CreateHistoryTarget(width, height);
            }
            catch (Exception error)
            {
                DisableTaa("history targets (device): " + error.Message);
                DisposeFramebuffer(list[TaaHistoryIndexA], disposeTextures: true);
                DisposeFramebuffer(list[TaaHistoryIndexB], disposeTextures: true);
                list[TaaHistoryIndexA] = null!;
                list[TaaHistoryIndexB] = null!;
            }
            // Optimum TAA (P5): the sharpen target. Its own try - a sharpen
            // target that cannot be allocated costs the sharpening, not TAA,
            // so it nulls the slot instead of calling DisableTaa.
            try
            {
                list[TaaSharpenIndex] = CreateColorTarget(width, height,
                    EnumTextureInternalFormat.Rgba16f);
            }
            catch (Exception error)
            {
                FramebufferError("VulkanStory disabled the TAA sharpen pass: {0}", error.Message);
                list[TaaSharpenIndex] = null!;
            }
        }
        AdoptTaaTargets(list, taaRequested);

        // FSR renders at a reduced scale and resolves into a native-sized target.
        if (!upscaling && settings.RenderScale < 1.0f)
        {
            list[FsrFramebufferIndex] = CreateColorTarget(
                displayWidth, displayHeight,
                EnumTextureInternalFormat.Rgba8);
        }

        if (upscaling)
        {
            try
            {
                list[UpscaledSceneIndex] = CreateOwnedTarget(displayWidth, displayHeight,
                    withDepth: true, storage: true);
            }
            catch (Exception error)
            {
                DisableUpscaler("display-resolution output: " + error.Message);
                list[UpscaledSceneIndex] = null!;
            }
        }

        list[5] = CreateDepthTarget(width / 4, height / 4);

        // Both shadow slots always hold a FrameBufferRef, exactly as the GL path
        // does: vanilla constructs the objects unconditionally and only allocates
        // their textures when the quality setting reaches each level.
        //
        // The distinction matters because ShaderProgramBase.Use dereferences both
        // FrameBuffers[11] and FrameBuffers[12] whenever shadowmapQuality > 0,
        // and every shader including fogandlight.fsh - sky.fsh among them - takes
        // that branch. Leaving slot 12 null at quality 1 is a null reference on
        // the first sky draw, which is what it was.
        bool handheldShadows = settings.HandheldShadowTier;
        int shadowSize = Math.Max(4, shadowMapQuality + 2) * 1024;
        int farShadowSize = handheldShadows ? 1024 : shadowSize;
        int nearShadowSize = handheldShadows ? 2048 : shadowSize;
        bool compactShadowDepth = handheldShadows && renderer.SupportsCompactShadowDepth();
        list[11] = shadowMapQuality > 0
            ? CreateDepthTarget(farShadowSize, farShadowSize, compactShadowDepth)
            : CreatePlaceholderTarget(farShadowSize, farShadowSize);
        list[12] = shadowMapQuality > 1
            ? CreateDepthTarget(nearShadowSize, nearShadowSize, compactShadowDepth)
            : CreatePlaceholderTarget(nearShadowSize, nearShadowSize);

        for (int shadow = 11; shadow <= 12; shadow++)
        {
            int textureId = list[shadow].DepthTextureId;
            if (textureId == 0) continue;
            SetupTextureSampler(textureId, 9729, 33069);
            renderer.SetTextureBorderColor(textureId, 1f, 1f, 1f, 1f);
            renderer.SetTextureParameter(textureId, 34892, 34894);
        }

        // The post chain's colour textures are transients; record the slot each one serves.
        foreach (int transientSlot in TransientAllocator.PostChainSlots)
        {
            FrameBufferRef transientTarget = list[transientSlot];
            if (transientTarget == null || transientTarget.ColorTextureIds == null ||
                transientTarget.ColorTextureIds.Length == 0) continue;
            renderer.OptInTransient(transientTarget.ColorTextureIds[0], transientSlot);
        }

        // World/UI separation: the HUD-less scene snapshot and the UI image (UiSeparation.cs).
        AllocateUiSeparationTargets(list, displayWidth, displayHeight);
        if (settings.FrameGenerationProvider == "fsr3")
            AllocateFrameGenerationTarget(list, displayWidth, displayHeight);

        FinishFramebuffers(list);
        return list;
    }

    private void SetupTextureSampler(int textureId, int filter, int wrap)
    {
        var renderer = RequireDevice();
        renderer.SetTextureParameter(textureId, GameGlTextureTokens.TextureMinFilter, filter);
        renderer.SetTextureParameter(textureId, GameGlTextureTokens.TextureMagFilter, filter);
        renderer.SetTextureParameter(textureId, GameGlTextureTokens.TextureWrapS, wrap);
        renderer.SetTextureParameter(textureId, GameGlTextureTokens.TextureWrapT, wrap);
    }

    private FrameBufferRef CreateColorTarget(int width, int height, EnumTextureInternalFormat format)
    {
        var renderer = RequireDevice();
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.FboId = renderer.CreateFramebuffer(width, height);
        target.ColorTextureIds = new int[1];
        target.ColorTextureIds[0] = renderer.CreateTransientTexture2D(width, height, format, -1);
        // setupAttachment uses linear filtering and edge clamping. FXAA and
        // the reduced-resolution blur passes require fractional texel samples.
        SetupTextureSampler(target.ColorTextureIds[0], 9729, 33071);
        renderer.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
        StateDrawBuffers(target.FboId, 1);
        return RegisterFramebuffer(target);
    }

    private FrameBufferRef CreateDepthTarget(int width, int height, bool useD16 = false)
    {
        var renderer = RequireDevice();
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.FboId = renderer.CreateFramebuffer(width, height);
        target.ColorTextureIds = new int[0];
        target.DepthTextureId = useD16
            ? renderer.CreateTexture2DRaw(width, height, 0x81A5, IntPtr.Zero, 2)
            : renderer.CreateTexture2D(width, height, EnumTextureInternalFormat.DepthComponent32,
                EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        SetupTextureSampler(target.DepthTextureId, 9729, 33071);
        renderer.AttachTexture(target.FboId, EnumFramebufferAttachment.DepthAttachment, target.DepthTextureId, 0);
        StateDrawBuffers(target.FboId, 0);
        return RegisterFramebuffer(target);
    }

    private FrameBufferRef CreateHistoryTarget(int width, int height)
    {
        var renderer = RequireDevice();
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.FboId = renderer.CreateFramebuffer(width, height);
        target.ColorTextureIds = new int[3];
        try
        {
        target.ColorTextureIds[0] = renderer.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        target.ColorTextureIds[1] = renderer.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        target.ColorTextureIds[2] = renderer.CreateTexture2DRaw(width, height, GlR32f, IntPtr.Zero, 4);
        // Optimum TAA: the resolve reprojects the history by a fractional pixel
        // offset, so colour (Catmull-Rom taps) and glow (a plain bilinear fetch)
        // must filter LINEAR; sampling them NEAREST snaps the reprojection to
        // whole pixels and the history never converges. Linear depth stays
        // NEAREST - interpolating across a silhouette invents a depth that is on
        // neither surface and defeats the disocclusion test. Clamp to edge on
        // all three, matching CreateHistoryTargetGl.
        SetupTextureSampler(target.ColorTextureIds[0], 9729, 33071);
        SetupTextureSampler(target.ColorTextureIds[1], 9729, 33071);
        SetupTextureSampler(target.ColorTextureIds[2], 9728, 33071);
        for (int attachment = 0; attachment < 3; attachment++)
        {
            renderer.AttachTexture(target.FboId,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + attachment),
                target.ColorTextureIds[attachment], 0);
        }
        StateDrawBuffers(target.FboId, 7);
        if (!renderer.CheckFramebufferComplete(target.FboId, out string status))
        {
            throw new Exception("VulkanStory TAA history FBO: " + status);
        }
        return RegisterFramebuffer(target);
        }
        catch
        {
            ReleaseUnpublishedFramebuffer(target);
            throw;
        }
    }

    /// <summary>Creates an owned framebuffer descriptor with dimensions but no color attachment storage.</summary>
    /// <param name="width">Descriptor width in pixels.</param>
    /// <param name="height">Descriptor height in pixels.</param>
    /// <returns>Registered placeholder reference used to preserve retained slot shape.</returns>
    internal FrameBufferRef CreatePlaceholderTarget(int width, int height)
    {
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.ColorTextureIds = new int[0];
        return RegisterFramebuffer(target);
    }

    /// <summary>Builds the retained RGBA tangent-plane SSAO noise texture values.</summary>
    /// <param name="random">Random source used for signed XY direction samples.</param>
    /// <param name="noiseSize">Square noise side length in texels.</param>
    /// <returns>Four floats per texel: normalized XY, zero Z and unit alpha.</returns>
    internal static float[] BuildSsaoNoise(Random random, int noiseSize)
    {
        float[] noise = new float[noiseSize * noiseSize * 4];
        Vec3f direction = new Vec3f();
        for (int texel = 0; texel < noiseSize * noiseSize; texel++)
        {
            direction.Set((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, 0f).Normalize();
            noise[texel * 4] = direction.X;
            noise[texel * 4 + 1] = direction.Y;
            noise[texel * 4 + 2] = direction.Z;
            noise[texel * 4 + 3] = 1f;
        }
        return noise;
    }

    /// <summary>Allocates display-sized HUD-free and UI targets independently, logging a fallback when either allocation fails.</summary>
    /// <param name="list">Current retained target-slot list receiving the owned references.</param>
    /// <param name="renderWidth">Retained caller render width; separate UI allocation uses actual window pixels.</param>
    /// <param name="renderHeight">Retained caller render height; separate UI allocation uses actual window pixels.</param>
    internal void AllocateUiSeparationTargets(List<FrameBufferRef> list, int renderWidth, int renderHeight)
    {
        CloseUiScope();
        var host = RequireFramebufferHost();
        sceneNoHudIndex = -1;
        uiTargetIndex = -1;
        SceneNoHudCaptured = false;

        try
        {
            // Default already contains the upscaled world at display resolution.
            var display = host.PixelSize();
            FrameBufferRef snapshot = CreateOwnedTarget(display.Width, display.Height, withDepth: false);
            list[SceneNoHudIndex] = snapshot;
            sceneNoHudIndex = SceneNoHudIndex;
        }
        catch (Exception error)
        {
            FramebufferError("VulkanStory: no HUD-less scene snapshot: {0}", error.Message);
            list[SceneNoHudIndex] = null!;
        }

        // The window's size, not the render size: the GUI has always laid itself out in window
        // pixels, and the compose puts it back over the window one for one.
        var window = host.PixelSize();
        int windowWidth = window.Width;
        int windowHeight = window.Height;
        try
        {
            FrameBufferRef ui = CreateOwnedTarget(windowWidth, windowHeight, withDepth: true);
            list[UiTargetIndex] = ui;
            uiTargetIndex = UiTargetIndex;
        }
        catch (Exception error)
        {
            FramebufferError("VulkanStory: no separate UI image, the GUI draws onto the window: {0}", error.Message);
            list[UiTargetIndex] = null!;
        }
    }

    private FrameBufferRef CreateOwnedTarget(int width, int height, bool withDepth,
        bool storage = false, bool frameGenerationStorage = false)
    {
        var renderer = RequireDevice();
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.FboId = renderer.CreateFramebuffer(width, height);
        target.ColorTextureIds = new int[1];
        try
        {
        target.ColorTextureIds[0] = storage
            ? renderer.CreateUpscaleTexture(width, height,
                frameGenerationStorage ? Silk.NET.Vulkan.Format.R8G8B8A8Unorm :
                    Silk.NET.Vulkan.Format.R16G16B16A16Sfloat, storage: true)
            : renderer.CreateTexture2D(width, height,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        SetupTextureSampler(target.ColorTextureIds[0], 9728, 33071);
        renderer.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
        if (withDepth)
        {
            target.DepthTextureId = renderer.CreateTexture2D(width, height,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
            SetupTextureSampler(target.DepthTextureId, 9728, 33071);
            renderer.AttachTexture(target.FboId, EnumFramebufferAttachment.DepthAttachment, target.DepthTextureId, 0);
        }
        StateDrawBuffers(target.FboId, 1);
        if (!renderer.CheckFramebufferComplete(target.FboId, out string status))
        {
            throw new Exception("framebuffer incomplete: " + status);
        }
        return RegisterFramebuffer(target);
        }
        catch
        {
            ReleaseUnpublishedFramebuffer(target);
            throw;
        }
    }

    /// <summary>Unwinds locally created attachments and framebuffer state before publication fails.</summary>
    private void ReleaseUnpublishedFramebuffer(FrameBufferRef target)
    {
        var renderer = RequireDevice();
        Stated.ForgetFramebuffer(target.FboId);
        renderer.DeleteFramebuffer(target.FboId);
        foreach (int texture in target.ColorTextureIds)
            if (texture > 0) renderer.DeleteTexture(texture);
        if (target.DepthTextureId > 0) renderer.DeleteTexture(target.DepthTextureId);
    }

    private void AllocateFrameGenerationTarget(List<FrameBufferRef> buffers, int width, int height)
    {
        try
        {
            buffers[GeneratedFrameIndex] = CreateOwnedTarget(width, height,
                withDepth: false, storage: true, frameGenerationStorage: true);
        }
        catch (Exception error)
        {
            FramebufferError("VulkanStory: FSR 3 frame generation target: {0}", error.Message);
        }
    }
}

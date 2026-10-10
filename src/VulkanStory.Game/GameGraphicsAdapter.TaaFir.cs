using Vintagestory.API.Client;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.AmbientOcclusion;
using VulkanStory.Render.Vulkan.Graph;
using EnumTextureInternalFormat = VulkanStory.Contracts.TextureInternalFormat;
using EnumTexturePixelFormat = VulkanStory.Contracts.TexturePixelFormat;
using EnumFramebufferAttachment = VulkanStory.Contracts.FramebufferAttachment;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    // Seven separate preceding inputs, transported between banks. Resolved
    // history remains in the original public slots and is never a cache input.
    private sealed class TaaSampleBank
    {
        internal FrameBufferRef Low = new();
        internal FrameBufferRef High = new();
        internal int[] SceneSamples = new int[7];
        internal int[] GlowSamples = new int[7];
        internal int CountTexture;
    }

    private TaaSampleBank[]? taaSampleBanks;
    private int taaWorkspaceWidth, taaWorkspaceHeight;
    private ulong taaWorkspaceExpectedBytes, taaWorkspaceOwnedBytes, taaSampleAllocationBytes;
    private ulong aoWorkspaceExpectedBytes, aoWorkspaceOwnedBytes;
    private bool aoWorkspacePossible;

    // Preserve the known native workspace even while TAA is off or SR uses a
    // smaller input. Owned images receive credit once; common targets do not.
    private void UpdateTaaWorkspaceReserve(int width, int height, bool replacingTargets = false)
    {
        if (width <= 0 || height <= 0) return;
        VulkanDevice renderer = RequireLifecycleDevice();
        bool dimensionsChanged = width != taaWorkspaceWidth || height != taaWorkspaceHeight;
        bool possibleAo = (postSettings?.Settings.AmbientOcclusion is "auto" or "gtao") &&
            RequireFramebufferHost().Settings().SsaoQuality > 0 &&
            shaderOverrides?.AllowsRetainedSceneFeatures != false && ambientOcclusionFailure == null;
        if (dimensionsChanged || possibleAo != aoWorkspacePossible)
        {
            aoWorkspaceExpectedBytes = possibleAo
                ? GtaoRenderer.EstimateWorkspaceAllocationBytes(renderer, width, height) : 0;
            aoWorkspacePossible = possibleAo;
        }
        if (dimensionsChanged)
        {
            ulong half = renderer.EstimateColorImageAllocationBytes(width, height,
                Silk.NET.Vulkan.Format.R16G16B16A16Sfloat);
            ulong unorm = renderer.EstimateColorImageAllocationBytes(width, height,
                Silk.NET.Vulkan.Format.R8G8B8A8Unorm);
            ulong scalar = renderer.EstimateColorImageAllocationBytes(width, height,
                Silk.NET.Vulkan.Format.R32Sfloat);
            taaWorkspaceExpectedBytes = checked(18 * half + 16 * unorm + 4 * scalar);
            taaWorkspaceWidth = width;
            taaWorkspaceHeight = height;
        }
        if (replacingTargets)
        {
            // Withdraw old-generation credit before retirement. The new layout
            // owns this priority claim before competing geometry can grow.
            taaWorkspaceOwnedBytes = 0;
            // Size-dependent AO targets are retired with the public set; its
            // persistent sampling LUT remains owned across this replacement.
            aoWorkspaceOwnedBytes = renderer.TextureAllocationBytes(ambientOcclusion?.HilbertTexture ?? 0);
        }
        else
        {
            var textures = new HashSet<int>();
            ulong samples = 0;
            if (taaSampleBanks is { } banks)
                foreach (TaaSampleBank bank in banks)
                {
                    foreach (int texture in bank.SceneSamples) Credit(texture, ref samples);
                    foreach (int texture in bank.GlowSamples) Credit(texture, ref samples);
                    Credit(bank.CountTexture, ref samples);
                }
            taaSampleAllocationBytes = samples;
            ulong owned = samples;
            if (allocatedFramebuffers is { } targets)
            {
                foreach (int slot in new[] { TaaHistoryIndexA, TaaHistoryIndexB, TaaSharpenIndex })
                    if (slot < targets.Count && targets[slot] is { Disposed: false } target)
                        foreach (int texture in target.ColorTextureIds) Credit(texture, ref owned);
                if (targets.Count > PrimaryIndex && targets[PrimaryIndex] is { Disposed: false } primary &&
                    FrameState.MotionAttachment >= 0 && FrameState.MotionAttachment < primary.ColorTextureIds.Length)
                    Credit(primary.ColorTextureIds[FrameState.MotionAttachment], ref owned);
            }
            taaWorkspaceOwnedBytes = owned;
            aoWorkspaceOwnedBytes = ambientOcclusion?.WorkspaceAllocationBytes ?? 0;

            void Credit(int texture, ref ulong total)
            {
                if (texture > 0 && textures.Add(texture))
                    total = checked(total + renderer.TextureAllocationBytes(texture));
            }
        }
        PublishCachedTaaWorkspaceReserve(renderer);
    }

    private void PublishCachedTaaWorkspaceReserve(VulkanDevice renderer) =>
        renderer.SetImageWorkspaceReserve(checked(
            (taaWorkspaceExpectedBytes > taaWorkspaceOwnedBytes ? taaWorkspaceExpectedBytes - taaWorkspaceOwnedBytes : 0) +
            (aoWorkspaceExpectedBytes > aoWorkspaceOwnedBytes ? aoWorkspaceExpectedBytes - aoWorkspaceOwnedBytes : 0)));

    /// <summary>Prepares the complete temporal image workspace before scene geometry is admitted.</summary>
    internal void PrepareTemporalImageTargets(IReadOnlyList<FrameBufferRef> targets)
    {
        UpdateTaaWorkspaceReserve(taaWorkspaceWidth, taaWorkspaceHeight);
        PrepareAmbientOcclusionTargets(targets);
        PrepareTaaSampleTargets(targets);
        UpdateTaaWorkspaceReserve(taaWorkspaceWidth, taaWorkspaceHeight);
    }

    /// <summary>Allocates active native TAA cache targets before preparation can allocate geometry buffers.</summary>
    internal void PrepareTaaSampleTargets(IReadOnlyList<FrameBufferRef> targets)
    {
        if (postSettings?.EffectiveTaa == true && TaaTargetsReady &&
            targets.Count > PrimaryIndex && targets[PrimaryIndex] is { Disposed: false } primary)
            EnsureTaaSampleBanks(primary.Width, primary.Height);
    }

    /// <summary>Private cache targets, ordered bank zero low/high then bank one low/high, for opt-in capture.</summary>
    internal FrameBufferRef[] TaaSampleCaptureTargets => taaSampleBanks is { } banks
        ? [banks[0].Low, banks[0].High, banks[1].Low, banks[1].High] : [];

    private void InvalidateTaaSampleWindow() => TaaHistoryValid = false;

    private bool EnsureTaaSampleBanks(int width, int height)
    {
        var renderer = RequireDevice();
        if (postSettings?.EffectiveTaa != true || !TaaTargetsReady)
        {
            ReleaseTaaSampleBanks();
            TaaReadiness = "native TAA sample window is inactive";
            return false;
        }
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "TAA sample targets require positive dimensions.");
        if (renderer.MaxColorAttachments < 8)
            throw new NotSupportedException($"The TAA sample transport pass writes eight color attachments; this device advertises {renderer.MaxColorAttachments}.");
        if (taaSampleBanks is { } existing && existing.All(bank =>
            !bank.Low.Disposed && !bank.High.Disposed &&
            bank.Low.Width == width && bank.Low.Height == height &&
            bank.High.Width == width && bank.High.Height == height))
            return true;

        long bytes = checked((long)width * height * 176);
        UpdateTaaWorkspaceReserve(Math.Max(width, taaWorkspaceWidth), Math.Max(height, taaWorkspaceHeight));
        bool replacing = taaSampleBanks != null;
        ReleaseTaaSampleBanks();
        // A dimension change must not retain the previous 176 bytes/pixel while
        // requesting its replacement. Ordinary frame deletion remains deferred.
        if (replacing) renderer.CompleteReleasedResources();
        InvalidateTaaSampleWindow();
        taaSampleBanks = [new TaaSampleBank(), new TaaSampleBank()];
        try
        {
            foreach (TaaSampleBank bank in taaSampleBanks)
                AllocateTaaSampleBank(bank, width, height);
            UpdateTaaWorkspaceReserve(taaWorkspaceWidth, taaWorkspaceHeight);
            return true;
        }
        catch (Exception error)
        {
            TaaReadiness = $"native TAA sample window allocation failed ({width}x{height}, {bytes} image bytes): {error.Message}";
            try { ReleaseTaaSampleBanks(); }
            catch (Exception cleanup)
            {
                throw new AggregateException(TaaReadiness, error, cleanup);
            }
            // The estimator requires all seven ages; resource pressure cannot
            // silently select the previous exponential accumulator.
            throw new InvalidOperationException(TaaReadiness, error);
        }
    }

    private void AllocateTaaSampleBank(TaaSampleBank bank, int width, int height)
    {
        var renderer = RequireDevice();
        InitializeTaaSampleFramebuffer(bank.Low, width, height, 8);
        InitializeTaaSampleFramebuffer(bank.High, width, height, 7);
        for (int age = 0; age < bank.SceneSamples.Length; age++)
        {
            FrameBufferRef target = age < 4 ? bank.Low : bank.High;
            int slot = (age < 4 ? age : age - 4) * 2;
            int scene = renderer.CreateTexture2D(width, height,
                EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            bank.SceneSamples[age] = target.ColorTextureIds[slot] = scene;
            SetupTextureSampler(scene, 9729, 33071); // LINEAR, CLAMP_TO_EDGE.
            renderer.AttachTexture(target.FboId,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot), scene, 0);

            int glow = renderer.CreateTexture2D(width, height,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            bank.GlowSamples[age] = target.ColorTextureIds[slot + 1] = glow;
            SetupTextureSampler(glow, 9729, 33071);
            renderer.AttachTexture(target.FboId,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot + 1), glow, 0);
        }
        bank.CountTexture = renderer.CreateTexture2DRaw(width, height, GlR32f, IntPtr.Zero, 4);
        bank.High.ColorTextureIds[6] = bank.CountTexture;
        SetupTextureSampler(bank.CountTexture, 9728, 33071); // NEAREST metadata, never interpolated.
        renderer.AttachTexture(bank.High.FboId,
            (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + 6), bank.CountTexture, 0);
        StateDrawBuffers(bank.Low.FboId, 255);
        StateDrawBuffers(bank.High.FboId, 127);
        bool lowComplete = renderer.CheckFramebufferComplete(bank.Low.FboId, out string lowStatus);
        bool highComplete = renderer.CheckFramebufferComplete(bank.High.FboId, out string highStatus);
        if (!lowComplete || !highComplete)
            throw new InvalidOperationException("Native TAA sample framebuffer is incomplete: " +
                (lowStatus != "complete" ? lowStatus : highStatus));
    }

    private void InitializeTaaSampleFramebuffer(FrameBufferRef target, int width, int height, int colors)
    {
        target.Width = width;
        target.Height = height;
        target.ColorTextureIds = new int[colors];
        target.FboId = RequireDevice().CreateFramebuffer(width, height);
        // Publish only to the private ownership table, before attachment creation
        // can fail. These references never enter the game's framebuffer array.
        RegisterFramebuffer(target);
    }

    /// <summary>Retires the auxiliary native-TAA images while the session device is still alive.</summary>
    internal void ReleaseTaaSampleTargets() => ReleaseTaaSampleBanks();

    private void ReleaseTaaSampleBanks()
    {
        InvalidateTaaSampleWindow();
        if (taaSampleBanks is not { } banks) return;
        var renderer = RequireLifecycleDevice();
        renderer.EndNativePass();
        renderer.EndStagePass();
        foreach (TaaSampleBank bank in banks)
        {
            ReleaseTaaSampleFramebuffer(bank.Low);
            ReleaseTaaSampleFramebuffer(bank.High);
            for (int age = 0; age < bank.SceneSamples.Length; age++)
            {
                if (bank.SceneSamples[age] > 0) renderer.DeleteTexture(bank.SceneSamples[age]);
                bank.SceneSamples[age] = 0;
                if (bank.GlowSamples[age] > 0) renderer.DeleteTexture(bank.GlowSamples[age]);
                bank.GlowSamples[age] = 0;
            }
            if (bank.CountTexture > 0) renderer.DeleteTexture(bank.CountTexture);
            bank.CountTexture = 0;
        }
        taaSampleBanks = null;
        taaWorkspaceOwnedBytes -= Math.Min(taaWorkspaceOwnedBytes, taaSampleAllocationBytes);
        taaSampleAllocationBytes = 0;
        // This path had live owned banks; post-device adapter detachment has
        // none and returns above without an image query or reserve setter.
        PublishCachedTaaWorkspaceReserve(renderer);
    }

    private void ReleaseTaaSampleFramebuffer(FrameBufferRef target)
    {
        if (target.Disposed) return;
        var renderer = RequireLifecycleDevice();
        if (ReferenceEquals(currentFramebuffer, target))
        {
            currentFramebuffer = null;
            CurrentTargetId = PassDeclaration.DefaultFramebuffer;
            GameFramebufferBindings.Set(platform!, null);
        }
        // Targets borrow the bank's attachments. Delete those exactly once from
        // the arrays above, using the renderer's normal timeline retirement.
        if (target.FboId > 0)
        {
            Stated.ForgetFramebuffer(target.FboId);
            renderer.DeleteFramebuffer(target.FboId);
        }
        if (FramebufferOwners.TryGetValue(target, out var owner)) owner.Released = true;
        target.Disposed = true;
    }
}

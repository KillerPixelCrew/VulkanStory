using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private int commonWorkspaceWidth, commonWorkspaceHeight;
    private bool commonWorkspaceSsao;
    private ulong commonWorkspaceBaseBytes, commonWorkspaceOitBytes, commonWorkspaceOwnedBytes;

    // The native TAA promise also needs the scene that feeds it. SR can shrink
    // this cohort independently of the display-sized post targets and shadows.
    private void UpdateCommonFrameWorkspaceReserve(VulkanDevice renderer, int width, int height,
        bool replacingTargets)
    {
        bool ssao = RequireFramebufferHost().Settings().SsaoQuality > 0;
        if (width != commonWorkspaceWidth || height != commonWorkspaceHeight || ssao != commonWorkspaceSsao)
        {
            var requirements = new Dictionary<(int Width, int Height, Format Format, int Layers), ulong>();
            ulong Image(int w, int h, Format format, int layers = 1)
            {
                var key = (Math.Max(1, w), Math.Max(1, h), format, layers);
                if (!requirements.TryGetValue(key, out ulong bytes))
                    requirements.Add(key, bytes = renderer.EstimateTextureImageAllocationBytes(
                        key.Item1, key.Item2, format, layers));
                return bytes;
            }

            ulong unorm = Image(width, height, Format.R8G8B8A8Unorm);
            ulong half = Image(width, height, Format.R16G16B16A16Sfloat);
            // Primary: native colour/glow/depth, plus its optional G-buffer.
            // Motion belongs exclusively to the existing TAA workspace claim.
            ulong primary = checked(2 * unorm + Image(width, height, Format.D32Sfloat) + (ssao ? 2 * half : 0));
            // Transparent still owns its original accumulation allocation after
            // OIT replaces attachment zero. Its private bin images are separate.
            ulong transparent = checked(half + unorm + Image(width, height, Format.R16Sfloat));
            ulong liquidDepth = Image(width / 4, height / 4, Format.D32Sfloat);
            ulong ssaoTargets = ssao ? checked(3 * Image(width / 2, height / 2, Format.R8G8B8A8Unorm)) : 0;
            commonWorkspaceBaseBytes = checked(primary + transparent + liquidDepth + ssaoTargets);
            commonWorkspaceOitBytes = checked(unorm + Image(width, height, Format.R16G16B16A16Sfloat, 3));
            commonWorkspaceWidth = width;
            commonWorkspaceHeight = height;
            commonWorkspaceSsao = ssao;
        }
        if (replacingTargets) commonWorkspaceOwnedBytes = 0;
        else CreditCommonFrameWorkspace(renderer);
    }

    private ulong CommonFrameWorkspaceReserveBytes
    {
        get
        {
            ulong expected = checked(commonWorkspaceBaseBytes + (oitDisabled ? 0 : commonWorkspaceOitBytes));
            return expected > commonWorkspaceOwnedBytes ? expected - commonWorkspaceOwnedBytes : 0;
        }
    }

    private void CreditCommonFrameWorkspace(VulkanDevice renderer)
    {
        var textures = new HashSet<int>();
        ulong owned = 0;
        if (allocatedFramebuffers is { } targets)
        {
            if (Live(PrimaryIndex) is { } primary)
            {
                for (int slot = 0; slot < Math.Min(4, primary.ColorTextureIds.Length); slot++)
                    if (slot != FrameState.MotionAttachment) Credit(primary.ColorTextureIds[slot]);
                Credit(primary.DepthTextureId);
            }
            if (Live((int)EnumFrameBuffer.Transparent) is { } transparent)
                foreach (int texture in transparent.ColorTextureIds.Take(3)) Credit(texture);
            if (Live((int)EnumFrameBuffer.LiquidDepth) is { } liquid) Credit(liquid.DepthTextureId);
            foreach (int index in new[] { NativeSsaoTargetIndex, NativeSsaoBlurVerticalIndex, NativeSsaoBlurHorizontalIndex })
                if (Live(index) is { ColorTextureIds.Length: > 0 } target) Credit(target.ColorTextureIds[0]);

            FrameBufferRef? Live(int index) => index >= 0 && index < targets.Count &&
                targets[index] is { Disposed: false } target ? target : null;
        }
        if (oitTransparent is { Disposed: false })
        {
            Credit(oitReveal);
            Credit(oitAccumulation);
        }
        commonWorkspaceOwnedBytes = owned;

        void Credit(int texture)
        {
            if (texture > 0 && textures.Add(texture))
                owned = checked(owned + renderer.TextureAllocationBytes(texture));
        }
    }

    // OIT creates/retires its private images after public target publication.
    // Refresh that ownership without querying prospective image requirements.
    private void RefreshCommonFrameWorkspaceOwnership()
    {
        if (!routingEnabled()) return;
        VulkanDevice renderer = RequireLifecycleDevice();
        CreditCommonFrameWorkspace(renderer);
        PublishCachedTaaWorkspaceReserve(renderer);
    }
}

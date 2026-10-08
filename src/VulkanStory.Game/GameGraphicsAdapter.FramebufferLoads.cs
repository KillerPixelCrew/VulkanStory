using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal bool UpscaledThisFrame { get; set; }
    internal bool UpscaledCompositeReady { get; set; }
    private void Viewport(int width, int height) =>
        Stated.Viewport = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));

    internal void LoadFramebuffer(FrameBufferRef target, int texture)
    {
        var renderer = RequireDevice();
        int handle = LiveFramebufferHandle(target);
        if (handle <= 0) throw new InvalidOperationException("An attachment swap requires an allocated framebuffer.");
        SetFramebuffer(target, keepViewport: false);
        renderer.AttachTexture(handle, FramebufferAttachment.ColorAttachment0, texture, 0);
    }

    internal void LoadFramebuffer(EnumFrameBuffer kind)
    {
        RequireDevice();
        var host = RequireFramebufferHost();
        if (kind == EnumFrameBuffer.Primary && UpscaledCompositeReady && UpscaledThisFrame)
        {
            FrameBufferRef composite = FramebufferAt((EnumFrameBuffer)UpscaledSceneIndex);
            SetFramebuffer(composite, keepViewport: false);
            return;
        }
        switch (kind)
        {
            case EnumFrameBuffer.Transparent:
            {
                // Retained overlay override: use the actual render target size,
                // including the selected vendor provider's render scale.
                FrameBufferRef transparent = FramebufferAt(kind);
                SetFramebuffer(transparent, keepViewport: false);
                Stated.CullEnabled = false;
                Stated.DepthWrite = false;
                Stated.DepthTest = true;
                Stated.SetDrawBuffers(transparent.FboId, 7);
                Stated.SetBlendEnabled(true);
                Stated.SetBlendMode(RenderBlendMode.Standard);
                Stated.SetSlotBlend(0, 32774, 1, 1, 1, 1);
                Stated.SetSlotBlend(1, 32774, 0, 769, 0, 769);
                Stated.SetSlotBlend(2, 32774, 770, 771, 770, 771);
                break;
            }
            case EnumFrameBuffer.Default:
            {
                var display = host.PixelSize();
                SetFramebuffer(null, keepViewport: true);
                Viewport(display.Width, display.Height);
                SelectDrawBuffer((OpenTK.Graphics.OpenGL.DrawBufferMode)1029);
                break;
            }
            case EnumFrameBuffer.BlurHorizontalMedRes:
            case EnumFrameBuffer.BlurVerticalMedRes:
            case EnumFrameBuffer.BlurVerticalLowRes:
            case EnumFrameBuffer.BlurHorizontalLowRes:
            case EnumFrameBuffer.GodRays:
            case EnumFrameBuffer.SSAO:
            case EnumFrameBuffer.SSAOBlurVertical:
            case EnumFrameBuffer.SSAOBlurHorizontal:
            case EnumFrameBuffer.SSAOBlurVerticalHalfRes:
            case EnumFrameBuffer.SSAOBlurHorizontalHalfRes:
                // The retained targets already carry render/display scaling;
                // window * ssaa alone would miss an upscaler's resolution.
                SetFramebuffer(FramebufferAt(kind), keepViewport: false);
                break;
            case EnumFrameBuffer.FindBright:
                SetFramebuffer(FramebufferAt(kind), keepViewport: true);
                break;
            case EnumFrameBuffer.Luma:
                SetFramebuffer(FramebufferAt(kind), keepViewport: true);
                Stated.SetBlendEnabled(false);
                break;
            case EnumFrameBuffer.ShadowmapFar:
            case EnumFrameBuffer.ShadowmapNear:
            case EnumFrameBuffer.LiquidDepth:
                FrameBufferRef depth = FramebufferAt(kind);
                Stated.DepthWrite = true;
                Stated.DepthTest = true;
                ToggleBlend(true, EnumBlendMode.Standard);
                Stated.CullEnabled = true;
                SetFramebuffer(depth, keepViewport: true);
                break;
            case EnumFrameBuffer.Primary:
                if (GameFramebufferBindings.OffscreenEnabled(platform!))
                    SetFramebuffer(FramebufferAt(kind), keepViewport: false);
                else
                {
                    SetFramebuffer(null, keepViewport: true);
                    SelectDrawBuffer((OpenTK.Graphics.OpenGL.DrawBufferMode)1029);
                }
                break;
        }
    }

    internal void UnloadFramebuffer(EnumFrameBuffer kind)
    {
        RequireDevice();
        var host = RequireFramebufferHost();
        bool offscreen = GameFramebufferBindings.OffscreenEnabled(platform!);
        FrameBufferRef? primary = offscreen ? FramebufferAt(EnumFrameBuffer.Primary) : null;
        var display = host.PixelSize();
        float scale = GameFramebufferBindings.SsaaLevel(platform!);
        if (kind == EnumFrameBuffer.Transparent) Stated.DepthWrite = true;
        Viewport((int)(display.Width * scale), (int)(display.Height * scale));
        SetFramebuffer(primary, keepViewport: true);
        if (primary != null) Viewport(primary.Width, primary.Height); // Retained override.
        else SelectDrawBuffer((OpenTK.Graphics.OpenGL.DrawBufferMode)1029);
    }

    /// <summary>Releases the supplied default framebuffer list and related published target state through the adapter owner.</summary>
    /// <param name="buffers">Target list whose owned resources should be released; null is accepted.</param>
    internal void DisposeFramebuffers(List<FrameBufferRef>? buffers)
    {
        var renderer = RequireDevice();
        if (buffers is null) return;
        // Validate the whole list before deleting any resource.
        bool anyLive = false;
        foreach (FrameBufferRef? target in buffers)
            if (target != null) anyLive |= !FramebufferOwnership(target).Released;
        if (!anyLive) return;
        var host = RequireFramebufferHost();
        host.ResetFrameGeneration();
        renderer.ResetGeneratedFramePresent();
        CloseUiScope();
        host.ReleaseAmbientOcclusionTargets();
        var deletedTextures = new HashSet<int>();
        var deletedTargets = new HashSet<int>();
        foreach (FrameBufferRef? target in buffers)
        {
            if (target is null) continue;
            var owner = FramebufferOwnership(target);
            if (owner.Released) continue;
            if (ReferenceEquals(currentFramebuffer, target)) SetFramebuffer(null, keepViewport: true);
            if (owner.Handle > 0 && deletedTargets.Add(owner.Handle))
            {
                Stated.ForgetFramebuffer(owner.Handle);
                renderer.DeleteFramebuffer(owner.Handle);
            }
            if (target.DepthTextureId > 0 && deletedTextures.Add(target.DepthTextureId))
                renderer.DeleteTexture(target.DepthTextureId);
            foreach (int texture in target.ColorTextureIds)
                if (texture > 0 && deletedTextures.Add(texture)) renderer.DeleteTexture(texture);
            owner.Released = true;
            target.Disposed = true;
        }
        // Rebuild assigns the newly constructed set before retiring the old
        // one. Do not erase the new set's published temporal/UI indices.
        if (ReferenceEquals(allocatedFramebuffers, buffers)) ResetFramebufferPublication();
    }
}

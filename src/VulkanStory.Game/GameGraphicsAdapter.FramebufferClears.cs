using OpenTK.Graphics.OpenGL;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    // Retained GL clear-color state is separate from the original platform's
    // private clearColor array used by ClearFrameBuffer(FrameBufferRef,bool).
    private float clearR, clearG, clearB, clearA;

    internal void SetClearColor(float r, float g, float b, float a)
    {
        RequireDevice();
        clearR = r; clearG = g; clearB = b; clearA = a;
    }

    internal void SelectDrawBuffer(DrawBufferMode selector)
    {
        int target = CurrentFramebufferHandle();
        int value = (int)selector;
        uint mask = value switch
        {
            0 => 0,
            1029 when target == PassDeclaration.DefaultFramebuffer => 1,
            36064 when target != PassDeclaration.DefaultFramebuffer => 1,
            _ => throw new NotSupportedException("Unsupported draw buffer for the current Vulkan framebuffer: " + value),
        };
        Stated.SetDrawBuffers(target, mask);
    }

    /// <summary>Translates the original draw-buffer selector array into a color-write mask for the currently bound target.</summary>
    /// <param name="count">Number of selector entries to apply.</param>
    /// <param name="selectors">Original GL-shaped color selectors for the current target.</param>
    internal void SelectDrawBuffers(int count, DrawBuffersEnum[] selectors)
    {
        int target = CurrentFramebufferHandle();
        ArgumentNullException.ThrowIfNull(selectors);
        if (count < 0 || count > selectors.Length || count > 32)
            throw new ArgumentOutOfRangeException(nameof(count));
        uint mask = 0;
        for (int slot = 0; slot < count; slot++)
        {
            int value = (int)selectors[slot];
            if (value == 0) continue;
            // The retained state uses identity attachment slots plus a mask.
            // Every targeted vanilla call uses this layout. Reordering needs
            // an explicit compatibility route rather than a silent remap.
            if (target == PassDeclaration.DefaultFramebuffer ? slot == 0 && value == 1029 : value == 36064 + slot)
                mask |= 1u << slot;
            else
                throw new NotSupportedException("Draw-buffer attachment remapping is not supported by this route.");
        }
        Stated.SetDrawBuffers(target, mask);
    }

    // Retained ClearTargetColor/ClearTargetDepth algorithms. The backend owns
    // pending clears, pass transitions and submission; game masks stay here.
    private void ClearTargetColor(int target, int slot, float r, float g, float b, float a)
    {
        var renderer = RequireDevice();
        if (target == 0) target = PassDeclaration.DefaultFramebuffer;
        if (((Stated.DrawBuffers(target) >> slot) & 1) == 0 || Stated.ColorMask == 0) return;
        renderer.ClearNativeColor(target, slot, r, g, b, a);
    }

    private void ClearTargetDepth(int target, float depth)
    {
        var renderer = RequireDevice();
        if (target == 0) target = PassDeclaration.DefaultFramebuffer;
        if (!Stated.DepthWrite) return;
        renderer.ClearNativeDepth(target, depth);
    }

    private static void RequireClearColor(float[] color)
    {
        ArgumentNullException.ThrowIfNull(color);
        if (color.Length < 4) throw new ArgumentException("A framebuffer clear needs four color components.", nameof(color));
    }

    internal void ClearCurrentColor(int slot, float[] color)
    {
        int target = CurrentFramebufferHandle();
        if ((uint)slot >= 32) throw new ArgumentOutOfRangeException(nameof(slot));
        RequireClearColor(color);
        ClearTargetColor(target, slot, color[0], color[1], color[2], color[3]);
    }

    internal void ClearFramebuffer(FrameBufferRef target, bool depth)
    {
        RequireDevice();
        ClearFramebuffer(target, GameFramebufferBindings.ClearColor(platform!), depth, colors: true);
    }

    internal void ClearFramebuffer(FrameBufferRef target, float[] color, bool depth, bool colors)
    {
        RequireDevice();
        ArgumentNullException.ThrowIfNull(target);
        int handle = LiveFramebufferHandle(target);
        if (colors) RequireClearColor(color);
        SetFramebuffer(target, keepViewport: true);
        if (colors)
            for (int slot = 0; slot < target.ColorTextureIds.Length; slot++)
                ClearTargetColor(handle, slot, color[0], color[1], color[2], color[3]);
        if (depth) ClearTargetDepth(handle, 1f);
    }

    private FrameBufferRef FramebufferAt(EnumFrameBuffer kind)
    {
        var buffers = platform!.FrameBuffers;
        int index = (int)kind;
        if (buffers is null || (uint)index >= buffers.Count || buffers[index] is null)
            throw new InvalidOperationException("The game framebuffer set is not ready: " + kind);
        FrameBufferRef target = buffers[index];
        _ = LiveFramebufferHandle(target);
        return target;
    }

    internal void ClearFramebuffer(EnumFrameBuffer kind)
    {
        int target = CurrentFramebufferHandle();
        switch (kind)
        {
            case EnumFrameBuffer.Default:
            {
                // Preserve the original method's temporary default bind and
                // subsequent primary bind, both without changing viewport.
                FrameBufferRef primary = FramebufferAt(EnumFrameBuffer.Primary);
                SetFramebuffer(null, keepViewport: true);
                SelectDrawBuffer((DrawBufferMode)1029);
                try
                {
                    ClearTargetColor(PassDeclaration.DefaultFramebuffer, 0, clearR, clearG, clearB, clearA);
                    ClearTargetDepth(PassDeclaration.DefaultFramebuffer, 1f);
                }
                finally { SetFramebuffer(primary, keepViewport: true); }
                break;
            }
            case EnumFrameBuffer.Primary:
            {
                int motion = FrameState.MotionAttachment;
                int primaryHandle = 0;
                if (motion >= 0)
                {
                    if (motion >= 32) throw new ArgumentOutOfRangeException(nameof(GameGraphicsFrameState.MotionAttachment));
                    primaryHandle = LiveFramebufferHandle(FramebufferAt(EnumFrameBuffer.Primary));
                }
                ClearTargetColor(target, 0, 0f, 0f, 0f, 1f);
                ClearTargetColor(target, 1, 0f, 0f, 0f, 1f);
                if (FrameState.Ssao)
                {
                    ClearTargetColor(target, 2, 0f, 0f, 0f, 1f);
                    ClearTargetColor(target, 3, 0f, 0f, 0f, 1f);
                }
                if (motion >= 0)
                {
                    Stated.SetDrawBuffers(primaryHandle, motion == 31 ? uint.MaxValue : (1u << (motion + 1)) - 1);
                    try { ClearTargetColor(target, motion, 0f, 0f, 0f, 0f); }
                    finally { Stated.SetDrawBuffers(primaryHandle, (1u << motion) - 1); }
                }
                ClearTargetDepth(target, 1f);
                break;
            }
            case EnumFrameBuffer.LiquidDepth:
            case EnumFrameBuffer.ShadowmapFar:
            case EnumFrameBuffer.ShadowmapNear:
            {
                FrameBufferRef sized = FramebufferAt(kind);
                Stated.Viewport = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)sized.Width, (uint)sized.Height));
                ClearTargetDepth(target, 1f);
                break;
            }
            case EnumFrameBuffer.Transparent:
                ClearTargetColor(target, 0, 0f, 0f, 0f, 0f);
                ClearTargetColor(target, 1, 1f, 0f, 0f, 0f);
                ClearTargetColor(target, 2, 0f, 0f, 0f, 0f);
                break;
        }
    }
}

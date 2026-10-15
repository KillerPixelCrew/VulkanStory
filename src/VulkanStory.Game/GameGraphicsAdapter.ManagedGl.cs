using Silk.NET.Vulkan;
using Vintagestory.Client;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal VulkanDevice ManagedGlDevice => RequireDevice();

    // The caller has already snapshotted the VAO's CPU streams into an owned mesh.
    // Recording uses the same target, uniforms, descriptors and pipeline path as
    // other stated draws; it never creates a second primary frame.
    internal void RecordManagedGlDraw(int mesh, int program, StatedRenderState state,
        int target, bool primary)
    {
        var renderer = RequireDevice();
        // Intermediate images retain GL row order. Viewport, scissor and winding
        // use the existing renderer convention; presentation flips the rows once.
        PassDeclaration? declaration = primary
            ? StatedPass?.FramebufferId == target ? StatedPass : StageDrawDeclaration()
            : null;
        if (!StatedDraw.Record(renderer, state, program, target, mesh, 1, null, null, 0,
            out string? refusal, declaration, requirePipeline: true))
            throw new InvalidOperationException("Managed GL draw refused: " + refusal);
        StatedDraws++;
        RuntimeStats.drawCallsCount++;
    }

    internal void ClearManagedGlTarget(int target, StatedRenderState state, int mask, float[] color)
    {
        var renderer = RequireDevice();
        if (state.ScissorEnabled)
            throw new NotSupportedException("Managed GL scissored clears require an attachment-rectangle clear capability.");
        const ColorComponentFlags all = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
            ColorComponentFlags.BBit | ColorComponentFlags.ABit;
        if ((mask & 0x4000) != 0 && state.ColorMask != 0)
        {
            if (state.ColorMask != all)
                throw new NotSupportedException("Managed GL masked color clears require a masked attachment-clear capability.");
            uint slots = state.DrawBuffers(target);
            for (int slot = 0; slot < StatedRenderState.MaxColorAttachments; slot++)
                if ((slots & (1u << slot)) != 0)
                    renderer.ClearNativeColor(target, slot, color[0], color[1], color[2], color[3]);
        }
        if ((mask & 0x0100) != 0 && state.DepthWrite) renderer.ClearNativeDepth(target, 1f);
        // None of this session's render targets has a stencil attachment.
        // GL ignores clear bits for missing buffers (secondary windows request
        // color|depth|stencil even when their surface has only color).
    }
}

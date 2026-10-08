using Vintagestory.API.Client;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>Per-frame switches that supplement the retained graphics state without modifying the original platform.</summary>
internal sealed record GameGraphicsFrameState(bool Ssao, bool MotionWriteActive, int MotionAttachment);

internal sealed partial class GameGraphicsAdapter
{
    // The retained frame/post-chain owner supplies these flags at stage transitions.
    internal GameGraphicsFrameState FrameState { get; set; } = new(false, false, -1);
    internal StatedRenderState RequireStatedState() { RequireDevice(); return Stated; }
    /// <summary>Applies the original blend-mode choice to the retained stated render state used by subsequent draws.</summary>
    /// <param name="on">Whether blending is enabled.</param>
    /// <param name="mode">Original blend mode translated into retained attachment blend state.</param>
    internal void ToggleBlend(bool on, EnumBlendMode mode)
    {
        RequireDevice();
        Stated.SetBlendEnabled(on);
        if (!on) return; // Original glDisable preserves configured factors.
        RenderBlendMode neutral = mode switch
        {
            EnumBlendMode.Multiply => RenderBlendMode.Multiply,
            EnumBlendMode.Brighten => RenderBlendMode.Brighten,
            EnumBlendMode.PremultipliedAlpha => RenderBlendMode.PremultipliedAlpha,
            EnumBlendMode.Glow => RenderBlendMode.Glow,
            EnumBlendMode.Overlay => RenderBlendMode.Overlay,
            _ => RenderBlendMode.Standard,
        };
        Stated.SetBlendMode(neutral);
        if (mode == EnumBlendMode.Standard && FrameState.Ssao)
        {
            Stated.SetSlotBlend(2, 32774, 1, 0, 1, 0);
            Stated.SetSlotBlend(3, 32774, 1, 0, 1, 0);
        }
        if (FrameState.MotionWriteActive && FrameState.MotionAttachment >= 0)
            Stated.SetSlotBlend(FrameState.MotionAttachment, 32774, 1, 0, 1, 0);
    }
}

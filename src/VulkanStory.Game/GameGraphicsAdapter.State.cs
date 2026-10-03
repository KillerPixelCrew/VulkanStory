using Vintagestory.API.Client;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed record GameGraphicsFrameState(bool Ssao, bool MotionWriteActive, int MotionAttachment);

internal sealed partial class GameGraphicsAdapter
{
    // The retained frame/post-chain owner supplies these flags at stage transitions.
    internal GameGraphicsFrameState FrameState { get; set; } = new(false, false, -1);
    internal StatedRenderState RequireStatedState() { RequireDevice(); return Stated; }
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

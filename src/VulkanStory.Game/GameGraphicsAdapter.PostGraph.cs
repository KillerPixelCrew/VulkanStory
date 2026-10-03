using Vintagestory.API.Client;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    // Retained Post branch of VulkanClientPlatform.Graph.PassReads and
    // PassTransientSlots, baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
    private int PostTargetIndex(int handle)
    {
        var targets = platform!.FrameBuffers;
        if (handle <= 0 || targets == null) return -1;
        for (int index = 0; index < targets.Count; index++)
            if (targets[index]?.FboId == handle) return index;
        return -1;
    }

    private static uint PostTransientSlots(int index) => index switch
    {
        (int)EnumFrameBuffer.FindBright or (int)EnumFrameBuffer.BlurHorizontalMedRes or
        (int)EnumFrameBuffer.BlurVerticalMedRes or (int)EnumFrameBuffer.BlurHorizontalLowRes or
        (int)EnumFrameBuffer.BlurVerticalLowRes or (int)EnumFrameBuffer.SSAOBlurHorizontal or
        (int)EnumFrameBuffer.SSAOBlurVertical => 1u,
        _ => 0u,
    };

    private int[] PostReadTextures(int target)
    {
        var reads = new List<int>();
        void Add(int id) { if (id > 0 && !reads.Contains(id)) reads.Add(id); }
        void Color(EnumFrameBuffer buffer, int slot = 0) => Add(ColourOf((int)buffer, slot));
        void Depth() => Add(DepthOf(PrimaryIndex));
        switch (target)
        {
            case (int)EnumFrameBuffer.FindBright:
            case (int)EnumFrameBuffer.GodRays:
            case (int)EnumFrameBuffer.Luma:
                Color(EnumFrameBuffer.Primary);
                Color(EnumFrameBuffer.Primary, 1);
                for (int slot = 0; slot < 2; slot++)
                {
                    Add(ColourOf(TaaHistoryIndexA, slot));
                    Add(ColourOf(TaaHistoryIndexB, slot));
                }
                break;
            case (int)EnumFrameBuffer.BlurHorizontalMedRes:
                Color(EnumFrameBuffer.FindBright); break;
            case (int)EnumFrameBuffer.BlurVerticalMedRes:
                Color(EnumFrameBuffer.BlurHorizontalMedRes); break;
            case (int)EnumFrameBuffer.BlurHorizontalLowRes:
                Color(EnumFrameBuffer.BlurVerticalMedRes); break;
            case (int)EnumFrameBuffer.BlurVerticalLowRes:
                Color(EnumFrameBuffer.BlurHorizontalLowRes); break;
            case (int)EnumFrameBuffer.SSAO:
                Color(EnumFrameBuffer.Primary, 2);
                Color(EnumFrameBuffer.Primary, 3);
                Color(EnumFrameBuffer.SSAO, 1);
                Color(EnumFrameBuffer.Transparent, 1);
                break;
            case (int)EnumFrameBuffer.SSAOBlurHorizontal:
                Color(EnumFrameBuffer.SSAO);
                Color(EnumFrameBuffer.SSAOBlurVertical);
                Depth();
                break;
            case (int)EnumFrameBuffer.SSAOBlurVertical:
                Color(EnumFrameBuffer.SSAOBlurHorizontal); break;
        }
        return reads.ToArray();
    }
}

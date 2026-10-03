using Vintagestory.API.Client;

namespace VulkanStory.Game;

/// <summary>
/// Staged game render-stage bracket from the original FrameGraph boundary.
/// The game adapter maps each stage and target to a backend pass declaration.
/// </summary>
internal interface IRenderStageListener
{
    void OnBeginRenderStage(EnumRenderStage stage);
    void OnEndRenderStage(EnumRenderStage stage);
}

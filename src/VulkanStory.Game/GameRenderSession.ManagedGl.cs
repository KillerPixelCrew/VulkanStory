namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private ManagedGlBridge? managedGl;
    internal ManagedGlBridge ManagedGl => managedGl ??= new ManagedGlBridge(this);
}

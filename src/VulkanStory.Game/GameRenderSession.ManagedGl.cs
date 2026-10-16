namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private ManagedGlBridge? managedGl;
    internal ManagedGlBridge ManagedGl => managedGl ??= new ManagedGlBridge(this);
    internal void ReportNativeGlFailure(string detail) => platform.Logger.Error("VulkanStory native GL dispatch failed: {0}", detail);
}

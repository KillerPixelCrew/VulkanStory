namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Registers the shared provider resolver for native imports remaining in the renderer.</summary>
internal static class RendererNativeLibrary
{
    static RendererNativeLibrary() => ReScaleFrame.Vulkan.VulkanNativeLibrary.RegisterHostAssembly(typeof(RendererNativeLibrary).Assembly);
    internal static void EnsureRegistered() { }
    internal static string? NgxPath => ReScaleFrame.Vulkan.VulkanNativeLibrary.NgxPath;
}

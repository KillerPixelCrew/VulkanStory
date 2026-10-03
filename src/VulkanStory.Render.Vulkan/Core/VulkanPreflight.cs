namespace VulkanStory.Render.Vulkan.Core;

/// <summary>A windowless check of the renderer's actual Vulkan device floor.</summary>
public static class VulkanPreflight
{
    /// <returns>Null when a usable Vulkan device can be created; otherwise the renderer's rejection reason.</returns>
    public static string? Check()
    {
        if (!VulkanContext.TryCreate(new VulkanContextOptions { Headless = true },
                out VulkanContext? context, out string? failureReason))
            return failureReason ?? "Vulkan initialization failed for an unknown reason";
        context?.Dispose();
        return null;
    }
}

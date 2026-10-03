using Silk.NET.Vulkan;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Create and release an SDL3 Vulkan surface without starting the game.</summary>
public static class VulkanWindowPreflight
{
    public static string? Check()
    {
        try
        {
            using SdlWindowHost window = SdlWindowHost.Create(
                "VulkanStory Vulkan surface preflight", 128, 96, hidden: true);
            var source = new SdlVulkanWindowSurface(window);
            var options = new VulkanContextOptions
            {
                RequiredInstanceExtensions = source.RequiredInstanceExtensions()
            };
            if (!VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason))
                return reason ?? "Vulkan context creation failed";

            SurfaceKHR surface = default;
            try
            {
                return source.TryCreate(context!, out surface, out reason)
                    ? null : reason ?? "SDL3 Vulkan surface creation failed";
            }
            finally
            {
                WindowSurface.Destroy(context!, surface);
                context!.Dispose();
            }
        }
        catch (Exception error)
        {
            return error.GetType().Name + ": " + error.Message;
        }
    }
}

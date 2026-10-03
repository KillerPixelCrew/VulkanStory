using Silk.NET.Vulkan;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Create and retire an SDL-backed Vulkan swapchain without the game.</summary>
public static class VulkanSwapchainPreflight
{
    public static string? Check()
    {
        try
        {
            using SdlWindowHost window = SdlWindowHost.Create(
                "VulkanStory swapchain preflight", 128, 96, hidden: true);
            var source = new SdlVulkanWindowSurface(window);
            var options = new VulkanContextOptions
            {
                RequiredInstanceExtensions = source.RequiredInstanceExtensions()
            };
            if (!VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason))
                return reason ?? "Vulkan context creation failed";

            SurfaceKHR surface = default;
            FrameTimeline? timeline = null;
            Swapchain? swapchain = null;
            try
            {
                if (!source.TryCreate(context!, out surface, out reason))
                    return reason ?? "SDL3 Vulkan surface creation failed";
                timeline = new FrameTimeline(context!);
                // Swapchain.TryCreate owns the surface on entry, including failure.
                SurfaceKHR handoff = surface;
                surface = default;
                if (!Swapchain.TryCreate(context!, handoff, 128, 96, true, timeline,
                        out swapchain, out reason))
                    return reason ?? "Vulkan swapchain creation failed";
                return null;
            }
            finally
            {
                swapchain?.Dispose();
                if (surface.Handle != 0) WindowSurface.Destroy(context!, surface);
                timeline?.Dispose();
                context!.Dispose();
            }
        }
        catch (Exception error)
        {
            return error.GetType().Name + ": " + error.Message;
        }
    }
}

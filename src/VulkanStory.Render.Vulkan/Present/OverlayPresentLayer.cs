using System;
using System.IO;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Discovers the application's WSI layer without registry installation or changing other applications.</summary>
internal static class OverlayPresentLayer
{
    internal const string Name = "VK_LAYER_VULKANSTORY_present";
    internal static bool Prepare()
    {
        if (!OperatingSystem.IsWindows()) return false;
        string directory = NativeRuntimePaths.DirectoryContaining("VulkanStoryWsi.dll", "VulkanStoryWsi.json");
        if (!File.Exists(Path.Combine(directory, "VulkanStoryWsi.dll")) ||
            !File.Exists(Path.Combine(directory, "VulkanStoryWsi.json")))
        {
            if (NativeRuntimePaths.IsDeployedPayload)
                throw new FileNotFoundException("The Vulkan presentation layer is missing from the runtime package.");
            return false;
        }
        // VK_LAYER_PATH overrides VK_ADD_LAYER_PATH. Preserve the caller's
        // search directories and add only this process's private bundle.
        // The loader treats even a whitespace override as present and ignores
        // VK_ADD_LAYER_PATH in that case.
        string variable = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_LAYER_PATH"))
            ? "VK_ADD_LAYER_PATH" : "VK_LAYER_PATH";
        string existing = Environment.GetEnvironmentVariable(variable) ?? "";
        foreach (string path in existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                if (string.Equals(Path.GetFullPath(path), directory, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Caller entries remain unchanged; a failed duplicate check
                // must not prevent adding our required private directory.
            }
        }
        Environment.SetEnvironmentVariable(variable,
            existing.Length == 0 ? directory : directory + Path.PathSeparator + existing,
            EnvironmentVariableTarget.Process);
        return true;
    }
}

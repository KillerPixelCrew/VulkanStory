using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Optimum.Launcher;

/// <summary>Check an explicit Vulkan request before starting game code.</summary>
internal static class VulkanLaunchPreflight
{
    internal static bool IsExplicitlyRequested(string dataPath)
    {
        string path = Path.Combine(dataPath, "ModConfig", "optimum.json");
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 1024 * 1024) return false;
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            bool requested = false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                // OptimumConfig uses the default case-sensitive JSON deserializer;
                // when a key occurs twice, its last value wins there as well.
                if (property.Name != "Renderer") continue;
                requested = property.Value.ValueKind == JsonValueKind.String &&
                    string.Equals(property.Value.GetString()?.Trim(), "vulkan", StringComparison.OrdinalIgnoreCase);
            }
            return requested;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return false;
    }

    internal static string? Check(string gameDirectory)
    {
        try
        {
            string rendererPath = Path.Combine(gameDirectory, "Optimum.Render.Vulkan.dll");
            if (!File.Exists(rendererPath))
                return "Optimum.Render.Vulkan.dll is missing. Repair or reinstall Optimum.";
            Assembly renderer = Assembly.LoadFrom(rendererPath);
            MethodInfo check = renderer.GetType("Optimum.Render.Vulkan.Core.VulkanPreflight", throwOnError: true)!
                .GetMethod("Check", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("The Vulkan preflight entry point is missing.");
            return check.Invoke(null, null) as string;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or FileNotFoundException or
                                   FileLoadException or TypeLoadException or MissingMethodException)
        {
            return ex.Message;
        }
    }
}

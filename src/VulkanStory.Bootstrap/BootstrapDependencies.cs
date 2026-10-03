using System.Reflection;
using System.Runtime.Loader;

namespace VulkanStory.Bootstrap;

internal sealed class BootstrapDependencies(string managedDirectory, string gameDirectory,
    Action<string, string>? trace = null)
{
    private readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VulkanStory.Game"] = Path.Combine(managedDirectory, "VulkanStory.Game.dll"),
        ["VulkanStory.Contracts"] = Path.Combine(managedDirectory, "VulkanStory.Contracts.dll"),
        ["VulkanStory.Input"] = Path.Combine(managedDirectory, "VulkanStory.Input.dll"),
        ["VulkanStory.Platform.Sdl"] = Path.Combine(managedDirectory, "VulkanStory.Platform.Sdl.dll"),
        ["VulkanStory.Render.Vulkan"] = Path.Combine(managedDirectory, "VulkanStory.Render.Vulkan.dll"),
        ["0Harmony"] = Path.Combine(gameDirectory, "Lib", "0Harmony.dll"),
        ["VintagestoryLib"] = Path.Combine(gameDirectory, "VintagestoryLib.dll"),
        ["VintagestoryAPI"] = Path.Combine(gameDirectory, "VintagestoryAPI.dll")
    };

    // The original ClientProgram.Main installs the game's resolver after this hook
    // returns. Reflection over its method signatures needs the same shipped Lib
    // dependencies before Main, while graphics and game statics remain untouched.
    private readonly string[] gameSearchPaths = [gameDirectory, Path.Combine(gameDirectory, "Lib")];

    // Keep this list aligned with stage-runtime.ps1. No mod-directory probing:
    // these are the private graphics dependencies of the prepared profile.
    private static readonly HashSet<string> privateDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "SDL3-CS", "Silk.NET.Core", "Silk.NET.Shaderc", "Silk.NET.Vulkan",
        "Silk.NET.Vulkan.Extensions.EXT", "Silk.NET.Vulkan.Extensions.KHR",
        "Microsoft.DotNet.PlatformAbstractions", "Microsoft.Extensions.DependencyModel"
    };

    internal void Register() => AssemblyLoadContext.Default.Resolving += Resolve;
    internal void Unregister() => AssemblyLoadContext.Default.Resolving -= Resolve;

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName requested)
    {
        string? path = Locate(requested);
        if (path is null) return null;
        trace?.Invoke("managed.dependency.resolved", requested.Name + " -> " + path);
        return context.LoadFromAssemblyPath(path);
    }

    internal string? Locate(AssemblyName requested)
    {
        string? name = requested.Name;
        if (string.IsNullOrEmpty(name) || name.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            return null;

        if (paths.TryGetValue(name, out string? pinned))
            return VerifyIdentity(requested, pinned);

        if (privateDependencies.Contains(name))
            return VerifyIdentity(requested, Path.Combine(managedDirectory, name + ".dll"));

        // Resolve only from the official game's own binary and Lib folders.
        // Other mods are discovered by the game's loader at its normal phase.
        foreach (string directory in gameSearchPaths)
        {
            string candidate = Path.Combine(directory, name + ".dll");
            if (File.Exists(candidate)) return VerifyIdentity(requested, candidate);
        }
        return null;
    }

    private static string VerifyIdentity(AssemblyName requested, string path)
    {
        AssemblyName actual = AssemblyName.GetAssemblyName(path);
        if (!AssemblyName.ReferenceMatchesDefinition(requested, actual) ||
            (requested.Version is not null && actual.Version is not null && actual.Version < requested.Version))
            throw new FileLoadException($"Bootstrap dependency identity mismatch: {requested.Name}", path);
        return Path.GetFullPath(path);
    }
}

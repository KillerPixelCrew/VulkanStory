using System.Reflection;
using System.Runtime.Loader;

namespace VulkanStory.Bootstrap;

/// <summary>Resolves early managed dependencies from pinned payload paths and the official game's binary folders.</summary>
/// <param name="managedDirectory">Directory containing the private VulkanStory managed payload.</param>
/// <param name="gameDirectory">Official client installation root.</param>
/// <param name="trace">Optional callback for successful resolution diagnostics.</param>
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

    /// <summary>Attaches this resolver to the existing default load context.</summary>
    internal void Register() => AssemblyLoadContext.Default.Resolving += Resolve;
    /// <summary>Detaches this resolver; already loaded assemblies remain in the default context.</summary>
    internal void Unregister() => AssemblyLoadContext.Default.Resolving -= Resolve;

    /// <summary>Loads a verified dependency path into the requesting load context when a candidate exists.</summary>
    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName requested)
    {
        string? path = Locate(requested);
        if (path is null) return null;
        trace?.Invoke("managed.dependency.resolved", requested.Name + " -> " + path);
        return context.LoadFromAssemblyPath(path);
    }

    /// <summary>Finds and verifies an allowed early dependency without loading its assembly.</summary>
    /// <param name="requested">Assembly identity requested by the runtime.</param>
    /// <returns>An absolute candidate path, or null for an invalid name or an absent unpinned dependency.</returns>
    /// <exception cref="FileLoadException">A located dependency does not satisfy the requested identity/version.</exception>
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

    /// <summary>Checks assembly metadata and returns an absolute path, preserving missing-file and metadata failures.</summary>
    private static string VerifyIdentity(AssemblyName requested, string path)
    {
        AssemblyName actual = AssemblyName.GetAssemblyName(path);
        if (!AssemblyName.ReferenceMatchesDefinition(requested, actual) ||
            (requested.Version is not null && actual.Version is not null && actual.Version < requested.Version))
            throw new FileLoadException($"Bootstrap dependency identity mismatch: {requested.Name}", path);
        return Path.GetFullPath(path);
    }
}

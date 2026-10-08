using System.Reflection;
using HarmonyLib;
using Vintagestory.Common;

namespace VulkanStory.Game;

/// <summary>Installs the isolated harness mod-discovery hook that excludes candidate-local development trees from ordinary game scanning.</summary>
internal static class HeadlessModDiscovery
{
    /// <summary>Resolves the pinned original mod-discovery target for the isolated harness.</summary>
    /// <returns>Original discovery method to patch; unsupported shapes throw.</returns>
    internal static MethodInfo Validate()
    {
        var method = AccessTools.Method(typeof(ModLoader), "CollectMods", []) ??
            throw new MissingMethodException("Official mod collection method is missing.");
        if (method.ReturnType != typeof(List<ModContainer>) || method.IsStatic || method.GetMethodBody() == null)
            throw new InvalidOperationException("Official mod collection signature changed.");
        string staged = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_MOD_DIRECTORY") ??
            throw new InvalidOperationException("Headless staged mod directory is missing.");
        if (!Path.IsPathFullyQualified(staged) || !File.Exists(Path.Combine(staged, "modinfo.json")))
            throw new InvalidOperationException("Headless staged mod directory is invalid.");
        return method;
    }

    /// <summary>Removes development-tree candidates from original discovery results in explicitly enabled headless mode.</summary>
    /// <param name="__result">Original mutable discovered-mod list.</param>
    internal static void Filter(List<ModContainer> __result)
    {
        if (!HeadlessHarnessOptions.Enabled) return;
        string installed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Mods", "vulkanstory"));
        string installedInput = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Mods", "vulkanstoryinput"));
        // Before info/assembly discovery, remove only the standard installed
        // VulkanStory folders. The ordinary loader finds the isolated staged copies.
        __result.RemoveAll(mod => string.Equals(Path.GetFullPath(mod.SourcePath), installed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(mod.SourcePath), installedInput, StringComparison.OrdinalIgnoreCase));
    }

    internal static string LoadedModLocation() => string.Join(";", AppDomain.CurrentDomain.GetAssemblies()
        .Where(assembly => assembly.GetName().Name == "VulkanStory.Mod")
        .Select(assembly => assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase));

    internal static bool IsStagedModLoaded(string location)
    {
        string? directory = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_MOD_DIRECTORY");
        return directory != null && string.Equals(location,
            Path.GetFullPath(Path.Combine(directory, "VulkanStory.Mod.dll")), StringComparison.OrdinalIgnoreCase);
    }
}

using System.Reflection;
using Vintagestory.API.Config;

namespace VulkanStory.Game;

/// <summary>Binds the official 1.22.7 startup profile and original built-in assemblies before Main, deferring data-path selection until window startup.</summary>
internal static class VersionProfile1227
{
    /// <summary>Loads the original built-in renderer assemblies and prepares complete supported startup routing.</summary>
    /// <param name="runtime">Process runtime receiving the complete plan.</param>
    /// <param name="gameDirectory">Official installation containing the built-in Mods assemblies.</param>
    /// <remarks>The service factory reads GamePaths only at the window boundary, after normal argument/path setup.</remarks>
    internal static void Prepare(ProcessRuntime runtime, string gameDirectory)
    {
        string root = Path.GetFullPath(gameDirectory);
        Assembly essentials = OriginalBuiltin(root, "VSEssentials");
        Assembly survival = OriginalBuiltin(root, "VSSurvivalMod");
        // The startup hook runs before Main parses arguments/configures paths.
        // Resolve the game's actual data path only when it requests its first window.
        var plan = StartupProfileComposition.Create1227(runtime,
            () => GameSessionServices.Load(GamePaths.DataPath), essentials, survival);
        runtime.Prepare(plan);
    }
    private static Assembly OriginalBuiltin(string gameDirectory, string name)
    {
        string path = Path.GetFullPath(Path.Combine(gameDirectory, "Mods", name + ".dll"));
        if (!File.Exists(path)) throw new FileNotFoundException("Required original built-in renderer assembly is missing.", path);
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(candidate => candidate.GetName().Name == name)
            ?? Assembly.LoadFrom(path);
        if (assembly.GetName().Name != name || !string.Equals(Path.GetFullPath(assembly.Location), path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Built-in renderer assembly is loaded from an unexpected location: " + name);
        return assembly;
    }
}

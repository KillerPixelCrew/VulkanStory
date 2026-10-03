using System.Reflection;
using Vintagestory.API.Config;

namespace VulkanStory.Game;

internal static class VersionProfile1227
{
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

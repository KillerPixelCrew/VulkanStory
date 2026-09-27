using System;
using System.IO;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The launcher loads the renderer from its patched-assembly cache, while native
/// runtimes are packaged beside the executable. Standalone tests may instead
/// copy them beside the renderer assembly.
/// </summary>
internal static class NativeRuntimePaths
{
    internal static string DirectoryContaining(params string[] files)
    {
        string application = AppContext.BaseDirectory;
        if (ContainsAll(application, files)) return application;
        string assembly = Path.GetDirectoryName(typeof(NativeRuntimePaths).Assembly.Location) ?? application;
        if (ContainsAll(assembly, files)) return assembly;
        return application;
    }

    private static bool ContainsAll(string directory, string[] files)
    {
        foreach (string file in files)
            if (!File.Exists(Path.Combine(directory, file))) return false;
        return true;
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// Locate native provider bridges and SDK runtimes from VulkanStory's private
/// package directory. Standalone development may keep them beside the assembly.
/// </summary>
internal static class NativeRuntimePaths
{
    internal static string AssemblyDirectory =>
        Path.GetDirectoryName(typeof(NativeRuntimePaths).Assembly.Location) ?? AppContext.BaseDirectory;

    internal static bool IsDeployedPayload =>
        string.Equals(Path.GetFileName(AssemblyDirectory), "managed", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileName(Path.GetDirectoryName(AssemblyDirectory)), "VulkanStory",
            StringComparison.OrdinalIgnoreCase);

    internal static string? PackageNativeDirectory
    {
        get
        {
            string? rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => OperatingSystem.IsWindows() ? "win-x64" :
                    OperatingSystem.IsLinux() ? "linux-x64" : null,
                Architecture.Arm64 => OperatingSystem.IsWindows() ? "win-arm64" :
                    OperatingSystem.IsLinux() ? "linux-arm64" : null,
                _ => null
            };
            return rid is null ? null : Path.Combine(
                Path.GetFullPath(Path.Combine(AssemblyDirectory, "..")), "native", rid);
        }
    }

    internal static string DirectoryContaining(params string[] files)
    {
        foreach (string file in files)
            if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file)
                throw new ArgumentException("Native runtime names must be filenames.", nameof(files));

        string assembly = AssemblyDirectory;
        string? packaged = PackageNativeDirectory;
        if (packaged is not null)
        {
            if (ContainsAll(packaged, files)) return packaged;
            // A released payload must not accidentally load a similarly named
            // bridge from the game folder or another mod.
            if (IsDeployedPayload)
                return packaged;
        }
        if (ContainsAll(assembly, files)) return assembly;
        string application = AppContext.BaseDirectory;
        if (ContainsAll(application, files)) return application;
        return assembly;
    }

    private static bool ContainsAll(string directory, string[] files)
    {
        foreach (string file in files)
            if (!File.Exists(Path.Combine(directory, file))) return false;
        return true;
    }

    // SDK redistributables often have sibling DLL dependencies. Keep resolution
    // local to the selected module without changing the process DLL search path.
    internal static nint LoadLibrary(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Native runtime paths must be absolute.", nameof(path));
        return NativeLibrary.Load(path, typeof(NativeRuntimePaths).Assembly,
            OperatingSystem.IsWindows()
                ? DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories
                : null);
    }

    internal static bool TryLoadLibrary(string path, out nint handle)
    {
        handle = 0;
        try { handle = LoadLibrary(path); return true; }
        catch (DllNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }
}

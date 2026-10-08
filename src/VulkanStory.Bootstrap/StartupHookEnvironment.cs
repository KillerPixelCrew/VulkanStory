namespace VulkanStory.Bootstrap;

/// <summary>Removes only VulkanStory's absolute startup-hook path from the process environment before child launches.</summary>
internal static class StartupHookEnvironment
{
    // Only remove this assembly's absolute-path entry; leave other hook names/paths intact.
    /// <summary>Filters this assembly's absolute path while retaining other hooks and relative/name entries.</summary>
    /// <param name="hooks">Platform-separated hook entries, or null for an unset variable.</param>
    /// <param name="ownPath">Absolute path identifying VulkanStory's startup-hook assembly.</param>
    /// <returns>The filtered entries, preserving null when the original variable is unset.</returns>
    internal static string? WithoutOwnHook(string? hooks, string ownPath)
    {
        if (hooks is null) return null;
        string[] entries = hooks.Split(Path.PathSeparator);
        return string.Join(Path.PathSeparator, entries.Where(entry =>
            !Path.IsPathFullyQualified(entry) ||
            !string.Equals(Path.GetFullPath(entry), Path.GetFullPath(ownPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
    }

    /// <summary>Updates DOTNET_STARTUP_HOOKS only when filtering removes this assembly's entry.</summary>
    /// <param name="ownPath">Path identifying the current startup-hook assembly.</param>
    internal static void RemoveOwnHook(string ownPath)
    {
        string? previous = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        string? filtered = WithoutOwnHook(previous, ownPath);
        if (!string.Equals(previous, filtered, StringComparison.Ordinal))
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", filtered);
    }
}

namespace VulkanStory.Bootstrap;

internal static class StartupHookEnvironment
{
    // Only remove this assembly's absolute-path entry; leave other hook names/paths intact.
    internal static string? WithoutOwnHook(string? hooks, string ownPath)
    {
        if (hooks is null) return null;
        string[] entries = hooks.Split(Path.PathSeparator);
        return string.Join(Path.PathSeparator, entries.Where(entry =>
            !Path.IsPathFullyQualified(entry) ||
            !string.Equals(Path.GetFullPath(entry), Path.GetFullPath(ownPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
    }

    internal static void RemoveOwnHook(string ownPath)
    {
        string? previous = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        string? filtered = WithoutOwnHook(previous, ownPath);
        if (!string.Equals(previous, filtered, StringComparison.Ordinal))
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", filtered);
    }
}

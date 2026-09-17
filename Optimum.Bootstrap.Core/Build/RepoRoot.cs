using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Build;

/// <summary>
/// Finds the Optimum checkout the engine has to drive: the nearest directory at
/// or above a starting point that holds the manifest and the scripts required
/// by the probed platform. Both front ends need this because the build pipeline
/// is still the platform scripts (INSTALLER-PLAN.md section 2).
/// </summary>
public static class RepoRoot
{
    public static string? Discover(ISystemProbe probe, string? explicitRoot = null)
    {
        // An explicit root is already an absolute path in the probe's world, so
        // walk it as given. Only the implicit case needs the real working
        // directory. Resolving an explicit path through Path.GetFullPath would
        // rewrite a POSIX path against the host drive on Windows, which is wrong
        // whenever the probe models a different platform (and in tests).
        string start = explicitRoot ?? Directory.GetCurrentDirectory();

        for (string? dir = start; dir is not null; dir = ParentOf(dir))
        {
            if (SourceCache.IsUsableCheckout(probe, dir))
                return dir;
        }

        return null;
    }

    /// <summary>
    /// The parent of a path, honouring both separators so a POSIX path resolves
    /// the same way regardless of the host the binary runs on. Returns null at
    /// the root so the walk terminates.
    /// </summary>
    private static string? ParentOf(string dir)
    {
        int cut = dir.TrimEnd('/', '\\').LastIndexOfAny(['/', '\\']);
        if (cut < 0)
            return null;
        return cut == 0 ? dir[..1] : dir[..cut];
    }
}

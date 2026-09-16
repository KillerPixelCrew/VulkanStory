using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.DataPath;

public sealed record DataPathDetection(string? Path, bool HasActiveSession);

/// <summary>
/// Session-aware detection of an existing Vintage Story data folder. Ports
/// <c>prompt_data_path</c> from <c>scripts/install-linux.sh</c> (which Windows and
/// macOS never had) and widens the candidate list per platform: a folder whose
/// <c>clientsettings.json</c> carries a <c>playeruid</c> wins over one that merely
/// exists.
/// </summary>
public static class DataPathProbe
{
    public static DataPathDetection Detect(ISystemProbe probe)
    {
        string[] candidates = Candidates(probe);

        foreach (string dir in candidates)
        {
            string settings = System.IO.Path.Combine(dir, "clientsettings.json");
            string? content = probe.ReadText(settings);
            if (content is not null && content.Contains("\"playeruid\"", StringComparison.Ordinal))
                return new DataPathDetection(dir, HasActiveSession: true);
        }

        foreach (string dir in candidates)
        {
            if (probe.DirectoryExists(dir))
                return new DataPathDetection(dir, HasActiveSession: false);
        }

        return new DataPathDetection(null, HasActiveSession: false);
    }

    private static string[] Candidates(ISystemProbe probe)
    {
        string home = probe.HomeDirectory;
        return probe.Os switch
        {
            OsKind.Windows =>
            [
                WinCombine(probe.GetEnvironmentVariable("APPDATA"), "VintagestoryData"),
                WinCombine(probe.GetEnvironmentVariable("APPDATA"), "OptimumData"),
            ],
            OsKind.MacOs =>
            [
                Posix(home, "Library", "Application Support", "VintagestoryData"),
                Posix(home, "Library", "Application Support", "OptimumVintagestoryData"),
                Posix(home, ".config", "VintagestoryData"),
            ],
            _ =>
            [
                Posix(home, ".config", "VintagestoryData"),
                Posix(home, ".config", "OptimumVintagestoryData"),
                Posix(home, "ApplicationData", "vintagestorydata"),
            ],
        };

        // The macOS and Linux data folders follow POSIX '/' convention regardless
        // of the host the installer binary happens to run on, so join with '/'
        // rather than System.IO.Path.Combine (which would emit '\' on Windows).
        static string Posix(params string[] parts) => string.Join('/', parts);

        static string WinCombine(string? root, string child) =>
            root is { Length: > 0 } ? System.IO.Path.Combine(root, child) : child;
    }
}

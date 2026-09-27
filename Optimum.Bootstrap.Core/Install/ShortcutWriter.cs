using Optimum.Bootstrap.Core.Platform;
using System.Security.Cryptography;
using System.Text;

namespace Optimum.Bootstrap.Core.Install;

/// <summary>
/// Writes and removes the application-menu and desktop shortcuts for an install.
/// Every operation is best effort: a shortcut that will not write is logged, not
/// fatal. Ports the shortcut handling from the Windows and Linux installers.
/// </summary>
public sealed class ShortcutWriter(ISystemProbe probe)
{
    /// <summary>Creates the requested shortcuts and returns the paths that were written.</summary>
    public IReadOnlyList<string> Create(string installDirectory, string launcherPath, ShortcutKinds kinds)
    {
        if (kinds == ShortcutKinds.None)
            return [];

        return probe.Os switch
        {
            OsKind.Windows => CreateWindows(installDirectory, launcherPath, kinds),
            _ => CreateLinux(installDirectory, launcherPath, kinds),
        };
    }

    public void Remove(IEnumerable<string> shortcutPaths)
    {
        foreach (string path in shortcutPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>Creates a per-install Linux menu entry for the guarded, confirming removal command.</summary>
    public string? CreateLinuxUninstallEntry(string installDirectory, string uninstallerPath)
    {
        if (probe.Os != OsKind.Linux) return null;
        string dataHome = probe.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Posix(probe.HomeDirectory, ".local", "share");
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installDirectory)))[..16]
            .ToLowerInvariant();
        string path = Posix(dataHome, "applications", $"optimum-uninstall-{id}.desktop");
        string entry = $"""
            [Desktop Entry]
            Type=Application
            Name=Uninstall Optimum
            Comment=Remove this separate Optimum installation
            Exec="{EscapeExec(uninstallerPath)}" uninstall-delta --install-dir "{EscapeExec(installDirectory)}" --confirm
            Terminal=true
            Icon=optimum
            Categories=Game;Settings;

            """;
        return WriteText(path, entry, executable: true).FirstOrDefault();
    }

    private List<string> CreateLinux(string installDirectory, string launcherPath, ShortcutKinds kinds)
    {
        var written = new List<string>();
        string home = probe.HomeDirectory;
        string dataHome = probe.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Posix(home, ".local", "share");

        string? icon = InstallIcon(installDirectory, Posix(dataHome, "icons", "hicolor", "256x256", "apps", "optimum.png"));
        if (icon is not null) written.Add(icon);
        string entry = DesktopEntry(launcherPath, installDirectory, icon);

        if (kinds.HasFlag(ShortcutKinds.Menu))
            written.AddRange(WriteText(Posix(dataHome, "applications", "optimum.desktop"), entry, executable: true));
        if (kinds.HasFlag(ShortcutKinds.Desktop))
            written.AddRange(WriteText(Posix(home, "Desktop", "Optimum.desktop"), entry, executable: true));

        return written;
    }

    /// <summary>
    /// Joins Linux paths with '/', the separator the target platform uses, rather
    /// than System.IO.Path.Combine (which emits '\' on a Windows build host and
    /// would produce mixed separators in the generated entries).
    /// </summary>
    private static string Posix(string root, params string[] parts) =>
        root.TrimEnd('/', '\\') + "/" + string.Join('/', parts);

    private List<string> CreateWindows(string installDirectory, string launcherPath, ShortcutKinds kinds)
    {
        var written = new List<string>();
        if (!OperatingSystem.IsWindows())
            return written;

        string? appData = probe.GetEnvironmentVariable("APPDATA");
        string? userProfile = probe.GetEnvironmentVariable("USERPROFILE") ?? probe.HomeDirectory;
        string exe = Path.Combine(installDirectory, "Optimum.exe");
        string linkTarget = File.Exists(exe) ? exe : launcherPath;

        if (kinds.HasFlag(ShortcutKinds.Menu) && appData is not null)
        {
            string dir = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Optimum");
            written.AddRange(WriteWindowsLink(Path.Combine(dir, "Optimum.lnk"), linkTarget, installDirectory));
        }
        if (kinds.HasFlag(ShortcutKinds.Desktop) && userProfile is not null)
            written.AddRange(WriteWindowsLink(Path.Combine(userProfile, "Desktop", "Optimum.lnk"), linkTarget, installDirectory));

        return written;
    }

    private static string DesktopEntry(string launcherPath, string workingDirectory, string? icon) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=Optimum
        Comment=High-performance client for Vintage Story
        Exec="{EscapeExec(launcherPath)}"
        Path={workingDirectory}
        Icon={icon ?? "optimum"}
        Terminal=false
        Categories=Game;
        StartupWMClass=Optimum

        """;

    /// <summary>
    /// Escapes a value for a quoted <c>Exec</c> per the Desktop Entry spec:
    /// backslash, double quote, backtick, and dollar are backslash-escaped.
    /// </summary>
    private static string EscapeExec(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            if (c == '%')
            {
                sb.Append("%%");
                continue;
            }
            if (c is '\\' or '"' or '`' or '$')
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    private string? InstallIcon(string installDirectory, string destination)
    {
        string[] sources =
        [
            Path.Combine(installDirectory, "assets", "gameicon.png"),
            Path.Combine(installDirectory, "logo.png"),
        ];
        foreach (string source in sources)
        {
            if (!File.Exists(source))
                continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                return destination;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        return null;
    }

    private static IEnumerable<string> WriteText(string path, string contents, bool executable)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            if (executable && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
            return [path];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> WriteWindowsLink(string linkPath, string target, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return [];
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = target;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = target + ",0";
            link.Save();
            return [linkPath];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }
}

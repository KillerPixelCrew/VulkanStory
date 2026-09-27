using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Optimum.Bootstrap.Core.Install;

/// <summary>
/// Registers and removes the Windows "Apps &amp; features" uninstall entry
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Optimum_is1</c>),
/// matching <c>scripts/install-windows.ps1</c>. A no-op on Linux, where
/// the install manifest and the <c>.desktop</c> entry are the record.
/// </summary>
public static class UninstallRegistration
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Optimum_is1";
    private const string DeltaKeyPrefix = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OptimumDelta_";

    /// <summary>Returns the registry key path when it was written, null otherwise.</summary>
    public static string? Register(string installDirectory, string version)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return RegisterWindows(installDirectory, version);
    }

    public static string? RegisterDelta(string installDirectory, string version, string uninstallerExecutable)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!Path.IsPathFullyQualified(installDirectory) || !File.Exists(uninstallerExecutable))
            return null;
        return RegisterDeltaWindows(installDirectory, version, uninstallerExecutable);
    }

    public static void Unregister(string? keyPath, string? installDirectory = null)
    {
        if (keyPath is null || !OperatingSystem.IsWindows())
            return;
        if (!string.Equals(keyPath, KeyPath, StringComparison.OrdinalIgnoreCase) &&
            (installDirectory is null || !Path.IsPathFullyQualified(installDirectory) ||
             !string.Equals(keyPath, DeltaKeyFor(installDirectory), StringComparison.OrdinalIgnoreCase)))
            return;
        UnregisterWindows(keyPath);
    }

    public static string DeltaKeyFor(string installDirectory)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)).ToUpperInvariant();
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return DeltaKeyPrefix + digest;
    }

    [SupportedOSPlatform("windows")]
    private static string? RegisterDeltaWindows(string installDirectory, string version, string uninstallerExecutable)
    {
        string keyPath = DeltaKeyFor(installDirectory);
        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue("DisplayName", "Optimum for Vintage Story");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "Zaldaryon");
            key.SetValue("InstallLocation", installDirectory);
            key.SetValue("DisplayIcon", Path.Combine(installDirectory, "Optimum.exe"));
            key.SetValue("UninstallString",
                $"\"{uninstallerExecutable}\" uninstall-delta --install-dir \"{installDirectory}\"");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            return keyPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? RegisterWindows(string installDirectory, string version)
    {
        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
            string exe = Path.Combine(installDirectory, "Optimum.exe");
            string uninstaller = Path.Combine(installDirectory, "uninstall.ps1");
            key.SetValue("DisplayName", "Optimum");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "Zaldaryon");
            key.SetValue("InstallLocation", installDirectory);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString",
                $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{uninstaller}\" -InstallDir \"{installDirectory}\" -Force");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            return KeyPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindows(string keyPath)
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            /* best effort */
        }
    }
}

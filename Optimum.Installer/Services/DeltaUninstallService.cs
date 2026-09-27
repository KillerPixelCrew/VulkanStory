using System.ComponentModel;
using System.Diagnostics;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Installer.Services;

public interface IDeltaUninstallService
{
    bool CanRemove(string directory);
    Task<UninstallResult> UninstallAsync(string directory, bool launchOriginal = false);
}

/// <summary>Removes only a manifest-bearing separate delta runtime.</summary>
public sealed class DeltaUninstallService : IDeltaUninstallService
{
    private readonly Func<string, bool> startOriginal;

    public DeltaUninstallService() : this(StartOriginal) { }

    internal DeltaUninstallService(Func<string, bool> startOriginal) => this.startOriginal = startOriginal;

    public bool CanRemove(string directory) => DeltaRuntimeGuard.IsSeparateRuntime(directory);

    public Task<UninstallResult> UninstallAsync(string directory, bool launchOriginal = false) => Task.Run(() =>
    {
        if (!DeltaRuntimeGuard.IsSeparateRuntime(directory))
            return UninstallResult.Failure(Optimum.Bootstrap.Core.FailureReason.BadInput,
                "This folder is not a separate Optimum delta installation.");
        string? originalLauncher = launchOriginal ? DeltaRuntimeGuard.FindOriginalLauncher(directory) : null;
        UninstallResult result = new Uninstaller(SystemProbe.Default).Uninstall(directory);
        if (!result.Ok || !launchOriginal) return result;
        return originalLauncher is not null && startOriginal(originalLauncher)
            ? result with { Message = "Optimum was removed and the original game was started." }
            : result with { Message = "Optimum was removed, but the original game could not be started." };
    });

    private static bool StartOriginal(string launcher)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(launcher)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(launcher)!,
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

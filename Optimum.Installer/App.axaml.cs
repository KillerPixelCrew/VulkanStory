using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Optimum.Installer.Services;
using Optimum.Installer.ViewModels;
using Optimum.Installer.Views;

namespace Optimum.Installer;

public partial class App : Application
{
    internal enum StartupRoute { Uninstall, LocalRelease, DownloadRelease, DeveloperBuild }

    internal static StartupRoute ChooseStartupRoute(string? baseDirectory, string[]? args)
    {
        if (args is { Length: > 0 } && args[0] == "--uninstall-runtime")
            return StartupRoute.Uninstall;
        if (baseDirectory is not null &&
            File.Exists(Path.Combine(baseDirectory, DeltaReleaseService.DescriptorName)))
            return StartupRoute.LocalRelease;
        if (args?.Contains("--developer-build", StringComparer.Ordinal) == true)
            return StartupRoute.DeveloperBuild;
        if (args?.Contains("--download-release", StringComparer.Ordinal) == true ||
            baseDirectory is not null &&
            File.Exists(Path.Combine(baseDirectory, "delta-decoder", "decoder.json")))
            return StartupRoute.DownloadRelease;
        return StartupRoute.DeveloperBuild;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Native Fluent theme; the OS supplies the accent colour and the
        // light/dark variant. No runtime colour setup is needed.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args;
            switch (ChooseStartupRoute(AppContext.BaseDirectory, args))
            {
            case StartupRoute.Uninstall:
                desktop.MainWindow = new DeltaUninstallWindow
                {
                    DataContext = new DeltaUninstallViewModel(new DeltaUninstallService(),
                        args is { Length: 2 } ? args[1] : ""),
                };
                break;
            case StartupRoute.LocalRelease:
                desktop.MainWindow = new DeltaInstallWindow
                {
                    DataContext = new DeltaInstallViewModel(new DeltaReleaseService(AppContext.BaseDirectory),
                        Optimum.Bootstrap.Core.Platform.SystemProbe.Default),
                };
                break;
            case StartupRoute.DownloadRelease:
                desktop.MainWindow = new DeltaInstallWindow
                {
                    DataContext = new DeltaInstallViewModel(
                        new RemoteDeltaReleaseService(DeltaReleaseAcquirer.CreateDefault()),
                        Optimum.Bootstrap.Core.Platform.SystemProbe.Default),
                };
                break;
            default:
                var shell = new MainWindowViewModel(InstallerServices.CreateReal());
                shell.ExitRequested += () => desktop.Shutdown();
                desktop.MainWindow = new MainWindow { DataContext = shell };
                break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

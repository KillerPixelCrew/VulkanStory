using Optimum.Installer.Services;
using Xunit;

namespace Optimum.Installer.Tests;

public sealed class AppStartupRouteTests
{
    [Fact]
    public void BundledInstallerUsesReleaseDownloadWhileDeveloperBuildKeepsWizard()
    {
        string directory = Directory.CreateTempSubdirectory("optimum-route-").FullName;
        try
        {
            Assert.Equal(App.StartupRoute.DeveloperBuild, App.ChooseStartupRoute(directory, []));
            Directory.CreateDirectory(Path.Combine(directory, "delta-decoder"));
            File.WriteAllText(Path.Combine(directory, "delta-decoder", "decoder.json"), "{}");
            Assert.Equal(App.StartupRoute.DownloadRelease, App.ChooseStartupRoute(directory, []));
            Assert.Equal(App.StartupRoute.DeveloperBuild,
                App.ChooseStartupRoute(directory, ["--developer-build"]));
            Assert.Equal(App.StartupRoute.DownloadRelease,
                App.ChooseStartupRoute(null, ["--download-release"]));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LocalReleaseAndUninstallTakePriority()
    {
        string directory = Directory.CreateTempSubdirectory("optimum-route-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, DeltaReleaseService.DescriptorName), "{}");
            Assert.Equal(App.StartupRoute.LocalRelease, App.ChooseStartupRoute(directory, []));
            Assert.Equal(App.StartupRoute.Uninstall,
                App.ChooseStartupRoute(directory, ["--uninstall-runtime", "some-runtime"]));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

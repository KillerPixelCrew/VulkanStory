// Source: Optimum.Bootstrap.Core.Tests/DataPathProbeTests.cs
namespace Optimum.Bootstrap.Core.Tests
{
using Optimum.Bootstrap.Core.DataPath;
using Xunit;

/// <summary>Ports the <c>prompt_data_path</c> heuristic from <c>scripts/install-linux.sh</c>.</summary>
public class DataPathProbeTests
{
    [Fact]
    public void PrefersACandidateWithAnActiveSessionOverOneThatMerelyExists()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/home/tester/.config/VintagestoryData");
        probe.AddDirectory("/home/tester/.config/OptimumVintagestoryData");
        probe.AddFile("/home/tester/.config/OptimumVintagestoryData/clientsettings.json",
            """{ "playeruid": "abc123" }""");

        DataPathDetection detection = DataPathProbe.Detect(probe);

        Assert.Equal("/home/tester/.config/OptimumVintagestoryData", detection.Path);
        Assert.True(detection.HasActiveSession);
    }

    [Fact]
    public void FallsBackToTheFirstDirectoryThatExists()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/home/tester/.config/VintagestoryData");

        DataPathDetection detection = DataPathProbe.Detect(probe);

        Assert.Equal("/home/tester/.config/VintagestoryData", detection.Path);
        Assert.False(detection.HasActiveSession);
    }

    [Fact]
    public void ReturnsNothingWhenNoCandidateExists()
    {
        DataPathDetection detection = DataPathProbe.Detect(new FakeSystemProbe());
        Assert.Null(detection.Path);
    }
}
}

// Source: Optimum.Bootstrap.Core.Tests/InstallPathGuardTests.cs
namespace Optimum.Bootstrap.Core.Tests
{
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Paths;
using Xunit;

/// <summary>
/// Every case INSTALLER-PLAN.md section 9 lists for the path guard, plus the
/// overlap and data-path rules from <c>Assert-SafeInstallerPaths</c>.
/// </summary>
public class InstallPathGuardTests
{
    private static FakeSystemProbe Linux()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Linux, HomeDirectory = "/home/tester" };
        return probe;
    }

    private static void AssertRejected(InstallPathVerdict verdict, string fragment)
    {
        Assert.False(verdict.Ok);
        Assert.Contains(fragment, verdict.Rejection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsTheFilesystemRoot() =>
        AssertRejected(InstallPathGuard.Check(Linux(), new InstallPathRequest("/")), "root");

    [Fact]
    public void RejectsTheHomeDirectory() =>
        AssertRejected(InstallPathGuard.Check(Linux(), new InstallPathRequest("/home/tester")), "home");

    [Fact]
    public void RejectsTheXdgDataHome()
    {
        FakeSystemProbe probe = Linux();
        probe.Environment["XDG_DATA_HOME"] = "/home/tester/.local/share";
        AssertRejected(InstallPathGuard.Check(probe, new InstallPathRequest("/home/tester/.local/share")), ".local/share");
    }

    [Fact]
    public void RejectsDotLocal() =>
        AssertRejected(InstallPathGuard.Check(Linux(), new InstallPathRequest("/home/tester/.local")), ".local");

    [Fact]
    public void RejectsAWindowsDriveRoot()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows, HomeDirectory = @"C:\Users\tester" };
        AssertRejected(InstallPathGuard.Check(probe, new InstallPathRequest(@"C:\")), "root");
    }

    [Fact]
    public void RejectsAPathInsideAVintageStoryInstall() =>
        AssertRejected(
            InstallPathGuard.Check(Linux(), new InstallPathRequest("/home/tester/.local/share/vintagestory/mods")),
            "Vintage Story");

    [Fact]
    public void RejectsADirectoryHoldingAVanillaGameWithNoOptimumMarker()
    {
        FakeSystemProbe probe = Linux();
        probe.AddFile("/opt/games/vs/Vintagestory");
        AssertRejected(InstallPathGuard.Check(probe, new InstallPathRequest("/opt/games/vs")), "vanilla Vintage Story");
    }

    [Fact]
    public void RejectsAnInstallDirectoryThatIsItselfASymlink()
    {
        FakeSystemProbe probe = Linux();
        probe.AddSymlink("/home/tester/games/optimum");
        AssertRejected(InstallPathGuard.Check(probe, new InstallPathRequest("/home/tester/games/optimum")), "symbolic link");
    }

    [Fact]
    public void AllowsAnInstallDirectoryUnderASymlinkedParent()
    {
        FakeSystemProbe probe = Linux();
        probe.AddSymlink("/home/tester/Games");   // a second drive mounted here
        Assert.True(InstallPathGuard.Check(probe, new InstallPathRequest("/home/tester/Games/optimum")).Ok);
    }

    [Fact]
    public void AllowsACleanSeparateDirectory()
    {
        InstallPathVerdict verdict = InstallPathGuard.Check(Linux(),
            new InstallPathRequest("/home/tester/games/optimum"));
        Assert.True(verdict.Ok);
        Assert.Null(verdict.Rejection);
    }

    [Fact]
    public void AllowsADirectoryHoldingAnExistingOptimumInstall()
    {
        FakeSystemProbe probe = Linux();
        probe.AddFile("/home/tester/games/optimum/Vintagestory");
        probe.AddFile("/home/tester/games/optimum/Optimum");
        Assert.True(InstallPathGuard.Check(probe, new InstallPathRequest("/home/tester/games/optimum")).Ok);
    }

    [Fact]
    public void RejectsAnInstallDirectoryThatOverlapsTheVintageStoryDirectory() =>
        AssertRejected(
            InstallPathGuard.Check(Linux(), new InstallPathRequest(
                "/home/tester/opt", VintageStoryDirectory: "/home/tester/opt/vs")),
            "overlap");

    [Fact]
    public void RejectsADataPathInsideTheInstallDirectory() =>
        AssertRejected(
            InstallPathGuard.Check(Linux(), new InstallPathRequest(
                "/home/tester/opt", DataPath: "/home/tester/opt/data")),
            "data path");
}
}

// Source: Optimum.Bootstrap.Core.Tests/SymlinkComponentCheckTests.cs
namespace Optimum.Bootstrap.Core.Tests
{
using Optimum.Bootstrap.Core.Paths;
using Xunit;

public class SymlinkComponentCheckTests
{
    [Fact]
    public void CleanPathHasNoSymlinkComponent()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/home/tester/games");
        Assert.Null(SymlinkComponentCheck.FirstSymlinkComponent(probe, "/home/tester/games/optimum"));
    }

    [Fact]
    public void ReturnsTheSymlinkedComponentWhenOneIsInThePath()
    {
        var probe = new FakeSystemProbe();
        probe.AddSymlink("/home/tester/games");

        Assert.Equal("/home/tester/games",
            SymlinkComponentCheck.FirstSymlinkComponent(probe, "/home/tester/games/optimum/bin"));
    }

    [Fact]
    public void RequireExistsThrowsWhenAComponentIsMissing()
    {
        var probe = new FakeSystemProbe();
        Assert.Throws<DirectoryNotFoundException>(() =>
            SymlinkComponentCheck.FirstSymlinkComponent(probe, "/nowhere/at/all", requireExists: true));
    }
}
}

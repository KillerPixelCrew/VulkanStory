// Source: Optimum.Bootstrap.Core.Tests/DotnetSdkProbeTests.cs
namespace Optimum.Bootstrap.Core.Tests
{
using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

/// <summary>
/// Ports the <c>check_dotnet10</c> selection from
/// <c>scripts/tests/install-linux-prerequisites.sh</c>: given a system dotnet on
/// SDK 9 and a user dotnet on SDK 10, detection picks the user one.
/// </summary>
public class DotnetSdkProbeTests
{
    [Fact]
    public void PicksTheCandidateThatReportsANet10Sdk()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/t/bin/dotnet:/home/tester/.dotnet/dotnet";
        probe.AddFile("/t/bin/dotnet");
        probe.AddFile("/home/tester/.dotnet/dotnet");
        probe.OnCommand("/t/bin/dotnet", "--list-sdks", "9.0.100 [/system/sdk]\n");
        probe.OnCommand("/home/tester/.dotnet/dotnet", "--list-sdks", "10.0.100 [/user/sdk]\n");

        Assert.Equal("/home/tester/.dotnet/dotnet", DotnetSdkProbe.Find(probe));
    }

    [Fact]
    public void ReturnsNullWhenNoCandidateReportsNet10()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/t/bin/dotnet";
        probe.AddFile("/t/bin/dotnet");
        probe.OnCommand("/t/bin/dotnet", "--list-sdks", "9.0.100 [/system/sdk]\n");

        Assert.Null(DotnetSdkProbe.Find(probe));
    }

    [Fact]
    public void PrefersDotnetOnPathBeforeTheCandidateList()
    {
        var probe = new FakeSystemProbe();
        probe.Path.Add("/usr/bin");
        probe.AddFile("/usr/bin/dotnet");
        probe.OnCommand("/usr/bin/dotnet", "--list-sdks", "10.0.203 [/usr/lib/dotnet/sdk]\n");

        Assert.Equal("/usr/bin/dotnet", DotnetSdkProbe.Find(probe));
    }

    [Fact]
    public void SkipsANonExecutableFileEarlierOnPathAndKeepsSearching()
    {
        var probe = new FakeSystemProbe();
        probe.Path.Add("/broken");
        probe.Path.Add("/usr/bin");
        probe.AddNonExecutableFile("/broken/dotnet");
        probe.AddFile("/usr/bin/dotnet");
        probe.OnCommand("/usr/bin/dotnet", "--list-sdks", "10.0.100 [/usr/lib/dotnet/sdk]\n");

        Assert.Equal("/usr/bin/dotnet", DotnetSdkProbe.Find(probe));
    }
}
}

// Source: Optimum.Bootstrap.Core.Tests/IlspycmdVersionTests.cs
namespace Optimum.Bootstrap.Core.Tests
{
using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

/// <summary>
/// Ports the ilspycmd version cases from
/// <c>scripts/tests/install-linux-prerequisites.sh</c>. These are the exact
/// accept and reject values that script pins.
/// </summary>
public class IlspycmdVersionTests
{
    private static readonly IlspycmdCompatibility Range = IlspycmdCompatibility.Fallback;

    [Theory]
    [InlineData("11.0.0.9375")]
    public void AcceptsVersionsInsideTheRange(string version)
    {
        Assert.True(Range.Supports(version));
    }

    [Theory]
    [InlineData("11.0.0.9374")]
    [InlineData("11.0.0.9376")]
    [InlineData("10.1.1.8388")]
    [InlineData("10.1.0.8386")]
    [InlineData("11.0.1.0")]
    [InlineData("11.1.0.0")]
    [InlineData("12.0.0.0")]
    [InlineData("10.2.0.1")]
    [InlineData("11.0.0.9375-rc1")]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("11.0.0")]
    public void RejectsEverythingElse(string version)
    {
        Assert.False(Range.Supports(version));
    }

    [Fact]
    public void ReadsTheRangeAndPinFromConfigFiles()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/repo/.config/ilspycmd-compat.json",
            """{ "minimumVersion": "11.0.0.9375", "maximumVersion": "11.0.0.9375" }""");
        probe.AddFile("/repo/.config/dotnet-tools.json",
            """{ "version": 1, "tools": { "ilspycmd": { "version": "11.0.0.9375" } } }""");

        IlspycmdCompatibility compat = ConfigFiles.ReadIlspycmdCompatibility(probe, "/repo");

        Assert.Equal("11.0.0.9375", compat.Pin);
        Assert.Equal(new IlspycmdVersion(11, 0, 0, 9375), compat.Minimum);
        Assert.Equal(new IlspycmdVersion(11, 0, 0, 9375), compat.Maximum);
    }

    [Fact]
    public void FallsBackWhenConfigFilesAreAbsent()
    {
        IlspycmdCompatibility compat = ConfigFiles.ReadIlspycmdCompatibility(new FakeSystemProbe(), "/repo");
        Assert.Equal(IlspycmdCompatibility.Fallback, compat);
    }
}
}

// Source: Optimum.Bootstrap.Core.Tests/NixEnvironmentTests.cs
namespace Optimum.Bootstrap.Core.Tests
{
using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

/// <summary>Ports <c>scripts/tests/install-linux-nixos.sh</c>.</summary>
public class NixEnvironmentTests
{
    [Fact]
    public void DownloadedSdkRunsOnADefaultGlibcHost()
    {
        var probe = new FakeSystemProbe { Arch = Architecture.X64 };
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");

        Assert.True(NixEnvironment.DownloadedSdkRunnable(probe));
    }

    [Theory]
    [InlineData(OsKind.Windows)]
    [InlineData(OsKind.MacOs)]
    public void TheNonFhsCheckIsLinuxOnly(OsKind os)
    {
        var probe = new FakeSystemProbe { Os = os };
        // A leftover NIX_STORE / interpreter override must not make a native
        // Windows or macOS host look non-FHS.
        probe.Environment["NIX_STORE"] = "/nix/store";
        probe.Environment["OPTIMUM_GLIBC_INTERPRETER"] = "/tmp/missing-ld-linux";

        Assert.False(NixEnvironment.IsNixOs(probe));
        Assert.Equal(string.Empty, NixEnvironment.GlibcInterpreterPath(probe));
        Assert.True(NixEnvironment.DownloadedSdkRunnable(probe));
    }

    [Fact]
    public void SdkAcquisitionBuildsAWindowsPlan()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows, HomeDirectory = @"C:\Users\tester" };

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, @"C:\repo");

        Assert.True(decision.CanRunScript);
        Assert.NotNull(decision.Plan);
        Assert.EndsWith("dotnet-install.ps1", decision.Plan!.ScriptUrl);
        Assert.Contains("-NoPath", decision.Plan.Arguments);
    }

    [Fact]
    public void DownloadedSdkDoesNotRunWhenTheInterpreterIsMissing()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_GLIBC_INTERPRETER"] = "/tmp/missing-ld-linux";

        Assert.Equal("/tmp/missing-ld-linux", NixEnvironment.GlibcInterpreterPath(probe));
        Assert.False(NixEnvironment.DownloadedSdkRunnable(probe));
    }

    [Fact]
    public void DetectNixOsFollowsNixStoreAndTheMarkerFile()
    {
        var probe = new FakeSystemProbe();
        Assert.False(NixEnvironment.IsNixOs(probe));

        probe.Environment["NIX_STORE"] = "/nix/store";
        Assert.True(NixEnvironment.IsNixOs(probe));

        probe.Environment.Remove("NIX_STORE");
        probe.AddFile("/etc/NIXOS");
        Assert.True(NixEnvironment.IsNixOs(probe));
    }

    [Fact]
    public void NixInstallCommandNamesNixpkgsAndTheSdk()
    {
        Assert.Contains("nixpkgs", NixEnvironment.DotnetSdkInstallCommand);
        Assert.Contains("dotnet-sdk_10", NixEnvironment.DotnetSdkInstallCommand);
    }

    [Fact]
    public void SdkAcquisitionRefusesOnNixOs()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["NIX_STORE"] = "/nix/store";

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, "/repo");

        Assert.False(decision.CanRunScript);
        Assert.Null(decision.Plan);
        Assert.Contains("NixOS", decision.RefusalReason);
    }

    [Fact]
    public void SdkAcquisitionRefusesOnANonFhsHost()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_GLIBC_INTERPRETER"] = "/tmp/missing-ld-linux";

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, "/repo");

        Assert.False(decision.CanRunScript);
        Assert.Contains("non-FHS", decision.RefusalReason);
    }

    [Fact]
    public void PrerequisiteScannerRoutesTheSdkRowThroughNixpkgsOnNixOs()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["NIX_STORE"] = "/nix/store";
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        PrerequisiteResult dotnet = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Dotnet);

        Assert.Equal(PrerequisiteState.Missing, dotnet.State);
        Assert.Contains("nixpkgs", dotnet.Label);
        Assert.Equal(NixEnvironment.DotnetSdkInstallCommand, dotnet.AcquisitionCommand);
    }

    [Fact]
    public void PrerequisiteScannerFlagsANonFhsHostWithNoInstallCommand()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_GLIBC_INTERPRETER"] = "/tmp/missing-ld-linux";
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        PrerequisiteResult dotnet = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Dotnet);

        Assert.Equal(PrerequisiteState.Missing, dotnet.State);
        Assert.Contains("non-FHS", dotnet.Label);
        Assert.Null(dotnet.AcquisitionCommand);
    }
}
}

// Source: Optimum.Bootstrap.Core.Tests/PrerequisiteScannerTests.cs
namespace Optimum.Bootstrap.Core.Tests
{
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

public class PrerequisiteScannerTests
{
    private static FakeSystemProbe LinuxWithCoreTools()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Linux };
        probe.Path.Add("/usr/bin");
        foreach (string tool in new[] { "git", "perl", "python3", "curl", "tar", "chmod", "pwsh", "apt-get" })
            probe.AddFile($"/usr/bin/{tool}");
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");
        return probe;
    }

    [Fact]
    public void OnlyTheSdkBlocksTheBuildWhenTheDecompilerIsAlsoMissing()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        IReadOnlyList<PrerequisiteResult> results = new PrerequisiteScanner(probe, "/repo").Scan();

        PrerequisiteId[] blocking = results.Where(r => r.BlocksBuild).Select(r => r.Definition.Id).ToArray();
        Assert.Equal([PrerequisiteId.Dotnet], blocking);

        PrerequisiteResult ilspy = results.Single(r => r.Definition.Id == PrerequisiteId.Ilspycmd);
        Assert.Equal(PrerequisiteState.OptionalMissing, ilspy.State);
        Assert.Equal(AcquisitionKind.Automatic, ilspy.Acquisition);
    }

    [Fact]
    public void PowerShellMissingDoesNotBlockTheBuild()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Files.Remove("/usr/bin/pwsh");
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        PrerequisiteResult pwsh = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Pwsh);

        Assert.Equal(RequirementLevel.RequiredForPackaging, pwsh.Definition.Level);
        Assert.Equal(PrerequisiteState.Missing, pwsh.State);
        Assert.False(pwsh.BlocksBuild);
    }

    [Fact]
    public void AppimagetoolInThePrivateToolDirectoryMustBeExecutable()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";
        probe.AddNonExecutableFile("/repo/.tools/appimagetool");

        PrerequisiteResult appimagetool = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Appimagetool);

        Assert.Equal(PrerequisiteState.OptionalMissing, appimagetool.State);
        Assert.Equal(AcquisitionKind.Automatic, appimagetool.Acquisition);
    }

    [Fact]
    public void AllRequiredPresentWhenTheSdkAndAnInRangeDecompilerAreThere()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/home/tester/.dotnet/dotnet";
        probe.AddFile("/home/tester/.dotnet/dotnet");
        probe.OnCommand("/home/tester/.dotnet/dotnet", "--list-sdks", "10.0.100 [/user/sdk]\n");
        probe.OnCommand("/home/tester/.dotnet/dotnet", "--version", "10.0.100\n");
        probe.AddFile("/home/tester/.dotnet/tools/ilspycmd");
        probe.OnCommand("/home/tester/.dotnet/tools/ilspycmd", "--version", "ilspycmd: 11.0.0.9375\n");

        var scanner = new PrerequisiteScanner(probe, "/repo");
        Assert.True(scanner.AllRequiredPresent());

        PrerequisiteResult ilspy = scanner.Scan().Single(r => r.Definition.Id == PrerequisiteId.Ilspycmd);
        Assert.Equal(PrerequisiteState.Ok, ilspy.State);
        Assert.Equal("11.0.0.9375", ilspy.DetectedVersion);
    }

    [Fact]
    public void AnOutOfRangeDecompilerIsReportedOutdatedWithTheUpdateCommand()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";
        probe.AddFile("/home/tester/.dotnet/tools/ilspycmd");
        probe.OnCommand("/home/tester/.dotnet/tools/ilspycmd", "--version", "ilspycmd: 10.2.0.1\n");

        PrerequisiteResult ilspy = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Ilspycmd);

        Assert.Equal(PrerequisiteState.Outdated, ilspy.State);
        Assert.Equal(
            "dotnet tool update -g ilspycmd --version 11.0.0.9375 --allow-downgrade",
            ilspy.AcquisitionCommand);
    }

    [Fact]
    public void InnoextractBelowElevenIsOutdated()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";
        probe.AddFile("/usr/bin/innoextract");
        probe.OnCommand("/usr/bin/innoextract", "--version", "innoextract 1.9\n");

        PrerequisiteResult inno = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Innoextract);

        Assert.Equal(PrerequisiteState.Outdated, inno.State);
    }

    [Theory]
    [InlineData("innoextract 1.11\n", 1, 11)]
    [InlineData("innoextract 1.9-gcc\n", 1, 9)]
    [InlineData("innoextract 2.0.1\n", 2, 0)]
    public void InnoextractVersionParse(string output, int major, int minor)
    {
        Assert.Equal((major, minor), PrerequisiteScanner.ParseInnoextractVersion(output));
    }

    // Forward-slash paths: FakeSystemProbe matches literal strings and the code
    // under test joins with Path.Combine, which uses the host separator when the
    // tests run on Linux. .NET on Windows accepts forward slashes anyway.
    private static FakeSystemProbe WindowsHost()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows, HomeDirectory = "C:/Users/tester" };
        probe.Path.Add("C:/Windows/System32/WindowsPowerShell/v1.0");
        probe.AddFile("C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe");
        return probe;
    }

    private static void AddWindowsDotnet(FakeSystemProbe probe)
    {
        probe.Path.Add("C:/Program Files/dotnet");
        probe.AddFile("C:/Program Files/dotnet/dotnet.exe");
        probe.OnCommand("C:/Program Files/dotnet/dotnet.exe", "--list-sdks", "10.0.100 [C:\\sdk]\n");
        probe.OnCommand("C:/Program Files/dotnet/dotnet.exe", "--version", "10.0.100\n");
    }

    [Fact]
    public void WindowsDoesNotDemandUnixToolsAndClearsWithJustDotnetAndGit()
    {
        FakeSystemProbe probe = WindowsHost();
        probe.Path.Add("C:/Program Files/Git/cmd");
        probe.AddFile("C:/Program Files/Git/cmd/git.exe");
        AddWindowsDotnet(probe);

        IReadOnlyList<PrerequisiteResult> results = new PrerequisiteScanner(probe, @"C:\repo").Scan();

        Assert.DoesNotContain(results, r => r.Definition.Id is PrerequisiteId.Perl
            or PrerequisiteId.Python3 or PrerequisiteId.Chmod or PrerequisiteId.Tar
            or PrerequisiteId.Curl or PrerequisiteId.Appimagetool);
        Assert.DoesNotContain(results, r => r.BlocksBuild);
        Assert.Equal(PrerequisiteState.Ok, results.Single(r => r.Definition.Id == PrerequisiteId.Pwsh).State);
    }

    [Fact]
    public void WindowsWithoutTheSdkOffersAnAutomaticInstallNotANonFhsRefusal()
    {
        FakeSystemProbe probe = WindowsHost();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "C:/absent/dotnet.exe";

        PrerequisiteResult dotnet = new PrerequisiteScanner(probe, "C:/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Dotnet);

        Assert.Equal(PrerequisiteState.Missing, dotnet.State);
        Assert.Equal(AcquisitionKind.Automatic, dotnet.Acquisition);
        Assert.DoesNotContain("non-FHS", dotnet.Label);
    }

    [Fact]
    public void WindowsWithoutGitPointsAtGitForWindows()
    {
        FakeSystemProbe probe = WindowsHost();
        AddWindowsDotnet(probe);

        PrerequisiteResult git = new PrerequisiteScanner(probe, "C:/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Git);

        Assert.Equal(PrerequisiteState.Missing, git.State);
        Assert.Equal(AcquisitionKind.DownloadPage, git.Acquisition);
        Assert.Equal(PrerequisiteScanner.GitForWindowsUrl, git.DownloadUrl);
        Assert.True(git.BlocksBuild);
    }
}
}

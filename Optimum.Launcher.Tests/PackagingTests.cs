// Source: Optimum.Launcher.Tests/solution-integrity-tests.cs
namespace Optimum.Launcher.Tests
{
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

public class SolutionIntegrityTests
{
    [Fact]
    public void EverySolutionProjectPathExists()
    {
        var root = FindRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(root.FullName, "VintageStory.slnx"));
        var projectPaths = solution
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        Assert.NotEmpty(projectPaths);
        foreach (var projectPath in projectPaths)
        {
            var fullPath = Path.GetFullPath(projectPath, root.FullName);
            Assert.True(File.Exists(fullPath), $"Solution project not found: {projectPath}");
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageStory.slnx")))
                return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root with VintageStory.slnx not found.");
    }
}
}

// Source: Optimum.Launcher.Tests/splash-screen-packaging-tests.cs
namespace Optimum.Launcher.Tests
{
using System;
using System.IO;
using Xunit;

/// <summary>
/// The patch-progress splash screen (PatchSplashScreen.cs) pulls in OpenTK
/// (GLFW) and SkiaSharp, both of which ship native binaries under
/// runtimes/&lt;rid&gt;/native/ - confirmed present after a real build
/// (runtimes/win-x64/native/{glfw3.dll,libSkiaSharp.dll}). package.ps1 stages
/// the Windows release by copying an explicit, fixed file list for the
/// launcher; without copying that runtimes/ folder too, the staged release
/// builds and runs fine in dev but throws DllNotFoundException the moment a
/// real user's cache-miss patch run tries to open the splash. Guards against
/// that fix regressing silently.
/// </summary>
public class SplashScreenPackagingTests
{
    [Fact]
    public void LauncherCsprojReferencesRequiredNativeAssetPackages()
    {
        string csproj = Read("Optimum.Launcher/Optimum.Launcher.csproj");
        Assert.Contains("PackageReference Include=\"OpenTK\"", csproj);
        Assert.Contains("PackageReference Include=\"SkiaSharp\"", csproj);
        Assert.Contains("PackageReference Include=\"SkiaSharp.NativeAssets.Win32\"", csproj);
        Assert.Contains("PackageReference Include=\"SkiaSharp.NativeAssets.Linux\"", csproj);
        Assert.Contains("PackageReference Include=\"SkiaSharp.NativeAssets.macOS\"", csproj);
    }

    [Fact]
    public void WindowsPackagingScriptCopiesLauncherRuntimesFolder()
    {
        string script = Read("scripts/package.ps1");
        Assert.Contains("Join-Path $launcherOut 'runtimes'", script);
    }

    private static string Read(string relativePath)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root.FullName, relativePath));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageStory.slnx")))
                return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root with VintageStory.slnx not found.");
    }
}
}

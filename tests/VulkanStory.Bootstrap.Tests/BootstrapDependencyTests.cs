using System.Reflection;
using System.Text.Json;
using VulkanStory.Bootstrap;
using Xunit;

namespace VulkanStory.Bootstrap.Tests;

/// <summary>Validates early dependency path/identity selection using copied framework assemblies in an isolated temporary directory.</summary>
/// <remarks>Exercises resolver metadata and filesystem behavior; does not load the game runtime or graphics.</remarks>
public sealed class BootstrapDependencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "vulkanstory-dependencies-" + Guid.NewGuid().ToString("N"));

    public BootstrapDependencyTests() => Directory.CreateDirectory(Path.Combine(root, "Lib"));

    [Fact]
    public void FindsAnIdentityMatchingLibraryBeforeTheGameRegistersItsResolver()
    {
        string library = Path.Combine(root, "Lib", "System.Text.Json.dll");
        File.Copy(typeof(JsonSerializer).Assembly.Location, library);
        var dependencies = new BootstrapDependencies(Path.Combine(root, "managed"), root);

        Assert.Equal(library, dependencies.Locate(typeof(JsonSerializer).Assembly.GetName()));
        Assert.Null(dependencies.Locate(new AssemblyName("Missing.Game.Dependency")));
    }

    [Fact]
    public void RefusesAFileWhoseContentsHaveTheWrongAssemblyIdentity()
    {
        File.Copy(typeof(JsonSerializer).Assembly.Location,
            Path.Combine(root, "Lib", "OpenTK.Mathematics.dll"));
        var dependencies = new BootstrapDependencies(Path.Combine(root, "managed"), root);

        Assert.Throws<FileLoadException>(() => dependencies.Locate(
            new AssemblyName("OpenTK.Mathematics, Version=4.9.4.0")));
    }

    [Fact]
    public void RefusesARequestedVersionNewerThanTheShippedLibrary()
    {
        File.Copy(typeof(JsonSerializer).Assembly.Location,
            Path.Combine(root, "Lib", "System.Text.Json.dll"));
        var dependencies = new BootstrapDependencies(Path.Combine(root, "managed"), root);

        Assert.Throws<FileLoadException>(() => dependencies.Locate(
            new AssemblyName("System.Text.Json, Version=999.0.0.0")));
    }

    public void Dispose()
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(root);
        if (!target.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup path escaped the temporary directory.");
        Directory.Delete(target, recursive: true);
    }
}

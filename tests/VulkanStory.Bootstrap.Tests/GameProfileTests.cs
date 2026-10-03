using System.Security.Cryptography;
using VulkanStory.Bootstrap;
using Xunit;

namespace VulkanStory.Bootstrap.Tests;

public sealed class GameProfileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vulkanstory-profile-" + Guid.NewGuid().ToString("N"));

    public GameProfileTests() => Directory.CreateDirectory(root);

    [Fact]
    public void RejectsAChangedAssemblyBeforeAnyRuntimeLoad()
    {
        string assembly = Path.Combine(root, "game.dll");
        File.WriteAllText(assembly, "accepted reference bytes");
        var profile = new GameProfile(1, "test", "1.22.7", 10,
            new() { ["game.dll"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))) });
        profile.VerifyFiles(root);
        File.WriteAllText(assembly, "patched or updated bytes");
        Assert.Throws<InvalidDataException>(() => profile.VerifyFiles(root));
    }

    [Fact]
    public void RejectsProfileTraversal()
    {
        var profile = new GameProfile(1, "test", "1.22.7", 10, new() { ["../outside.dll"] = new string('0', 64) });
        Assert.Throws<InvalidDataException>(() => profile.VerifyFiles(root));
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

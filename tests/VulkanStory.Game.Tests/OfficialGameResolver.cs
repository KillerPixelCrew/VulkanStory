using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VulkanStory.Game.Tests;

/// <summary>Test-assembly initializer that verifies pinned official reference hashes before resolving game/Lib dependencies.</summary>
/// <remarks>The assembly disables parallel tests because Harmony patches and game statics are shared process state.</remarks>
internal static class OfficialGameResolver
{
    /// <summary>Reads the configured official directory/profile and attaches its managed dependency resolver.</summary>
    /// <remarks>Fails module initialization for missing configuration, unreadable files, or mismatched hashes.</remarks>
    [ModuleInitializer]
    internal static void Initialize()
    {
        string directory = typeof(OfficialGameResolver).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "OfficialGameDirectory").Value ??
            throw new InvalidDataException("Set VintageStoryPath to the official installation.");
        using JsonDocument profile = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "official-profile.json")));
        foreach (JsonProperty file in profile.RootElement.GetProperty("files").EnumerateObject())
        {
            using FileStream stream = File.OpenRead(Path.Combine(directory, file.Name));
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Value.GetString(),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Official test reference hash mismatch: " + file.Name);
        }
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string root in new[] { directory, Path.Combine(directory, "Lib") })
            {
                string path = Path.Combine(root, name.Name + ".dll");
                if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            return null;
        };
    }
}

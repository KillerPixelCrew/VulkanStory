using System.Security.Cryptography;
using System.Text.Json;

namespace VulkanStory.Bootstrap;

internal sealed record GameProfile(int Schema, string Id, string GameVersion, int RuntimeMajor,
    Dictionary<string, string> Files)
{
    internal static GameProfile Load(string path)
    {
        using FileStream file = File.OpenRead(path);
        if (file.Length > 64 * 1024) throw new InvalidDataException("Game profile exceeds the size limit.");
        GameProfile profile = JsonSerializer.Deserialize<GameProfile>(file,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing game profile.");
        if (profile.Schema != 1 || string.IsNullOrWhiteSpace(profile.Id) || profile.Files is null || profile.Files.Count == 0)
            throw new InvalidDataException("Unsupported or empty game profile.");
        return profile;
    }

    internal void VerifyFiles(string gameDirectory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)) + Path.DirectorySeparatorChar;
        foreach ((string relative, string expected) in Files)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (Path.IsPathRooted(relative) || !path.StartsWith(root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"Profile path is outside the game directory: {relative}");
            using FileStream file = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(file));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Game profile {Id} does not match {relative}. No startup patches were applied.");
        }
    }
}

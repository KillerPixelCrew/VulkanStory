using System.Text.Json;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Reads the original game disabled-mod policy at window-service selection, after game settings and paths have initialized.</summary>
internal static class RuntimeModDisablement
{
    // Use the same metadata as the shipped ordinary mod, rather than assuming
    // assembly-version formatting matches the mod manager's id@version keys.
    private static readonly (string Id, string VersionKey) Identity = ReadIdentity();

    private static (string, string) ReadIdentity()
    {
        using Stream source = typeof(RuntimeModDisablement).Assembly.GetManifestResourceStream("VulkanStory.Game.ModInfo.json")
            ?? throw new InvalidDataException("VulkanStory client mod metadata is missing.");
        using JsonDocument document = JsonDocument.Parse(source);
        string id = document.RootElement.GetProperty("modid").GetString()
            ?? throw new InvalidDataException("VulkanStory client mod ID is missing.");
        string version = document.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidDataException("VulkanStory client mod version is missing.");
        return (id, id + "@" + version);
    }

    internal static bool IsDisabled()
    {
        // Called at the first window request, after normal argument/data-path
        // selection and client settings loading. Match the original ModLoader.
        var disabled = ClientSettings.DisabledMods;
        return disabled != null && (disabled.Contains(Identity.Id) || disabled.Contains(Identity.VersionKey));
    }
}

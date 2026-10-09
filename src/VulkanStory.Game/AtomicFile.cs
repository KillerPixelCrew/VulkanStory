namespace VulkanStory.Game;

/// <summary>Replaces small VulkanStory-owned files through a sibling temporary file.</summary>
internal static class AtomicFile
{
    /// <summary>Writes text to <c>path + ".tmp"</c> and moves it over the destination.</summary>
    /// <param name="path">Destination pathname; its parent directory is created when needed.</param>
    /// <param name="contents">Complete file text.</param>
    /// <remarks>Directory, write and move failures propagate to the caller; nothing is swallowed.</remarks>
    internal static void WriteAllText(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}

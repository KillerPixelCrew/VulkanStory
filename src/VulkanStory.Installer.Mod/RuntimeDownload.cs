using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VulkanStory.Installer.Mod;

internal static class RuntimeDownload
{
    public static async Task<string> InstallAsync(string game, Action<string> progress, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new InvalidOperationException("This release requires Windows x64.");
        if (File.Exists(Path.Combine(game, "VulkanStory", "package.json")))
            throw new InvalidOperationException("Vulkan Story is already installed. Close the game before updating it.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VulkanStoryInstaller/0.1.4");
        using var release = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/KillerPixelCrew/VulkanStory/releases/latest", token));
        var root = release.RootElement;
        var asset = root.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "VulkanStory-win-x64.zip");
        string digest = asset.GetProperty("digest").GetString() ?? "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
            throw new InvalidDataException("The release has no SHA256 download checksum.");
        string work = Path.Combine(Path.GetTempPath(), "VulkanStory-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string zip = Path.Combine(work, "runtime.zip");
            using (var response = await http.GetAsync(asset.GetProperty("browser_download_url").GetString(), HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(zip);
                var buffer = new byte[131072];
                long downloaded = 0, total = asset.GetProperty("size").GetInt64();
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    downloaded += count;
                    if (downloaded > total) throw new InvalidDataException("Download exceeds the published size.");
                    progress($"Downloading Vulkan Story… {downloaded / 1048576} / {total / 1048576} MB");
                }
                if (downloaded != total) throw new InvalidDataException("The download is incomplete.");
            }
            progress("Verifying and extracting Vulkan Story…");
            await using (var file = File.OpenRead(zip))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download checksum does not match.");
            await Task.Run(() => Extract(zip, game, token), token);
            return root.GetProperty("tag_name").GetString() ?? "";
        }
        finally { try { Directory.Delete(work, true); } catch { /* Temporary cleanup must not hide the install result. */ } }
    }

    private static void Extract(string zip, string game, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zip);
        string prefix = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<(ZipArchiveEntry Entry, string Path)>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (!(name == "hostfxr.dll" || name.StartsWith("VulkanStory/", StringComparison.Ordinal) ||
                  name.StartsWith("Mods/vulkanstory/", StringComparison.Ordinal) || name.StartsWith("Mods/vulkanstoryinput/", StringComparison.Ordinal)) ||
                name.Split('/').Any(p => p is ".." or "." or "" || p.Contains(':')))
                throw new InvalidDataException("Unexpected archive path: " + name);
            string destination = Path.GetFullPath(Path.Combine(game, name));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !destinations.Add(destination))
                throw new InvalidDataException("Invalid archive destination: " + name);
            for (string? parent = Path.GetDirectoryName(destination); parent != null && parent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Installation path contains a directory link: " + parent);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Installation would overwrite an existing file: " + name);
            files.Add((entry, destination));
        }
        if (!archive.Entries.Any(e => e.FullName == "hostfxr.dll") || !archive.Entries.Any(e => e.FullName == "VulkanStory/package.json"))
            throw new InvalidDataException("The archive is missing the runtime or shim.");
        var written = new List<string>();
        try
        {
            // Install the shim last so a failed extraction cannot activate an incomplete runtime.
            foreach (var item in files.OrderBy(f => f.Entry.FullName == "hostfxr.dll" ? 1 : 0))
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                using var output = new FileStream(item.Path, FileMode.CreateNew, FileAccess.Write);
                written.Add(item.Path);
                using var input = item.Entry.Open();
                input.CopyTo(output);
            }
        }
        catch
        {
            foreach (string path in written.AsEnumerable().Reverse()) File.Delete(path);
            throw;
        }
    }
}

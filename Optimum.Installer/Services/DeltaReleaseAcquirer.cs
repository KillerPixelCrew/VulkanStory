using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Installer.Services;

/// <summary>Fetches one exact-version release asset and verifies GitHub's SHA-256 before extraction.</summary>
public sealed class DeltaReleaseAcquirer(HttpClient client, string cacheDirectory)
{
    private const string Repository = "StratumServer/Optimum";
    private const long MaximumArchive = 2L * 1024 * 1024 * 1024;
    private const long MaximumExtracted = 4L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DeltaReleaseAcquirer CreateDefault()
    {
        string? cache = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(cache) || !Path.IsPathFullyQualified(cache))
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return new DeltaReleaseAcquirer(new HttpClient { Timeout = TimeSpan.FromMinutes(15) },
            Path.Combine(cache, "Optimum", "DeltaDownloads"));
    }

    public async Task<string> AcquireAsync(string gameVersion, string optimumVersion, string rid,
        CancellationToken token)
    {
        if (!ValidComponent(gameVersion) || !ValidComponent(optimumVersion) ||
            rid is not ("win-x64" or "linux-x64") || !Path.IsPathFullyQualified(cacheDirectory))
            throw new InvalidDataException("Invalid release version, platform or cache path.");
        string tag = "v" + optimumVersion;
        string name = $"Optimum-{tag}-VS{gameVersion}-{rid}-Delta.zip";
        string api = $"https://api.github.com/repos/{Repository}/releases/tags/{tag}";
        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Optimum.Installer", optimumVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        byte[] metadata = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(token),
            2 * 1024 * 1024, token);
        (long size, string hash) = FindAsset(metadata, tag, name);

        if (!SymlinkComponentCheck.IsClean(SystemProbe.Default, cacheDirectory))
            throw new InvalidDataException("Linked download cache is not supported.");
        Directory.CreateDirectory(cacheDirectory);
        string archive = Path.Combine(cacheDirectory, hash + ".zip");
        if (!await MatchesAsync(archive, size, hash, token))
            await DownloadAsync(tag, name, archive, size, hash, optimumVersion, token);

        string staging = Path.Combine(cacheDirectory, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractAsync(archive, staging, token);
            string descriptorPath = Path.Combine(staging, DeltaReleaseService.DescriptorName);
            if (!File.Exists(descriptorPath) || new FileInfo(descriptorPath).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Downloaded release has no valid descriptor.");
            var descriptor = JsonSerializer.Deserialize<DeltaRelease>(
                await File.ReadAllTextAsync(descriptorPath, token), Json);
            string manifestPath = Path.Combine(staging, "delta-pack", "manifest.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 64 * 1024)
                throw new InvalidDataException("Downloaded release has no valid delta manifest.");
            var manifest = JsonSerializer.Deserialize<BinaryDeltaManifest>(
                await File.ReadAllTextAsync(manifestPath, token), Json);
            if (descriptor?.GameVersion != gameVersion || descriptor.OptimumVersion != optimumVersion ||
                descriptor.Rid != rid || manifest?.Format != BinaryDeltaPack.Format ||
                manifest.GameVersion != gameVersion || manifest.OptimumVersion != optimumVersion ||
                manifest.Rid != rid ||
                !Directory.Exists(Path.Combine(staging, "runtime-payload")) ||
                !File.Exists(Path.Combine(staging, "delta-decoder", "decoder.json")))
                throw new InvalidDataException("Downloaded release does not match the selected game and platform.");
            return staging;
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static bool ValidComponent(string value) => value.Length is > 0 and < 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static (long Size, string Hash) FindAsset(byte[] metadata, string tag, string name)
    {
        using var document = JsonDocument.Parse(metadata);
        JsonElement release = document.RootElement;
        if (release.GetProperty("tag_name").GetString() != tag ||
            release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The matching release is not published.");
        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            if (asset.GetProperty("state").GetString() != "uploaded") break;
            long size = asset.GetProperty("size").GetInt64();
            string? digest = asset.GetProperty("digest").GetString();
            if (size is < 1 or > MaximumArchive || digest is null ||
                !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != 71 || !digest.AsSpan(7).ToString().All(Uri.IsHexDigit)) break;
            return (size, digest[7..].ToUpperInvariant());
        }
        throw new InvalidDataException("No complete matching delta archive with a SHA-256 digest was found.");
    }

    private async Task DownloadAsync(string tag, string name, string archive, long size, string hash,
        string optimumVersion, CancellationToken token)
    {
        string temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://github.com/{Repository}/releases/download/{tag}/{name}");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Optimum.Installer", optimumVersion));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != size)
                throw new InvalidDataException("Downloaded release size differs from its published size.");
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[1024 * 1024];
                long written = 0;
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    written += count;
                    if (written > size) throw new InvalidDataException("Downloaded release exceeds its published size.");
                    hasher.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (written != size || !Convert.ToHexString(hasher.GetHashAndReset()).Equals(hash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded release does not match its published SHA-256.");
            }
            File.Move(temporary, archive, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<bool> MatchesAsync(string path, long size, string hash, CancellationToken token)
    {
        if (!File.Exists(path) || !SymlinkComponentCheck.IsClean(SystemProbe.Default, path)) return false;
        await using var stream = File.OpenRead(path);
        return stream.Length == size && Convert.ToHexString(await SHA256.HashDataAsync(stream, token))
            .Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream source, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Release metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task ExtractAsync(string archivePath, string staging, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 4 or > 20000)
            throw new InvalidDataException("Downloaded release has an invalid file count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName;
            if (!AllowedEntry(name) || !names.Add(name) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                entry.Length is < 1 or > 512L * 1024 * 1024 ||
                (total += entry.Length) > MaximumExtracted)
                throw new InvalidDataException("Downloaded release contains an invalid archive entry.");
            if (name == DeltaReleaseService.DescriptorName && entry.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Downloaded release descriptor is too large.");
            string path = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            byte[] buffer = new byte[1024 * 1024];
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                written += count;
                if (written > entry.Length)
                    throw new InvalidDataException("Downloaded release archive entry exceeds its declared size.");
                await target.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (written != entry.Length)
                throw new InvalidDataException("Downloaded release archive entry was truncated.");
            if (OperatingSystem.IsLinux() &&
                name is "delta-uninstaller" or "delta-decoder/xdelta3" or "runtime-payload/Optimum" or
                    "runtime-payload/delta-decoder/xdelta3")
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static bool AllowedEntry(string name)
    {
        if (name.Length is < 1 or > 512 ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or '+' or '-'))
            return false;
        foreach (string part in name.Split('/'))
        {
            if (part.Length is < 1 or > 128 || part is "." or ".." || part.EndsWith('.')) return false;
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or
                "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or
                "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
                return false;
        }
        return name is "delta-release.json" or "delta-uninstaller.exe" or "delta-uninstaller" ||
            name.StartsWith("delta-pack/", StringComparison.Ordinal) ||
            name.StartsWith("runtime-payload/", StringComparison.Ordinal) ||
            name.StartsWith("delta-decoder/", StringComparison.Ordinal);
    }
}

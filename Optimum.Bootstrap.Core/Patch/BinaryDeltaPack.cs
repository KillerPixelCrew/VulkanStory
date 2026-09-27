using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Patch;

public sealed record BinaryDeltaFile(string Path, string Delta, long InputSize, string InputSha256,
    long OutputSize, string OutputSha256, long DeltaSize, string DeltaSha256,
    string? SourcePath = null);

public sealed record BinaryDeltaManifest(string Format, string GameVersion, string OptimumVersion,
    string Rid, IReadOnlyList<BinaryDeltaFile> Files);

public interface IBinaryDeltaDecoder
{
    Task DecodeAsync(string original, string delta, string output, CancellationToken cancellationToken);
}

/// <summary>
/// Applies a locally acquired pack into a NEW directory. The caller authenticates
/// the release manifest before calling; hashes establish integrity, not publisher identity.
/// Does not activate the cache, deploy mods, or modify the source installation.
/// </summary>
public sealed class BinaryDeltaPack(IBinaryDeltaDecoder decoder)
{
    public const string Format = "optimum-vcdiff-1";
    private const long MaximumFileSize = 512L * 1024 * 1024;
    private static readonly string[] Targets =
        ["VintagestoryLib.dll", "VintagestoryAPI.dll", "Mods/VSEssentials.dll", "Mods/VSSurvivalMod.dll"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BinaryDeltaManifest> ApplyAsync(string originalDirectory, string packDirectory,
        string outputDirectory, string expectedGameVersion, string expectedOptimumVersion, string expectedRid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string original = Absolute(originalDirectory), pack = Absolute(packDirectory), output = Absolute(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Delta output already exists; choose a new cache directory.");
        if (Within(output, original) || Within(output, pack))
            throw new InvalidDataException("Delta output must be outside the original and pack directories.");
        RequireNoLinks(output);
        string manifestPath = Path.Combine(pack, "manifest.json");
        RequireNoLinks(manifestPath);
        if (new FileInfo(manifestPath).Length > 64 * 1024)
            throw new InvalidDataException("Delta manifest is too large.");
        var manifest = JsonSerializer.Deserialize<BinaryDeltaManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), Json)
            ?? throw new InvalidDataException("Missing delta manifest.");
        Validate(manifest, expectedGameVersion, expectedOptimumVersion, expectedRid);

        // Complete preflight before invoking the decoder or creating the destination.
        foreach (var file in manifest.Files)
        {
            await VerifyAsync(Path.Combine(original, file.SourcePath ?? file.Path), file.InputSize, file.InputSha256, cancellationToken);
            await VerifyAsync(Path.Combine(pack, file.Delta), file.DeltaSize, file.DeltaSha256, cancellationToken);
        }

        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".optimum-delta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string result = Path.Combine(staging, "result");
            Directory.CreateDirectory(result);
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Decode verified snapshots so a game update cannot change the source
                // between its hash check and the decoder reading it.
                string inputCopy = Path.Combine(staging, "input"), deltaCopy = Path.Combine(staging, "delta");
                File.Copy(Path.Combine(original, file.SourcePath ?? file.Path), inputCopy, overwrite: true);
                File.Copy(Path.Combine(pack, file.Delta), deltaCopy, overwrite: true);
                await VerifyAsync(inputCopy, file.InputSize, file.InputSha256, cancellationToken);
                await VerifyAsync(deltaCopy, file.DeltaSize, file.DeltaSha256, cancellationToken);
                string destination = Path.Combine(result, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await decoder.DecodeAsync(inputCopy, deltaCopy, destination, cancellationToken);
                await VerifyAsync(destination, file.OutputSize, file.OutputSha256, cancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(result, "delta-manifest.json"), JsonSerializer.Serialize(manifest, Json), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequireNoLinks(output);
            Directory.Move(result, output);
            return manifest;
        }
        finally
        {
            // Only this invocation's generated staging directory is removed.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal static void Validate(BinaryDeltaManifest manifest, string game, string optimum, string rid)
    {
        if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(optimum) ||
            rid is not ("win-x64" or "linux-x64") || manifest.Format != Format ||
            manifest.GameVersion != game || manifest.OptimumVersion != optimum || manifest.Rid != rid)
            throw new InvalidDataException("Delta pack version or platform does not match the requested runtime.");
        if (manifest.Files is null || manifest.Files.Count < Targets.Length || manifest.Files.Count > 256 ||
            manifest.Files.Any(f => f is null) ||
            manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
            !Targets.All(target => manifest.Files.Any(f => f.Path == target)) ||
            manifest.Files.Any(f => !Targets.Contains(f.Path, StringComparer.Ordinal) && !IsShaderPath(f.Path)))
            throw new InvalidDataException("Delta pack has missing, duplicate or unsupported targets.");
        foreach (var file in manifest.Files)
        {
            string source = file.SourcePath ?? file.Path;
            if (file.Delta != file.Path + ".vcdiff" ||
                (file.SourcePath is not null && (Targets.Contains(file.Path, StringComparer.Ordinal) || !IsShaderPath(source))) ||
                !ValidSize(file.InputSize) || !ValidSize(file.OutputSize) || !ValidSize(file.DeltaSize) ||
                !ValidHash(file.InputSha256) || !ValidHash(file.OutputSha256) || !ValidHash(file.DeltaSha256))
                throw new InvalidDataException($"Invalid delta entry: {file.Path}");
        }
    }

    private static bool IsShaderPath(string? path)
    {
        if (path is null) return false;
        string prefix = path.StartsWith("assets/game/shaders/", StringComparison.Ordinal)
            ? "assets/game/shaders/" : "assets/game/shaderincludes/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string leaf = path[prefix.Length..];
        return leaf.Length > 0 && leaf.Length <= 128 &&
            (leaf.EndsWith(".vsh", StringComparison.Ordinal) || leaf.EndsWith(".fsh", StringComparison.Ordinal) ||
             leaf.EndsWith(".gsh", StringComparison.Ordinal)) &&
            leaf.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }

    private static bool ValidSize(long size) => size > 0 && size <= MaximumFileSize;
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Absolute(string value) => Path.IsPathFullyQualified(value)
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(value))
        : throw new ArgumentException("Delta paths must be absolute.");
    private static bool Within(string path, string root) => path.Equals(root, Comparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, Comparison);
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static void RequireNoLinks(string path)
    {
        if (!SymlinkComponentCheck.IsClean(SystemProbe.Default, path))
            throw new InvalidDataException($"Symbolic links are not supported in delta paths: {path}");
    }

    internal static async Task VerifyAsync(string path, long length, string expectedHash, CancellationToken token)
    {
        RequireNoLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != length || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token))
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Delta size/hash mismatch: {path}");
    }
}

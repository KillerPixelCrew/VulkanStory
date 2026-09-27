using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Installer.Services;

public interface IDeltaReleaseService
{
    Task InstallAsync(string original, string destination, CancellationToken token,
        string? dataPath = null, ShortcutKinds shortcuts = ShortcutKinds.None,
        DeltaInstallPreset preset = DeltaInstallPreset.ExistingSettings);
    Task<string> RepairAsync(string destination, CancellationToken token);
}

public sealed record DeltaPayloadFile(string Path, long Size, string Sha256);
public sealed record DeltaRelease(string GameVersion, string OptimumVersion, string Rid,
    IReadOnlyList<DeltaPayloadFile> PayloadFiles, DeltaPayloadFile? Uninstaller = null);

/// <summary>Local release route. Release acquisition/authentication belongs to packaging.</summary>
public sealed class DeltaReleaseService(string releaseDirectory) : IDeltaReleaseService
{
    public const string DescriptorName = "delta-release.json";
    public Task InstallAsync(string original, string destination, CancellationToken token,
        string? dataPath = null, ShortcutKinds shortcuts = ShortcutKinds.None,
        DeltaInstallPreset preset = DeltaInstallPreset.ExistingSettings) => Task.Run(async () =>
    {
        if (!Enum.IsDefined(preset)) throw new InvalidDataException("Unknown install preset.");
        if (preset != DeltaInstallPreset.ExistingSettings &&
            (dataPath is null || !DeltaPresetSettings.IsEmptyDataPath(dataPath)))
            throw new InvalidDataException("Presets need a new or empty separate data folder.");
        if (dataPath is not null)
        {
            string? parent = Path.IsPathFullyQualified(dataPath)
                ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dataPath)) : null;
            if (parent is null || !Directory.Exists(parent) || File.Exists(dataPath))
                throw new InvalidDataException("Choose an existing data folder or a new folder under an existing parent.");
            var verdict = InstallPathGuard.Check(SystemProbe.Default,
                new InstallPathRequest(destination, dataPath, original));
            if (!verdict.Ok) throw new InvalidDataException(verdict.Rejection);
        }
        var release = await ReadMatchingReleaseAsync(token);
        string payload = Path.Combine(releaseDirectory, "runtime-payload");
        await VerifyPayloadAsync(payload, release.PayloadFiles, token);
        string? uninstallerPath = null;
        if (release.Uninstaller is not null)
        {
            string bundledUninstaller = await VerifyUninstallerAsync(releaseDirectory, release.Uninstaller, token);
            if (OperatingSystem.IsWindows())
                uninstallerPath = PrepareWindowsUninstaller(bundledUninstaller, release.Uninstaller.Sha256);
            else if (OperatingSystem.IsLinux())
                uninstallerPath = PrepareLinuxUninstaller(bundledUninstaller, release.Uninstaller.Sha256,
                    SystemProbe.Default);
        }
        bool createdDataFolder = false;
        string? seededConfig = null;
        try
        {
            await new DeltaRuntimeInstaller(BundledDeltaDecoder.Create(releaseDirectory)).InstallAsync(
                original, Path.Combine(releaseDirectory, "delta-pack"), payload, destination,
                release.GameVersion, release.OptimumVersion, release.Rid, token, async (runtime, cancellation) =>
                {
                    await VerifyPayloadAsync(runtime, release.PayloadFiles, cancellation, allowOriginalFiles: true);
                    if (dataPath is not null)
                        await File.WriteAllTextAsync(Path.Combine(runtime, "datapath.cfg"), dataPath, cancellation);
                    await DeltaRuntimeValidator.ValidateAsync(runtime, cancellation);
                    if (dataPath is not null && !Directory.Exists(dataPath))
                    {
                        Directory.CreateDirectory(dataPath);
                        createdDataFolder = true;
                    }
                    if (dataPath is not null && preset != DeltaInstallPreset.ExistingSettings)
                        seededConfig = await DeltaPresetSettings.SeedAsync(dataPath, preset, cancellation);
                    string[] entries = Directory.EnumerateFileSystemEntries(runtime)
                        .Select(Path.GetFileName)
                        .Where(name => name is not null && name != ".optimum")
                        .Select(name => name!)
                        .ToArray();
                    var install = new InstallManifest
                    {
                        OptimumVersion = release.OptimumVersion,
                        InstalledAtUtc = DateTimeOffset.UtcNow,
                        InstallDirectory = Path.GetFullPath(destination),
                        DataPath = dataPath,
                        Launcher = Path.Combine(Path.GetFullPath(destination), OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum"),
                        Entries = entries,
                    };
                    await File.WriteAllTextAsync(Path.Combine(runtime, InstallManifest.RelativePath),
                        install.Serialize(), cancellation);
                });
        }
        catch
        {
            if (seededConfig is not null)
            {
                try
                {
                    File.Delete(seededConfig);
                    string configDirectory = Path.GetDirectoryName(seededConfig)!;
                    if (!Directory.EnumerateFileSystemEntries(configDirectory).Any()) Directory.Delete(configDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (createdDataFolder && dataPath is not null)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dataPath).Any()) Directory.Delete(dataPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        if (shortcuts != ShortcutKinds.None)
            RecordShortcuts(destination, shortcuts, SystemProbe.Default);
        if (uninstallerPath is not null &&
            OperatingSystem.IsWindows() &&
            UninstallRegistration.RegisterDelta(destination, release.OptimumVersion, uninstallerPath) is string key)
            RecordRegistration(destination, key);
        if (uninstallerPath is not null && OperatingSystem.IsLinux())
            RecordLinuxUninstallEntry(destination, uninstallerPath, SystemProbe.Default);
    }, token);

    public Task<string> RepairAsync(string destination, CancellationToken token) => Task.Run(async () =>
    {
        var release = await ReadMatchingReleaseAsync(token);
        string payload = Path.Combine(releaseDirectory, "runtime-payload");
        await VerifyPayloadAsync(payload, release.PayloadFiles, token);
        return await new DeltaRuntimeRepairer(BundledDeltaDecoder.Create(releaseDirectory)).RepairAsync(
            destination, Path.Combine(releaseDirectory, "delta-pack"), payload,
            release.GameVersion, release.OptimumVersion, release.Rid, token,
            async (runtime, cancellation) =>
            {
                await VerifyPayloadAsync(runtime, release.PayloadFiles, cancellation, allowOriginalFiles: true);
                await DeltaRuntimeValidator.ValidateAsync(runtime, cancellation);
            });
    }, token);

    private async Task<DeltaRelease> ReadMatchingReleaseAsync(CancellationToken token)
    {
        string descriptor = Path.Combine(releaseDirectory, DescriptorName);
        if (new FileInfo(descriptor).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Release description is too large.");
        var release = JsonSerializer.Deserialize<DeltaRelease>(await File.ReadAllTextAsync(descriptor, token),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Missing release description.");
        if (release.OptimumVersion != CoreInfo.Version || release.Rid != RuntimeInformation.RuntimeIdentifier)
            throw new InvalidDataException("This release does not match the installer version or this computer.");
        return release;
    }

    internal static async Task<string> VerifyUninstallerAsync(string releaseDirectory, DeltaPayloadFile file,
        CancellationToken token)
    {
        string expected = OperatingSystem.IsWindows() ? "delta-uninstaller.exe" : "delta-uninstaller";
        if (file.Path != expected || file.Size is < 1 or > 100_000_000 ||
            file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid standalone uninstaller description.");
        string path = Path.Combine(releaseDirectory, expected);
        if (!SymlinkComponentCheck.IsClean(SystemProbe.Default, path))
            throw new InvalidDataException("Linked standalone uninstaller is not supported.");
        await using var stream = File.OpenRead(path);
        if (stream.Length != file.Size ||
            !Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(file.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Standalone uninstaller is damaged.");
        return path;
    }

    internal static string PrepareWindowsUninstaller(string source, string sha256)
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            throw new InvalidDataException("Windows local application data folder is unavailable.");
        string directory = Path.Combine(localData, "Optimum", "UninstallTools");
        return PrepareContentAddressedUninstaller(source, sha256, directory, ".exe");
    }

    internal static string PrepareLinuxUninstaller(string source, string sha256, ISystemProbe probe)
    {
        string dataHome = probe.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(probe.HomeDirectory, ".local", "share");
        if (!Path.IsPathFullyQualified(dataHome))
            throw new InvalidDataException("Linux application data folder must be absolute.");
        string directory = Path.Combine(dataHome, "optimum", "uninstall-tools");
        string target = PrepareContentAddressedUninstaller(source, sha256, directory, "");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute);
        return target;
    }

    private static string PrepareContentAddressedUninstaller(string source, string sha256,
        string directory, string suffix)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, sha256.ToUpperInvariant() + suffix);
        if (!File.Exists(target))
        {
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary);
                try { File.Move(temporary, target); }
                catch (IOException) when (File.Exists(target)) { /* another install won the race */ }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        using var stream = File.OpenRead(target);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stored standalone uninstaller does not match the release.");
        return target;
    }

    internal static void RecordLinuxUninstallEntry(string destination, string uninstallerPath,
        ISystemProbe probe)
    {
        string manifestPath = Path.Combine(destination, InstallManifest.RelativePath);
        var manifest = InstallManifest.Deserialize(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Installed runtime has no uninstall manifest.");
        var writer = new ShortcutWriter(probe);
        string? entry = writer.CreateLinuxUninstallEntry(destination, uninstallerPath);
        if (entry is null)
            throw new IOException("Could not create the Linux uninstall menu entry.");
        string temporary = manifestPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, (manifest with { Shortcuts = [.. manifest.Shortcuts, entry] }).Serialize());
            File.Move(temporary, manifestPath, overwrite: true);
        }
        catch
        {
            writer.Remove([entry]);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void RecordRegistration(string destination, string key)
    {
        string path = Path.Combine(destination, InstallManifest.RelativePath);
        var manifest = InstallManifest.Deserialize(File.ReadAllText(path))
            ?? throw new InvalidDataException("Installed runtime has no uninstall manifest.");
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, (manifest with { UninstallRegistryKey = key }).Serialize());
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            UninstallRegistration.Unregister(key, destination);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void RecordShortcuts(string destination, ShortcutKinds kinds, ISystemProbe probe)
    {
        string manifestPath = Path.Combine(destination, InstallManifest.RelativePath);
        var manifest = InstallManifest.Deserialize(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Installed runtime has no uninstall manifest.");
        var writer = new ShortcutWriter(probe);
        IReadOnlyList<string> created = writer.Create(destination, manifest.Launcher!, kinds);
        string temporary = manifestPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, (manifest with { Shortcuts = created }).Serialize());
            File.Move(temporary, manifestPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writer.Remove(created);
            try { File.Delete(temporary); } catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException) { }
            // A shortcut failure does not invalidate the verified runtime.
        }
    }

    internal static async Task VerifyPayloadAsync(string root, IReadOnlyList<DeltaPayloadFile>? files, CancellationToken token, bool allowOriginalFiles = false)
    {
        if (files is null || files.Count == 0 || files.Count > 10000)
            throw new InvalidDataException("The runtime payload has no valid inventory.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path) || file.Path.Contains('\\') ||
                file.Path.Contains(':') || file.Path.Split('/').Any(p => p is "" or "." or "..") ||
                !paths.Add(file.Path) || file.Size < 1 || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid runtime payload inventory.");
            string name = Path.GetFileName(file.Path);
            if (new[] { "Vintagestory", "VSEssentials", "VSSurvivalMod", "VSCreativeMod" }
                    .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                name.Contains("donor", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Game and donor files do not belong in the release payload: {file.Path}");
            string path = Path.Combine(root, file.Path);
            if (!SymlinkComponentCheck.IsClean(SystemProbe.Default, path)) throw new InvalidDataException("Linked payload files are not supported.");
            await using var stream = File.OpenRead(path);
            if (stream.Length != file.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Runtime payload is damaged: {file.Path}");
        }
        foreach (string required in new[] { "Optimum.dll", "Optimum.deps.json", "Optimum.runtimeconfig.json",
            "Optimum.Bootstrap.Core.dll", "Optimum.Render.Vulkan.dll", "Optimum.Api.Contracts.dll",
            "Optimum.GameContent.dll", "shaders-vk/shaders.manifest.json", OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum",
            OperatingSystem.IsWindows() ? "SDL3.dll" : "libSDL3.so",
            "gamecontrollerdb.txt", "ControllerMappings-LICENSE.txt" })
            if (!paths.Contains(required)) throw new InvalidDataException($"Runtime payload is incomplete: {required}");
        if (!allowOriginalFiles) CheckDirectory(root);
        void CheckDirectory(string directory)
        {
            if (!SymlinkComponentCheck.IsClean(SystemProbe.Default, directory)) throw new InvalidDataException("Linked payload directory.");
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(path)) CheckDirectory(path);
                else if (!paths.Contains(Path.GetRelativePath(root, path).Replace('\\', '/')))
                    throw new InvalidDataException($"Unlisted runtime payload file: {path}");
            }
        }
    }

}

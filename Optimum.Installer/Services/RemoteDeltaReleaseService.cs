using System.Runtime.InteropServices;
using System.Text.Json;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Installer.Services;

/// <summary>Acquires a matching release, then uses the same verified local install and repair path.</summary>
public sealed class RemoteDeltaReleaseService(DeltaReleaseAcquirer acquirer) : IDeltaReleaseService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InstallAsync(string original, string destination, CancellationToken token,
        string? dataPath = null, ShortcutKinds shortcuts = ShortcutKinds.None,
        DeltaInstallPreset preset = DeltaInstallPreset.ExistingSettings)
    {
        if (!Path.IsPathFullyQualified(original)) throw new InvalidDataException("Choose an original game folder.");
        string selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(original));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        GameInstallation? installation = new GameInstallProbe(SystemProbe.Default).Detect([selected])
            .FirstOrDefault(found => found.Directory.Equals(selected, comparison));
        if (installation?.Version is null)
            throw new InvalidDataException("The original game's version could not be read.");
        string release = await acquirer.AcquireAsync(installation.Version, CoreInfo.Version,
            RuntimeInformation.RuntimeIdentifier, token);
        try
        {
            await new DeltaReleaseService(release).InstallAsync(original, destination, token,
                dataPath, shortcuts, preset);
        }
        finally { RemoveExtraction(release); }
    }

    public async Task<string> RepairAsync(string destination, CancellationToken token)
    {
        if (!DeltaRuntimeGuard.IsSeparateRuntime(destination))
            throw new InvalidDataException("Choose an existing separate Optimum installation.");
        string receiptPath = Path.Combine(destination, DeltaRuntimeInstaller.ReceiptPath);
        if (new FileInfo(receiptPath).Length > 64 * 1024)
            throw new InvalidDataException("The installed runtime receipt is too large.");
        var receipt = JsonSerializer.Deserialize<DeltaRuntimeReceipt>(
            await File.ReadAllTextAsync(receiptPath, token), Json);
        if (receipt?.Manifest?.GameVersion is null ||
            receipt.Manifest.OptimumVersion != CoreInfo.Version)
            throw new InvalidDataException("Repair requires the matching Optimum installer version.");
        string release = await acquirer.AcquireAsync(receipt.Manifest.GameVersion, CoreInfo.Version,
            RuntimeInformation.RuntimeIdentifier, token);
        try { return await new DeltaReleaseService(release).RepairAsync(destination, token); }
        finally { RemoveExtraction(release); }
    }

    private static void RemoveExtraction(string path)
    {
        try
        {
            if (Path.GetFileName(path).StartsWith(".extract-", StringComparison.Ordinal) &&
                Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache-cleanup failure cannot undo an activated runtime or repair.
        }
    }
}

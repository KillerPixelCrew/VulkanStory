using System.Text.Json;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Patch;

/// <summary>Identifies a separate delta runtime before a removal operation.</summary>
public static class DeltaRuntimeGuard
{
    /// <summary>Find the original game's own launcher without loading game code.</summary>
    public static string? FindOriginalLauncher(string runtime)
    {
        try
        {
            if (!Path.IsPathFullyQualified(runtime)) return null;
            string receiptPath = Path.Combine(runtime, DeltaRuntimeInstaller.ReceiptPath);
            if (!SymlinkComponentCheck.IsClean(SystemProbe.Default, receiptPath)) return null;
            var info = new FileInfo(receiptPath);
            if (!info.Exists || info.Length is < 1 or > 64 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(receiptPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("originalDirectory", out var element) ||
                element.ValueKind != JsonValueKind.String) return null;
            string? original = element.GetString();
            if (string.IsNullOrWhiteSpace(original) || !Path.IsPathFullyQualified(original)) return null;
            original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(original));
            string installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (Overlaps(original, installed, comparison) ||
                !File.Exists(Path.Combine(original, "Vintagestory.dll")) ||
                !Directory.Exists(Path.Combine(original, "assets"))) return null;
            string launcher = Path.Combine(original, OperatingSystem.IsWindows()
                ? "Vintagestory.exe" : "Vintagestory");
            return File.Exists(launcher) ? launcher : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    public static bool IsSeparateRuntime(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return false;
        try
        {
            string receiptPath = Path.Combine(directory, DeltaRuntimeInstaller.ReceiptPath);
            string manifestPath = Path.Combine(directory, InstallManifest.RelativePath);
            if (!SymlinkComponentCheck.IsClean(SystemProbe.Default, directory) ||
                !SymlinkComponentCheck.IsClean(SystemProbe.Default, receiptPath) ||
                !SymlinkComponentCheck.IsClean(SystemProbe.Default, manifestPath)) return false;
            if (new FileInfo(receiptPath).Length > 64 * 1024 ||
                new FileInfo(manifestPath).Length > 1024 * 1024) return false;
            var receipt = JsonSerializer.Deserialize<DeltaRuntimeReceipt>(File.ReadAllText(receiptPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var manifest = InstallManifest.Deserialize(File.ReadAllText(manifestPath));
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (receipt?.Manifest?.Format != BinaryDeltaPack.Format ||
                string.IsNullOrWhiteSpace(receipt.OriginalDirectory) ||
                !Path.IsPathFullyQualified(receipt.OriginalDirectory) ||
                manifest is null ||
                string.IsNullOrWhiteSpace(manifest.InstallDirectory) ||
                !Path.IsPathFullyQualified(manifest.InstallDirectory) ||
                manifest.Entries is null || manifest.Shortcuts is null ||
                (manifest.UninstallRegistryKey is not null &&
                 !manifest.UninstallRegistryKey.Equals(UninstallRegistration.DeltaKeyFor(target), comparison)) ||
                !target.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.InstallDirectory)), comparison))
                return false;

            string original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(receipt.OriginalDirectory));
            if (Overlaps(target, original, comparison)) return false;
            if (manifest.DataPath is not null)
            {
                if (!Path.IsPathFullyQualified(manifest.DataPath) ||
                    Overlaps(target, Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.DataPath)), comparison))
                    return false;
            }

            return manifest.Entries.All(entry =>
                !string.IsNullOrWhiteSpace(entry) &&
                entry is not "." and not ".." &&
                !entry.Contains('/') && !entry.Contains('\\') && !entry.Contains(':') &&
                SymlinkComponentCheck.IsClean(SystemProbe.Default, Path.Combine(target, entry)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool Overlaps(string left, string right, StringComparison comparison) =>
        left.Equals(right, comparison) ||
        left.StartsWith(WithSeparator(right), comparison) ||
        right.StartsWith(WithSeparator(left), comparison);

    private static string WithSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}

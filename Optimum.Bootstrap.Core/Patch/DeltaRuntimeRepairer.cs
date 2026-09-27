using System.Text.Json;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Patch;

/// <summary>Rebuilds a damaged delta runtime from the same release and keeps the previous copy as a backup.</summary>
public sealed class DeltaRuntimeRepairer(IBinaryDeltaDecoder decoder)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> RepairAsync(string runtime, string pack, string payload,
        string gameVersion, string optimumVersion, string rid, CancellationToken token = default,
        Func<string, CancellationToken, Task>? validateRuntime = null)
    {
        if (!Path.IsPathFullyQualified(runtime) || !DeltaRuntimeGuard.IsSeparateRuntime(runtime))
            throw new InvalidDataException("Choose an existing separate Optimum delta installation.");
        runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime));
        string receiptPath = Path.Combine(runtime, DeltaRuntimeInstaller.ReceiptPath);
        var receipt = JsonSerializer.Deserialize<DeltaRuntimeReceipt>(
            await File.ReadAllTextAsync(receiptPath, token), Json)
            ?? throw new InvalidDataException("The installed runtime has no readable delta receipt.");
        var manifest = InstallManifest.Deserialize(await File.ReadAllTextAsync(
            Path.Combine(runtime, InstallManifest.RelativePath), token))
            ?? throw new InvalidDataException("The installed runtime has no readable install manifest.");
        if (receipt.Manifest is null || receipt.Manifest.GameVersion != gameVersion ||
            receipt.Manifest.OptimumVersion != optimumVersion || receipt.Manifest.Rid != rid ||
            manifest.OptimumVersion != optimumVersion)
            throw new InvalidDataException("Repair requires the same game, Optimum version and platform release.");

        string parent = Path.GetDirectoryName(runtime)!;
        string name = Path.GetFileName(runtime);
        string suffix = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, $".{name}-repair-{suffix}");
        string backup = Path.Combine(parent, $"{name}.optimum-backup-{suffix}");
        if (!IsDirectChild(staging, parent) || !IsDirectChild(backup, parent) ||
            Directory.Exists(staging) || File.Exists(staging) ||
            Directory.Exists(backup) || File.Exists(backup) ||
            !SymlinkComponentCheck.IsClean(SystemProbe.Default, staging) ||
            !SymlinkComponentCheck.IsClean(SystemProbe.Default, backup))
            throw new InvalidDataException("Could not reserve safe repair and backup folders.");

        try
        {
            await new DeltaRuntimeInstaller(decoder).InstallAsync(receipt.OriginalDirectory, pack, payload,
                staging, gameVersion, optimumVersion, rid, token, async (rebuilt, cancellation) =>
                {
                    if (manifest.DataPath is not null)
                        await File.WriteAllTextAsync(Path.Combine(rebuilt, "datapath.cfg"),
                            manifest.DataPath, cancellation);
                    if (validateRuntime is not null) await validateRuntime(rebuilt, cancellation);
                    string[] entries = Directory.EnumerateFileSystemEntries(rebuilt)
                        .Select(Path.GetFileName)
                        .Where(entry => entry is not null and not ".optimum")
                        .Select(entry => entry!)
                        .ToArray();
                    var repairedManifest = manifest with
                    {
                        InstalledAtUtc = DateTimeOffset.UtcNow,
                        Entries = entries,
                        Launcher = Path.Combine(runtime, rid == "win-x64" ? "Optimum.exe" : "Optimum"),
                    };
                    await File.WriteAllTextAsync(Path.Combine(rebuilt, InstallManifest.RelativePath),
                        repairedManifest.Serialize(), cancellation);
                });

            token.ThrowIfCancellationRequested();
            Directory.Move(runtime, backup);
            try { Directory.Move(staging, runtime); }
            catch (Exception activationError)
            {
                try { Directory.Move(backup, runtime); }
                catch (Exception rollbackError)
                {
                    throw new IOException($"Repair activation failed; the previous runtime remains at {backup}.",
                        new AggregateException(activationError, rollbackError));
                }
                throw;
            }
            return backup;
        }
        finally
        {
            if (IsDirectChild(staging, parent) && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static bool IsDirectChild(string path, string parent) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(parent),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

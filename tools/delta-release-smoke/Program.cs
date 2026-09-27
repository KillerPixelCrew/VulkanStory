using System.Security.Cryptography;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Win32;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Installer.Services;

if (args.Length == 3 && args[0] == "--remote")
{
    string archive = Path.GetFullPath(args[1]);
    string remoteOutput = Path.GetFullPath(args[2]);
    string proofRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
        ".tools", "binary-delta-poc"));
    if (!Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]) ||
        !archive.StartsWith(proofRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        !remoteOutput.StartsWith(proofRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        Directory.Exists(remoteOutput) || File.Exists(remoteOutput))
        throw new ArgumentException("Remote smoke requires an archive and new output inside the proof directory.");
    string remoteOriginal = Path.Combine(proofRoot, "official-win", "app");
    string gameDll = Path.Combine(remoteOriginal, "VintagestoryLib.dll");
    string hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(gameDll)));
    var info = new FileInfo(archive);
    string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
    string name = Path.GetFileName(archive);
    string metadata = JsonSerializer.Serialize(new
    {
        tag_name = "v0.3.17", draft = false, prerelease = false,
        assets = new[] { new { name, state = "uploaded", size = info.Length,
            digest = "sha256:" + digest } },
    });
    using var client = new HttpClient(new ProofReleaseHandler(archive, metadata));
    var service = new RemoteDeltaReleaseService(new DeltaReleaseAcquirer(client,
        Path.Combine(proofRoot, "remote-cache")));
    await service.InstallAsync(remoteOriginal, remoteOutput, CancellationToken.None);
    await DeltaRuntimeInstaller.VerifyAsync(remoteOutput, "0.3.17", "win-x64");
    File.AppendAllText(Path.Combine(remoteOutput, "VintagestoryLib.dll"), "remote repair proof damage");
    string remoteBackup = await service.RepairAsync(remoteOutput, CancellationToken.None);
    await DeltaRuntimeInstaller.VerifyAsync(remoteOutput, "0.3.17", "win-x64");
    if (!Directory.Exists(remoteBackup) ||
        !File.ReadAllText(Path.Combine(remoteBackup, "VintagestoryLib.dll"))
            .Contains("remote repair proof damage", StringComparison.Ordinal))
        throw new InvalidDataException("Remote repair did not retain the damaged previous copy.");
    if (hashBefore != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(gameDll))))
        throw new InvalidDataException("Remote install or repair changed the original game.");
    Console.WriteLine("Verified remote delta install and repair at " + remoteOutput);
    return 0;
}

if (args.Length == 3 && args[0] == "--repair")
{
    string repairRelease = Path.GetFullPath(args[1]);
    string runtime = Path.GetFullPath(args[2]);
    string proofRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
        ".tools", "binary-delta-poc"));
    if (!Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]) ||
        !runtime.StartsWith(proofRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        !DeltaRuntimeGuard.IsSeparateRuntime(runtime))
        throw new ArgumentException("Repair smoke requires a separate runtime inside the local proof directory.");
    DeltaRuntimeReceipt receipt = System.Text.Json.JsonSerializer.Deserialize<DeltaRuntimeReceipt>(
        File.ReadAllText(Path.Combine(runtime, DeltaRuntimeInstaller.ReceiptPath)),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    string originalDll = Path.Combine(receipt.OriginalDirectory, "VintagestoryLib.dll");
    string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalDll)));
    string patchedDll = Path.Combine(runtime, "VintagestoryLib.dll");
    File.AppendAllText(patchedDll, "repair smoke damage");
    try
    {
        await DeltaRuntimeInstaller.VerifyAsync(runtime, receipt.Manifest.OptimumVersion, receipt.Manifest.Rid);
        throw new InvalidDataException("Damage was not detected before repair.");
    }
    catch (InvalidDataException ex) when (!ex.Message.Contains("Damage was not detected", StringComparison.Ordinal)) { }

    string backup = await new DeltaReleaseService(repairRelease).RepairAsync(runtime, CancellationToken.None);
    await DeltaRuntimeInstaller.VerifyAsync(runtime, receipt.Manifest.OptimumVersion, receipt.Manifest.Rid);
    if (!DeltaRuntimeGuard.IsSeparateRuntime(runtime) || !Directory.Exists(backup) ||
        !File.ReadAllText(Path.Combine(backup, "VintagestoryLib.dll")).Contains("repair smoke damage", StringComparison.Ordinal) ||
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalDll))) != originalHash)
        throw new InvalidDataException("Repair did not preserve the previous copy and original game.");
    Console.WriteLine($"Verified repaired delta runtime at {runtime}; previous copy at {backup}");
    return 0;
}

if (args.Length == 4 && args[0] is "--remove" or "--remove-registered")
{
    string runtime = args[1], game = args[2], data = args[3];
    string proofRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
        ".tools", "binary-delta-poc"));
    string target = Path.GetFullPath(runtime);
    if (!Path.IsPathFullyQualified(runtime) ||
        !target.StartsWith(proofRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        !Path.IsPathFullyQualified(game) || !Path.IsPathFullyQualified(data))
        throw new ArgumentException("Removal smoke targets must be absolute and inside the local proof directory.");

    string gameDll = Path.Combine(game, "VintagestoryLib.dll");
    string hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(gameDll)));
    string dataMarker = Path.Combine(data, "keep-smoke.txt");
    File.WriteAllText(dataMarker, "keep");
    if (args[0] == "--remove-registered")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        InstallManifest registeredManifest = InstallManifest.Deserialize(File.ReadAllText(
            Path.Combine(target, InstallManifest.RelativePath)))!;
        string keyPath = registeredManifest.UninstallRegistryKey
            ?? throw new InvalidDataException("No Windows uninstall entry was recorded.");
        if (keyPath != UninstallRegistration.DeltaKeyFor(target))
            throw new InvalidDataException("Windows uninstall key does not match this runtime.");
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath)
               ?? throw new InvalidDataException("Windows uninstall entry is missing."))
        {
            string? command = key.GetValue("UninstallString") as string;
            string executable = command?.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                ?? throw new InvalidDataException("Windows uninstall command is missing.");
            if (!File.Exists(executable) ||
                !command!.Equals($"\"{executable}\" uninstall-delta --install-dir \"{target}\"",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Windows uninstall command does not target the standalone tool.");
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                ArgumentList = { "uninstall-delta", "--install-dir", target },
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            }) ?? throw new InvalidDataException("Standalone uninstaller could not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidDataException("Standalone uninstaller failed: " + await process.StandardError.ReadToEndAsync());
        }
        using var leftover = Registry.CurrentUser.OpenSubKey(keyPath);
        if (leftover is not null) throw new InvalidDataException("Windows uninstall entry was not removed.");
    }
    else
    {
        var uninstaller = new DeltaUninstallService();
        if (!uninstaller.CanRemove(target))
            throw new InvalidDataException("The installed copy was not accepted as a delta runtime.");
        var removal = await uninstaller.UninstallAsync(target);
        if (!removal.Ok) throw new InvalidDataException(removal.Message);
    }
    if (Directory.Exists(target) || File.ReadAllText(dataMarker) != "keep" ||
        hashBefore != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(gameDll))))
        throw new InvalidDataException("Removal changed the original or data, or left the runtime behind.");
    Console.WriteLine($"Verified {(args[0] == "--remove-registered" ? "registered" : "GUI")} delta removal at {target}");
    return 0;
}

if (args.Length is not (3 or 4) || args.Any(path => !Path.IsPathFullyQualified(path)))
{
    Console.Error.WriteLine("usage: delta-release-smoke <absolute-release> <absolute-original-game> <absolute-new-output> [existing-data-folder]");
    return 2;
}

string release = args[0], original = args[1], output = args[2];
string? dataPath = args.Length == 4 ? args[3] : null;
string originalLib = Path.Combine(original, "VintagestoryLib.dll");
string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalLib)));
await new DeltaReleaseService(release).InstallAsync(original, output, CancellationToken.None, dataPath);
if (dataPath is not null && File.ReadAllText(Path.Combine(output, "datapath.cfg")) != dataPath)
    throw new InvalidDataException("Custom data path was not recorded in the runtime.");
if (dataPath is not null && !Directory.Exists(dataPath))
    throw new InvalidDataException("Custom data folder was not created.");
await DeltaRuntimeInstaller.VerifyAsync(output, "0.3.17", "win-x64");
InstallManifest manifest = InstallManifest.Deserialize(
    File.ReadAllText(Path.Combine(output, InstallManifest.RelativePath)))
    ?? throw new InvalidDataException("Delta runtime lacks an uninstall manifest.");
if (!Path.GetFullPath(manifest.InstallDirectory).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase) ||
    manifest.DataPath != dataPath || !manifest.Entries.Contains("assets"))
    throw new InvalidDataException("Delta uninstall manifest does not describe the installed runtime.");
string after = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalLib)));
if (before != after) throw new InvalidDataException("Original game DLL changed during installation.");
string log = Path.Combine(output, ".optimum", "validation-data", "Logs", "optimum-launcher.log");
if (!File.ReadAllText(log).Contains("Delta runtime validation succeeded.", StringComparison.Ordinal))
    throw new InvalidDataException("The staged launcher did not report successful validation.");
Console.WriteLine($"Verified GUI release service install at {output}");
return 0;

sealed class ProofReleaseHandler(string archive, string metadata) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(archive)) });
}

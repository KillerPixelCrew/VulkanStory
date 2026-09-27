using System.Text.Json;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Patch;

public sealed record DeltaRuntimeReceipt(string OriginalDirectory, BinaryDeltaManifest Manifest);

/// <summary>Builds a separate complete runtime from local originals and a trusted Optimum payload.</summary>
public sealed class DeltaRuntimeInstaller(IBinaryDeltaDecoder decoder)
{
    public const string ReceiptPath = ".optimum/delta-runtime.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InstallAsync(string original, string pack, string payload, string output,
        string gameVersion, string optimumVersion, string rid, CancellationToken token = default,
        Func<string, CancellationToken, Task>? validateRuntime = null)
    {
        foreach (string path in new[] { original, pack, payload, output })
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Runtime paths must be absolute.");
            NoLinks(path);
        }
        original = Path.GetFullPath(original);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Runtime output already exists.");
        foreach (string source in new[] { original, pack, payload })
            if (Within(output, source)) throw new InvalidDataException("Runtime output must be outside its inputs.");
        if (!File.Exists(Path.Combine(original, "Vintagestory.dll")) || !Directory.Exists(Path.Combine(original, "assets")))
            throw new InvalidDataException("A complete original client, including assets, is required.");
        string launcher = rid == "win-x64" ? "Optimum.exe" : "Optimum";
        if (!File.Exists(Path.Combine(payload, launcher)) || !File.Exists(Path.Combine(payload, "Optimum.dll")))
            throw new InvalidDataException("Payload must include a published Optimum launcher.");

        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".optimum-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string patched = Path.Combine(staging, "patched"), runtime = Path.Combine(staging, "runtime");
            var manifest = await new BinaryDeltaPack(decoder).ApplyAsync(original, pack, patched,
                gameVersion, optimumVersion, rid, token);
            var targets = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var symbols = targets.Select(p => Path.ChangeExtension(p, ".pdb")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            CopyTree(original, runtime, relative => !targets.Contains(relative) && !symbols.Contains(relative) &&
                !relative.StartsWith(".optimum/", StringComparison.OrdinalIgnoreCase) &&
                !relative.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase) &&
                !relative.Equals("datapath.cfg", StringComparison.OrdinalIgnoreCase), token);
            CopyTree(payload, runtime, relative =>
            {
                if (targets.Contains(relative) || symbols.Contains(relative) ||
                    relative.StartsWith(".optimum/", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("datapath.cfg", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Payload contains a reserved runtime file: {relative}");
                return true;
            }, token);
            foreach (var file in manifest.Files)
            {
                string destination = Path.Combine(runtime, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(patched, file.Path), destination);
            }
            string receipt = Path.Combine(runtime, ReceiptPath);
            Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new DeltaRuntimeReceipt(original, manifest), Json), token);
            await VerifyAsync(runtime, optimumVersion, rid, token);
            if (validateRuntime is not null) await validateRuntime(runtime, token);
            token.ThrowIfCancellationRequested();
            NoLinks(output);
            Directory.Move(runtime, output);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    /// <summary>Check original compatibility and every patched DLL before loading any game code.</summary>
    public static async Task VerifyAsync(string runtime, string optimumVersion, string rid, CancellationToken token = default)
    {
        string receiptPath = Path.Combine(runtime, ReceiptPath);
        NoLinks(receiptPath);
        if (new FileInfo(receiptPath).Length > 64 * 1024) throw new InvalidDataException("Runtime receipt is too large.");
        var receipt = JsonSerializer.Deserialize<DeltaRuntimeReceipt>(await File.ReadAllTextAsync(receiptPath, token), Json)
            ?? throw new InvalidDataException("Missing runtime receipt.");
        if (receipt.Manifest is null || !Path.IsPathFullyQualified(receipt.OriginalDirectory))
            throw new InvalidDataException("Invalid runtime receipt.");
        BinaryDeltaPack.Validate(receipt.Manifest, receipt.Manifest.GameVersion, optimumVersion, rid);
        foreach (var file in receipt.Manifest.Files)
        {
            await BinaryDeltaPack.VerifyAsync(Path.Combine(receipt.OriginalDirectory, file.SourcePath ?? file.Path), file.InputSize, file.InputSha256, token);
            await BinaryDeltaPack.VerifyAsync(Path.Combine(runtime, file.Path), file.OutputSize, file.OutputSha256, token);
        }
    }

    private static void CopyTree(string source, string destination, Func<string, bool> include, CancellationToken token)
    {
        Walk(source);
        void Walk(string directory)
        {
            NoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                NoLinks(path);
                string relative = Path.GetRelativePath(source, path).Replace('\\', '/');
                if (Directory.Exists(path))
                {
                    if (include(relative + "/")) Walk(path);
                }
                else if (include(relative))
                {
                    string target = Path.Combine(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(path, target, overwrite: true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(path));
                }
            }
        }
    }

    private static void NoLinks(string path)
    {
        if (!SymlinkComponentCheck.IsClean(SystemProbe.Default, path)) throw new InvalidDataException($"Linked runtime path: {path}");
    }
    private static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

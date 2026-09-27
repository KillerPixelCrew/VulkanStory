using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Optimum.Bootstrap.Core.Patch;

public static class BundledDeltaDecoder
{
    public static IBinaryDeltaDecoder Create(string applicationDirectory)
    {
        string directory = Path.Combine(applicationDirectory, "delta-decoder");
        var descriptor = JsonSerializer.Deserialize<Descriptor>(File.ReadAllText(Path.Combine(directory, "decoder.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing decoder descriptor.");
        if (descriptor.Rid != RuntimeInformation.RuntimeIdentifier || descriptor.Version != "3.2.0")
            throw new InvalidDataException("Bundled decoder does not match this platform or supported version.");
        string executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "xdelta3.exe" : "xdelta3");
        using var stream = File.OpenRead(executable);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bundled decoder checksum mismatch. Repair the installation.");
        return new XdeltaProcessDecoder(Path.GetFullPath(executable));
    }

    private sealed record Descriptor(string Version, string Rid, string Sha256);
}

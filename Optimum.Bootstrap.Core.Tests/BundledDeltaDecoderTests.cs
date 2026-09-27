using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Bootstrap.Core.Patch;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class BundledDeltaDecoderTests
{
    [Fact]
    public void ValidatesBundledExecutableBeforeReturningAdapter()
    {
        string root = Path.Combine(Path.GetTempPath(), "decoder-tests-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "delta-decoder");
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "xdelta3.exe" : "xdelta3");
            File.WriteAllText(executable, "fixture only: not executed");
            File.WriteAllText(Path.Combine(directory, "decoder.json"), JsonSerializer.Serialize(new
            {
                version = "3.2.0", rid = RuntimeInformation.RuntimeIdentifier,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))),
            }));
            Assert.IsType<XdeltaProcessDecoder>(BundledDeltaDecoder.Create(root));
            File.AppendAllText(executable, "corrupt");
            Assert.Throws<InvalidDataException>(() => BundledDeltaDecoder.Create(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

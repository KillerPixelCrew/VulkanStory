using System;
using System.IO;
using System.Text.Json;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class DeltaLaunchRecoveryTests
{
    [Fact]
    public void FindsOnlyTheRecordedSeparateOriginalGame()
    {
        string root = Directory.CreateTempSubdirectory("optimum-original-recovery-").FullName;
        try
        {
            string original = Path.Combine(root, "original");
            string runtime = Path.Combine(root, "runtime");
            Directory.CreateDirectory(Path.Combine(original, "assets"));
            Directory.CreateDirectory(Path.Combine(runtime, ".optimum"));
            File.WriteAllText(Path.Combine(original, "Vintagestory.dll"), "game marker");
            string launcher = Path.Combine(original, OperatingSystem.IsWindows()
                ? "Vintagestory.exe" : "Vintagestory");
            File.WriteAllText(launcher, "launcher marker");
            string receipt = Path.Combine(runtime, DeltaRuntimeInstaller.ReceiptPath);
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { originalDirectory = original }));

            Assert.Equal(launcher, DeltaLaunchRecovery.FindOriginalLauncher(runtime));
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { originalDirectory = runtime }));
            Assert.Null(DeltaLaunchRecovery.FindOriginalLauncher(runtime));
            File.WriteAllText(receipt, "{ broken");
            Assert.Null(DeltaLaunchRecovery.FindOriginalLauncher(runtime));
            File.WriteAllText(receipt, "[]");
            Assert.Null(DeltaLaunchRecovery.FindOriginalLauncher(runtime));
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { originalDirectory = original }));
            File.Delete(launcher);
            Assert.Null(DeltaLaunchRecovery.FindOriginalLauncher(runtime));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

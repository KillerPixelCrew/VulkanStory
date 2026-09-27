using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using System.Text.Json;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Tests;
using Optimum.Installer.Services;
using Optimum.Installer.ViewModels;
using Optimum.Installer.Views;
using Xunit;

namespace Optimum.Installer.Tests;

public sealed class DeltaInstallTests
{
    private sealed class Service(Func<CancellationToken, Task> run) : IDeltaReleaseService
    {
        public int Calls { get; private set; }
        public int RepairCalls { get; private set; }
        public string? DataPath { get; private set; }
        public ShortcutKinds Shortcuts { get; private set; }
        public DeltaInstallPreset Preset { get; private set; }
        public Task InstallAsync(string original, string destination, CancellationToken token,
            string? dataPath = null, ShortcutKinds shortcuts = ShortcutKinds.None,
            DeltaInstallPreset preset = DeltaInstallPreset.ExistingSettings)
        {
            Calls++;
            DataPath = dataPath;
            Shortcuts = shortcuts;
            Preset = preset;
            return run(token);
        }
        public async Task<string> RepairAsync(string destination, CancellationToken token)
        {
            RepairCalls++;
            await run(token);
            return Path.Combine(Path.GetDirectoryName(destination)!, "previous-copy");
        }
    }

    private static DeltaInstallViewModel Model(IDeltaReleaseService service) => new(service, new FakeSystemProbe())
    {
        OriginalDirectory = Path.GetFullPath("original"), DestinationDirectory = Path.GetFullPath("destination"),
    };

    [Fact]
    public async Task InstallRequiresConsentAndOnlyCompletesAfterServiceSucceeds()
    {
        var completion = new TaskCompletionSource();
        var service = new Service(_ => completion.Task);
        var model = Model(service);
        Assert.False(model.CanInstall);
        model.Accepted = true;
        var running = model.InstallCommand.ExecuteAsync(null);
        Assert.True(model.Busy);
        Assert.False(model.Completed);
        Assert.False(model.CanEdit);
        completion.SetResult();
        await running;
        Assert.True(model.Completed);
        Assert.False(model.CanInstall);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task FailureLeavesRetryAvailable()
    {
        var model = Model(new Service(_ => throw new InvalidDataException("Missing renderer")));
        model.Accepted = true;
        await model.InstallCommand.ExecuteAsync(null);
        Assert.False(model.Completed);
        Assert.True(model.CanInstall);
        Assert.Contains("Missing renderer", model.Status);
    }

    [Fact]
    public async Task RepairUsesExistingFolderAndReportsBackup()
    {
        var service = new Service(_ => Task.CompletedTask);
        var model = new DeltaInstallViewModel(service, new FakeSystemProbe(), _ => true)
        {
            DestinationDirectory = Path.GetFullPath("repair-target"),
        };
        Assert.True(model.CanRepair);
        await model.RepairCommand.ExecuteAsync(null);
        Assert.Equal(1, service.RepairCalls);
        Assert.True(model.Completed);
        Assert.Contains("previous-copy", model.Status);
    }

    [Fact]
    public async Task CustomDataFolderMayBeNewUnderExistingParentAndIsPassedToInstallService()
    {
        string data = Path.Combine(Path.GetTempPath(), "delta-data-test-" + Guid.NewGuid().ToString("N"));
        var service = new Service(_ => Task.CompletedTask);
        var model = Model(service);
        model.Accepted = true;
        model.UseCustomDataPath = true;
        model.DataPath = Path.Combine(data, "nested");
        Assert.False(model.CanInstall);
        model.DataPath = data;
        Assert.True(model.CanInstall);
        Directory.CreateDirectory(data);
        try
        {
            model.DataPath = data + Path.DirectorySeparatorChar;
            Assert.True(model.CanInstall);
            await model.InstallCommand.ExecuteAsync(null);
            Assert.True(model.Completed);
            Assert.Equal(model.DataPath, service.DataPath);
        }
        finally { Directory.Delete(data); }
    }

    [Fact]
    public async Task ShortcutChoicesArePassedToInstallService()
    {
        var service = new Service(_ => Task.CompletedTask);
        var model = Model(service);
        model.Accepted = true;
        model.CreateMenuShortcut = true;
        model.CreateDesktopShortcut = true;

        await model.InstallCommand.ExecuteAsync(null);

        Assert.Equal(ShortcutKinds.Menu | ShortcutKinds.Desktop, service.Shortcuts);
    }

    [Fact]
    public async Task PresetRequiresEmptySeparateDataFolderAndIsPassedToService()
    {
        string root = Directory.CreateTempSubdirectory("delta-preset-model-").FullName;
        try
        {
            var service = new Service(_ => Task.CompletedTask);
            var model = Model(service);
            model.Accepted = true;
            model.Preset = DeltaInstallPreset.Handheld;
            Assert.True(model.UseCustomDataPath);
            model.DataPath = Path.Combine(root, "data");
            Assert.True(model.CanInstall);
            Directory.CreateDirectory(model.DataPath);
            File.WriteAllText(Path.Combine(model.DataPath, "save.txt"), "existing data");
            Assert.False(model.CanInstall);
            File.Delete(Path.Combine(model.DataPath, "save.txt"));
            Assert.True(model.CanInstall);

            await model.InstallCommand.ExecuteAsync(null);
            Assert.Equal(DeltaInstallPreset.Handheld, service.Preset);
            Assert.Equal(model.DataPath, service.DataPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(DeltaInstallPreset.Desktop, "off", 1.0)]
    [InlineData(DeltaInstallPreset.Handheld, "xess", 0.75)]
    public async Task PresetSeedsOnlyEmptyFolder(DeltaInstallPreset preset, string upscaler, double renderScale)
    {
        string root = Directory.CreateTempSubdirectory("delta-preset-data-").FullName;
        string data = Path.Combine(root, "data");
        try
        {
            string config = await DeltaPresetSettings.SeedAsync(data, preset, CancellationToken.None);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(config, TestContext.Current.CancellationToken));
            Assert.Equal("vulkan", json.RootElement.GetProperty("Renderer").GetString());
            Assert.Equal(upscaler, json.RootElement.GetProperty("Upscaler").GetString());
            Assert.Equal(renderScale, json.RootElement.GetProperty("RenderScale").GetDouble(), 2);
            Assert.Equal(preset == DeltaInstallPreset.Handheld,
                json.RootElement.TryGetProperty("AvoidForcedGc", out var avoidGc) && avoidGc.GetBoolean());
            Assert.Equal(preset == DeltaInstallPreset.Handheld,
                json.RootElement.TryGetProperty("IdleThreadWait", out var idleWait) && idleWait.GetBoolean());
            Assert.Equal(preset == DeltaInstallPreset.Handheld,
                json.RootElement.TryGetProperty("HighResolutionFrameWait", out var frameWait) && frameWait.GetBoolean());
            Assert.Equal(preset == DeltaInstallPreset.Handheld,
                json.RootElement.TryGetProperty("HandheldShadowTier", out var shadowTier) && shadowTier.GetBoolean());
            Assert.Equal(preset == DeltaInstallPreset.Handheld,
                json.RootElement.TryGetProperty("GreedyMeshEnabled", out var greedyMesh) && greedyMesh.GetBoolean());
            if (preset == DeltaInstallPreset.Handheld)
            {
                Assert.Equal(1, json.RootElement.GetProperty("GreedyMeshLightTolerance").GetInt32());
                Assert.Equal(128, json.RootElement.GetProperty("GreedyMeshFarDistance").GetInt32());
                using var clientSettings = JsonDocument.Parse(await File.ReadAllTextAsync(
                    Path.Combine(data, "clientsettings.json"), TestContext.Current.CancellationToken));
                JsonElement ints = clientSettings.RootElement.GetProperty("intSettings");
                JsonElement floats = clientSettings.RootElement.GetProperty("floatSettings");
                Assert.Equal(16000, ints.GetProperty("maxAsyncQuadParticles").GetInt32());
                Assert.Equal(16000, ints.GetProperty("maxAsyncCubeParticles").GetInt32());
                Assert.Equal(60, ints.GetProperty("particleLevel").GetInt32());
                Assert.Equal(4, ints.GetProperty("mipmapLevel").GetInt32());
                Assert.Equal(2, ints.GetProperty("cloudRenderMode").GetInt32());
                Assert.Equal(160, ints.GetProperty("viewDistance").GetInt32());
                Assert.Equal(60, ints.GetProperty("maxFps").GetInt32());
                Assert.Equal(2, ints.GetProperty("vsyncMode").GetInt32());
                Assert.Equal(2, ints.GetProperty("shadowMapQuality").GetInt32());
                Assert.Equal(0.25f, floats.GetProperty("lodBias").GetSingle());
                Assert.Equal(0.55f, floats.GetProperty("lodBiasFar").GetSingle());
                Assert.True(clientSettings.RootElement.GetProperty("boolSettings")
                    .GetProperty("showMoreGfxOptions").GetBoolean());
            }
            else Assert.False(File.Exists(Path.Combine(data, "clientsettings.json")));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DeltaPresetSettings.SeedAsync(data, preset, CancellationToken.None));
            Assert.True(File.Exists(config));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancelDoesNotShowSuccess()
    {
        var model = Model(new Service(token => Task.Delay(Timeout.Infinite, token)));
        model.Accepted = true;
        var running = model.InstallCommand.ExecuteAsync(null);
        model.CancelCommand.Execute(null);
        await running;
        Assert.False(model.Completed);
        Assert.False(model.Busy);
        Assert.Contains("cancelled", model.Status);
    }

    [AvaloniaFact]
    public void ReleaseWindowRendersWithoutPrerequisiteScreen()
    {
        var model = Model(new Service(_ => Task.CompletedTask));
        var window = new DeltaInstallWindow { DataContext = model };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.GetVisualDescendants().OfType<PrerequisitesView>());
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Install");
            Assert.False(button.IsEffectivelyEnabled);
            model.Accepted = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEffectivelyEnabled);
            var presets = window.GetVisualDescendants().OfType<ComboBox>().Single();
            presets.SelectedItem = DeltaInstallPreset.Handheld;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(DeltaInstallPreset.Handheld, model.Preset);
            Assert.True(model.UseCustomDataPath);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task PayloadInventoryRejectsTraversalAndMissingRuntime()
    {
        string root = Path.Combine(Path.GetTempPath(), "delta-payload-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => DeltaReleaseService.VerifyPayloadAsync(root,
                [new("../escape", 1, new string('0', 64))], CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => DeltaReleaseService.VerifyPayloadAsync(root, [], CancellationToken.None));
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task StandaloneUninstallerMustMatchItsReleaseHash()
    {
        string root = Directory.CreateTempSubdirectory("delta-uninstaller-test-").FullName;
        try
        {
            string name = OperatingSystem.IsWindows() ? "delta-uninstaller.exe" : "delta-uninstaller";
            string path = Path.Combine(root, name);
            File.WriteAllText(path, "standalone tool");
            byte[] bytes = File.ReadAllBytes(path);
            var file = new DeltaPayloadFile(name, bytes.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            Assert.Equal(path, await DeltaReleaseService.VerifyUninstallerAsync(root, file, CancellationToken.None));
            File.AppendAllText(path, "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DeltaReleaseService.VerifyUninstallerAsync(root, file, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PayloadInventoryChecksHashesAndRejectsUnlistedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "delta-inventory-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inventory = new List<DeltaPayloadFile>();
            foreach (string relative in new[] { "Optimum.dll", "Optimum.deps.json", "Optimum.runtimeconfig.json",
                "Optimum.Bootstrap.Core.dll", "Optimum.Render.Vulkan.dll", "Optimum.Api.Contracts.dll",
                "Optimum.GameContent.dll", "shaders-vk/shaders.manifest.json", OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum",
                OperatingSystem.IsWindows() ? "SDL3.dll" : "libSDL3.so",
                "gamecontrollerdb.txt", "ControllerMappings-LICENSE.txt" })
            {
                string path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "synthetic fixture");
                inventory.Add(new(relative, new FileInfo(path).Length,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));
            }
            await DeltaReleaseService.VerifyPayloadAsync(root, inventory, CancellationToken.None);
            string sdlName = OperatingSystem.IsWindows() ? "SDL3.dll" : "libSDL3.so";
            await Assert.ThrowsAsync<InvalidDataException>(() => DeltaReleaseService.VerifyPayloadAsync(root,
                inventory.Where(file => file.Path != sdlName).ToArray(), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => DeltaReleaseService.VerifyPayloadAsync(root,
                inventory.Where(file => file.Path != "gamecontrollerdb.txt").ToArray(), CancellationToken.None));
            File.WriteAllText(Path.Combine(root, "unlisted.dll"), "extra");
            await Assert.ThrowsAsync<InvalidDataException>(() => DeltaReleaseService.VerifyPayloadAsync(root, inventory, CancellationToken.None));
            File.Delete(Path.Combine(root, "unlisted.dll"));
            File.AppendAllText(Path.Combine(root, "Optimum.dll"), "damage");
            await Assert.ThrowsAsync<InvalidDataException>(() => DeltaReleaseService.VerifyPayloadAsync(root, inventory, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ShortcutRecordsAreRemovedWithTheDeltaRuntime()
    {
        string root = Directory.CreateTempSubdirectory("delta-shortcut-test-").FullName;
        string runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, ".optimum"));
        Directory.CreateDirectory(Path.Combine(runtime, "assets"));
        File.WriteAllText(Path.Combine(runtime, "Optimum"), "launcher");
        File.WriteAllText(Path.Combine(runtime, "assets", "gameicon.png"), "icon");
        var manifest = new InstallManifest
        {
            OptimumVersion = "0.3.17",
            InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = runtime,
            Launcher = Path.Combine(runtime, "Optimum"),
            Entries = ["Optimum", "assets"],
        };
        File.WriteAllText(Path.Combine(runtime, InstallManifest.RelativePath), manifest.Serialize());
        var probe = new FakeSystemProbe { Os = OsKind.Linux, HomeDirectory = root.Replace('\\', '/') };
        probe.Environment["XDG_DATA_HOME"] = root.Replace('\\', '/') + "/xdg";
        try
        {
            DeltaReleaseService.RecordShortcuts(runtime, ShortcutKinds.Menu, probe);
            InstallManifest updated = InstallManifest.Deserialize(
                File.ReadAllText(Path.Combine(runtime, InstallManifest.RelativePath)))!;
            Assert.Contains(updated.Shortcuts, path => path.EndsWith("optimum.desktop", StringComparison.Ordinal));
            Assert.Contains(updated.Shortcuts, path => path.EndsWith("optimum.png", StringComparison.Ordinal));
            Assert.All(updated.Shortcuts, path => Assert.True(File.Exists(path)));

            Assert.True(new Uninstaller(SystemProbe.Default).Uninstall(runtime).Ok);
            Assert.False(Directory.Exists(runtime));
            Assert.All(updated.Shortcuts, path => Assert.False(File.Exists(path)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowsShortcutRecordsAreRemovedWithTheDeltaRuntime()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("delta-windows-shortcut-test-").FullName;
        string runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, ".optimum"));
        File.WriteAllText(Path.Combine(runtime, "Optimum.exe"), "launcher");
        var manifest = new InstallManifest
        {
            OptimumVersion = "0.3.17",
            InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = runtime,
            Launcher = Path.Combine(runtime, "Optimum.exe"),
            Entries = ["Optimum.exe"],
        };
        File.WriteAllText(Path.Combine(runtime, InstallManifest.RelativePath), manifest.Serialize());
        var probe = new FakeSystemProbe { Os = OsKind.Windows, HomeDirectory = root };
        probe.Environment["APPDATA"] = Path.Combine(root, "appdata");
        probe.Environment["USERPROFILE"] = root;
        try
        {
            DeltaReleaseService.RecordShortcuts(runtime, ShortcutKinds.Menu | ShortcutKinds.Desktop, probe);
            InstallManifest updated = InstallManifest.Deserialize(
                File.ReadAllText(Path.Combine(runtime, InstallManifest.RelativePath)))!;
            Assert.Equal(2, updated.Shortcuts.Count);
            Assert.All(updated.Shortcuts, path => Assert.True(File.Exists(path)));

            Assert.True(new Uninstaller(SystemProbe.Default).Uninstall(runtime).Ok);
            Assert.False(Directory.Exists(runtime));
            Assert.All(updated.Shortcuts, path => Assert.False(File.Exists(path)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LinuxUninstallEntryUsesAnExternalToolAndIsRemovedWithTheRuntime()
    {
        string root = Directory.CreateTempSubdirectory("delta-linux-uninstall-test-").FullName;
        string runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, ".optimum"));
        File.WriteAllText(Path.Combine(runtime, "Optimum"), "launcher");
        string source = Path.Combine(root, "published-uninstaller");
        File.WriteAllText(source, "standalone uninstaller");
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
        var probe = new FakeSystemProbe { Os = OsKind.Linux, HomeDirectory = root };
        probe.Environment["XDG_DATA_HOME"] = Path.Combine(root, "xdg");
        var manifest = new InstallManifest
        {
            OptimumVersion = "0.3.17", InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = runtime, Entries = ["Optimum"],
        };
        File.WriteAllText(Path.Combine(runtime, InstallManifest.RelativePath), manifest.Serialize());
        try
        {
            string tool = DeltaReleaseService.PrepareLinuxUninstaller(source, digest, probe);
            Assert.True(File.Exists(tool));
            Assert.False(tool.StartsWith(runtime + Path.DirectorySeparatorChar, StringComparison.Ordinal));

            DeltaReleaseService.RecordLinuxUninstallEntry(runtime, tool, probe);
            InstallManifest recorded = InstallManifest.Deserialize(
                File.ReadAllText(Path.Combine(runtime, InstallManifest.RelativePath)))!;
            string entry = Assert.Single(recorded.Shortcuts);
            string text = File.ReadAllText(entry);
            Assert.Contains("Name=Uninstall Optimum", text);
            Assert.Contains("uninstall-delta --install-dir", text);
            Assert.Contains("--confirm", text);
            Assert.Contains("Terminal=true", text);

            Assert.True(new Uninstaller(SystemProbe.Default).Uninstall(runtime).Ok);
            Assert.False(Directory.Exists(runtime));
            Assert.False(File.Exists(entry));
            Assert.True(File.Exists(tool));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
